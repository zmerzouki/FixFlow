using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models;
using QuickFix;
using QuickFix.Fields;
using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Email;
using FixFlow.TradeAllocBridge.Core.Excel;
using FixFlow.TradeAllocBridge.Core.Fix;
using FixFlow.TradeAllocBridge.Core.Logging;
using FixFlow.TradeAllocBridge.Core.Mapping;
using FixFlow.TradeAllocBridge.Core.Reporting;
using Serilog;
using FixMessage = QuickFix.Message;
using FixFlow.TradeAllocBridge.CLI.Services;
using System.IO;

namespace FixFlow.TradeAllocBridge.CLI
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (Environment.UserInteractive)
            {
                Console.Title = "FixFlowService";
            }

            if (args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  TradeAllocBridge process-now [--dry-run]");
                Console.WriteLine("  TradeAllocBridge run [--dry-run] [--interval-seconds N]");
                Console.WriteLine();
                Console.WriteLine("Description:");
                Console.WriteLine("  FixFlowService processes mailbox allocations, builds FIX 4.2 messages, and optionally sends them.");
                Console.WriteLine("  Use --dry-run to validate and log FIX messages without transmitting them.");
                return 0;
            }

            var command = args[0].Trim().ToLowerInvariant();
            if (command != "process-now" && command != "run" && command != "watch")
            {
                Console.WriteLine($"Unknown command: {args[0]}");
                Console.WriteLine("Use 'help' to see available commands.");
                return 1;
            }

            bool dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
            int intervalSeconds = ParseIntervalSeconds(args, defaultSeconds: 60);

            try
            {
                var builder = Host.CreateApplicationBuilder(args);
                if (OperatingSystem.IsWindows())
                {
                    builder.Services.AddWindowsService();
                }
                if (OperatingSystem.IsLinux())
                {
                    builder.Services.AddSystemd();
                }
                builder.Configuration.AddJsonFile(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    optional: false,
                    reloadOnChange: true);
                var sharedSettingsPath = SharedConfigResolver.ResolveSharedAppSettingsPath(AppContext.BaseDirectory);
                if (!string.IsNullOrWhiteSpace(sharedSettingsPath))
                {
                    builder.Configuration.AddJsonFile(sharedSettingsPath, optional: true, reloadOnChange: true);
                }

                var config = builder.Configuration.Get<AppConfig>()!;
                LogService.Configure(config.Logging);

                ConfigureServices(builder, config);

                if (command == "run" || command == "watch")
                {
                    builder.Services.AddHostedService(sp =>
                        new AllocationProcessingWorker(sp, intervalSeconds, dryRun));
                }

                using var app = builder.Build();

                SetupMappingWatcher(app);

                if (command == "run" || command == "watch")
                {
                    Console.WriteLine("Starting FixFlowService in hosted mode.");
                    Console.WriteLine($"Polling interval: {intervalSeconds}s");
                    Console.WriteLine(dryRun
                        ? "Running in DRY-RUN (validation-only) mode - no messages will be sent."
                        : "Running in LIVE mode.");
                    await app.RunAsync();
                    return 0;
                }

                await ProcessOnceAsync(app.Services, dryRun, keepEngineRunning: false, CancellationToken.None);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fatal error: {ex.Message}");
                Log.Error(ex, "Unhandled exception in FixFlowService");
                return -1;
            }
        }

        private static void ConfigureServices(HostApplicationBuilder builder, AppConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.Fix.SessionQualifier))
            {
                config.Fix.SessionQualifier = builder.Configuration["FixSessionQualifiers:Service"] ?? "FIXFLOWSERVICE";
            }

            builder.Services.AddSingleton(config);
            builder.Services.AddSingleton(config.Email);
            builder.Services.AddSingleton(config.Fix);
            builder.Services.AddSingleton(config.Logging);

            builder.Services.AddSingleton<IAllocationEmailService, GraphEmailService>();
            builder.Services.AddSingleton<ITradeFileParser, ExcelParser>();
            builder.Services.AddSingleton<FixApp>();
            builder.Services.AddSingleton<IFixSessionEngine, FixEngine>();
            builder.Services.AddSingleton<IFixMessageClient, FixClient>();
            builder.Services.AddSingleton<ValidationReport>();

            // Prepare canonical paths used by the service runtime.
            var activeConfigs = ResolveServiceConfigsDir();
            var incoming = Path.Combine(activeConfigs, "incoming");

            // Register FixMappingRepository for the active configs directory.
            builder.Services.AddSingleton<FixMappingRepository>(sp =>
            {
                var loggerRepo = sp.GetRequiredService<ILogger<FixMappingRepository>>();
                return new FixMappingRepository(activeConfigs, loggerRepo);
            });

            // Register the MappingStagingWatcher so FixFlowService watches configs/incoming.
            builder.Services.AddSingleton(sp =>
                new MappingStagingWatcher(incoming, activeConfigs, sp.GetRequiredService<ILogger<MappingStagingWatcher>>())
            );
        }

        private static string ResolveServiceConfigsDir()
        {
            const string envVar = "FIXFLOW_CLI_CONFIGS";
            try
            {
                var env = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrWhiteSpace(env))
                {
                    var candidate = env;
                    if (!Path.IsPathRooted(candidate))
                    {
                        candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, candidate));
                    }
                    else
                    {
                        candidate = Path.GetFullPath(candidate);
                    }

                    if (Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                // Ignore env lookup errors and fall back.
            }

            return Path.Combine(AppContext.BaseDirectory, "configs");
        }

        private static void SetupMappingWatcher(IHost app)
        {
            // Start the watcher (process existing staged files and assign callback)
            var watcher = app.Services.GetRequiredService<MappingStagingWatcher>();
            var mappingRepo = app.Services.GetRequiredService<FixMappingRepository>();
            try
            {
                watcher.EnqueueExisting();

                // When a mapping is successfully moved into the active configs folder,
                // notify the repository so in-memory consumers can reload.
                watcher.OnMappingDeployed = deployedPath =>
                {
                    try
                    {
                        var logger = app.Services.GetRequiredService<ILogger<Program>>();
                        logger.LogInformation("Watcher moved mapping into active folder: {Path}", deployedPath);

                        // Notify repository that mappings changed
                        mappingRepo.NotifyMappingsChanged();

                        logger.LogInformation("Notified FixMappingRepository of mapping changes.");
                    }
                    catch (Exception ex)
                    {
                        var logger = app.Services.GetRequiredService<ILogger<Program>>();
                        logger.LogWarning(ex, "Failed to notify mapping repository after deploy");
                    }
                };
            }
            catch (Exception ex)
            {
                // Do not block startup on watcher issues; just log
                var logger = app.Services.GetRequiredService<ILogger<Program>>();
                logger.LogWarning(ex, "Failed to initialize mapping staging watcher");
            }
        }

        public static async Task ProcessOnceAsync(IServiceProvider services, bool dryRun, bool keepEngineRunning, CancellationToken cancellationToken)
        {
            var config = services.GetRequiredService<AppConfig>();
            var fixEngine = services.GetRequiredService<IFixSessionEngine>();
            var emailService = services.GetRequiredService<IAllocationEmailService>();
            var excelParser = services.GetRequiredService<ITradeFileParser>();
            var fixClient = services.GetRequiredService<IFixMessageClient>();
            var report = services.GetRequiredService<ValidationReport>();
            var fixApp = services.GetRequiredService<FixApp>();
            if (fixEngine is FixEngine concreteFixEngine)
            {
                fixApp.Engine = concreteFixEngine;
            }

            Console.WriteLine(dryRun
                ? "Running in DRY-RUN (validation-only) mode - no messages will be sent."
                : "Starting allocation processing...");

            var emails = await emailService.FetchNewEmailsAsync();
            Console.WriteLine($"Fetched {emails.Count} email(s) with attachments.");

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // -------------------------------------------------------------------
            // Dynamic FIX Session Configuration (from all mappings)
            // -------------------------------------------------------------------
            var allMappings = new List<(string Sender, string Target, string? SenderSubId, string? TargetSubId)>();

            foreach (var email in emails)
            {
                if (string.IsNullOrEmpty(email.MapPath) || !File.Exists(email.MapPath))
                {
                    Console.WriteLine($"No mapping found for client {email.ClientId} (sender {email.SenderEmail}). Skipping.");
                    continue;
                }

                try
                {
                    var mapping = MappingConfig.Load(email.MapPath);
                    string senderComp = ResolveSenderCompId(mapping, config.Fix);
                    string targetComp = ResolveTargetCompId(mapping, config.Fix);
                    var senderSubId = mapping.Predefined?.SenderSubID;
                    var targetSubId = mapping.Predefined?.TargetSubID;
                    allMappings.Add((senderComp, targetComp, senderSubId, targetSubId));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load mapping file {email.MapPath}: {ex.Message}");
                }
            }

            if (allMappings.Count > 0)
            {
                fixEngine.AppendSessionsIfMissing(allMappings);

                if (dryRun)
                {
                    fixEngine.ReloadSettings(fixApp);
                }
                else if (!keepEngineRunning || !fixEngine.IsStarted)
                {
                    fixEngine.ReloadSettings(fixApp);
                    fixEngine.Start();
                }
            }
            else
            {
                Console.WriteLine("No valid mappings found - skipping FIX session initialization.");
            }

            // -------------------------------------------------------------------
            // Process emails (main allocation loop)
            // -------------------------------------------------------------------
            foreach (var email in emails)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (string.IsNullOrEmpty(email.MapPath) || !File.Exists(email.MapPath))
                    continue;

                MappingConfig mapping;
                try
                {
                    mapping = MappingConfig.Load(email.MapPath);
                    Console.WriteLine($"Loaded mapping for client {email.ClientId} from {email.MapPath}");
                }
                catch (Exception exMap)
                {
                    Console.WriteLine($"Failed to load mapping for client {email.ClientId}: {exMap.Message}");
                    continue;
                }

                var fixBuilderLogger = services.GetRequiredService<ILogger<FixMessageBuilder>>();
                var fixBuilder = new FixMessageBuilder(mapping, config.Fix, fixBuilderLogger);

                foreach (var attachmentPath in email.Attachments)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (!File.Exists(attachmentPath))
                    {
                        Console.WriteLine($"Attachment not found: {attachmentPath}");
                        continue;
                    }

                    Console.WriteLine($"Processing file: {Path.GetFileName(attachmentPath)}");
                    var trades = excelParser.Parse(attachmentPath, mapping);
                    var totalTrades = trades.Count;
                    // -------------------------------------------------------------------
                    // Group allocations by required fields (54,55,79,80,153)
                    // -------------------------------------------------------------------
                    var symbolColumn = mapping.TradeAllocations.FirstOrDefault(kvp => kvp.Value == "55").Key ?? "SYMBOL";
                    var sideColumn = mapping.TradeAllocations.FirstOrDefault(kvp => kvp.Value == "54").Key ?? "SIDE";
                    var accountColumn = mapping.TradeAllocations.FirstOrDefault(kvp => kvp.Value == "79").Key ?? "ALLOC ACCOUNT";
                    var qtyColumn = mapping.TradeAllocations.FirstOrDefault(kvp => kvp.Value == "80").Key ?? "QUANTITY";
                    var priceColumn = mapping.TradeAllocations.FirstOrDefault(kvp => kvp.Value == "153").Key ?? "PRICE";
                    var tradeDateColumn = mapping.TradeAllocations.FirstOrDefault(kvp => kvp.Value == "75").Key ?? "TRADEDATE";

                    bool IsMissing(TradeRecord t, string column) =>
                        !t.Fields.TryGetValue(column, out var val) || string.IsNullOrWhiteSpace(val?.Trim());

                    static string NormalizeTradeDate(string? raw)
                    {
                        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
                        var trimmed = raw.Trim();
                        if (trimmed.Length == 8 && trimmed.All(char.IsDigit))
                        {
                            return trimmed;
                        }

                        if (DateTime.TryParse(trimmed, out var parsed))
                        {
                            return parsed.ToString("yyyyMMdd");
                        }

                        return string.Empty;
                    }

                    static string ResolveTradeDate(IEnumerable<TradeRecord> groupTrades, string tradeDateColumnName)
                    {
                        foreach (var trade in groupTrades)
                        {
                            if (trade.Fields.TryGetValue(tradeDateColumnName, out var raw))
                            {
                                var normalized = NormalizeTradeDate(raw);
                                if (!string.IsNullOrEmpty(normalized))
                                {
                                    return normalized;
                                }
                            }
                        }

                        return string.Empty;
                    }

                    static string FormatAllocId(string allocId, string tradeDate)
                    {
                        return string.IsNullOrWhiteSpace(tradeDate) ? allocId : $"{allocId}_{tradeDate}";
                    }

                    var missingTrades = trades
                        .Where(t =>
                            IsMissing(t, symbolColumn) ||
                            IsMissing(t, sideColumn) ||
                            IsMissing(t, accountColumn) ||
                            IsMissing(t, qtyColumn) ||
                            IsMissing(t, priceColumn))
                        .ToList();

                    var missingTags = new List<string>();
                    if (missingTrades.Any(t => IsMissing(t, sideColumn))) missingTags.Add("Side (tag 54)");
                    if (missingTrades.Any(t => IsMissing(t, symbolColumn))) missingTags.Add("Symbol (tag 55)");
                    if (missingTrades.Any(t => IsMissing(t, accountColumn))) missingTags.Add("AllocAccount (tag 79)");
                    if (missingTrades.Any(t => IsMissing(t, qtyColumn))) missingTags.Add("AllocShares (tag 80)");
                    if (missingTrades.Any(t => IsMissing(t, priceColumn))) missingTags.Add("AllocAvgPx (tag 153)");

                    if (missingTrades.Any())
                    {
                        Console.WriteLine($"{missingTrades.Count} out of {totalTrades} Allocation(s) failed to process. Missing required value(s): {string.Join(", ", missingTags)}");
                    }

                    var disableAllocationMerge = mapping.DisableAllocationMerge;
                    string ResolveGroupKey(TradeRecord trade)
                    {
                        if (disableAllocationMerge)
                        {
                            return $"ROW:{trade.Id}";
                        }

                        var sideValue = trade.Fields.TryGetValue(sideColumn, out var side) ? side.Trim() : string.Empty;
                        var symbolValue = trade.Fields.TryGetValue(symbolColumn, out var symbol) ? symbol.Trim() : string.Empty;
                        return $"{symbolValue}|{sideValue}";
                    }

                    string ResolveSymbolValue(TradeRecord trade) =>
                        trade.Fields.TryGetValue(symbolColumn, out var symbolValue) ? symbolValue.Trim() : string.Empty;

                    string ResolveSideValue(TradeRecord trade) =>
                        trade.Fields.TryGetValue(sideColumn, out var sideValue) ? sideValue.Trim() : string.Empty;

                    string BuildMergeFailureMessage(int groupTotal, string side, string symbol, IEnumerable<string> tags)
                    {
                        if (disableAllocationMerge)
                        {
                            return $"{groupTotal} allocation(s) failed to process. Missing required value(s): {string.Join(", ", tags)}.";
                        }

                        return $"{groupTotal} out of {totalTrades} allocations identified for group trade {side}/{symbol} failed to merge because it is missing required value(s): {string.Join(", ", tags)}. Allocations processing cancelled.";
                    }

                    string BuildSkippedSendMessage(int groupTotal, string side, string symbol)
                    {
                        if (disableAllocationMerge)
                        {
                            return $"Allocation {side}/{symbol} was built but cannot be sent because other allocations have error(s). Allocations processing cancelled.";
                        }

                        var failedAllocationsCount = totalTrades - groupTotal;
                        return $" {groupTotal} out of {totalTrades} Allocations identified for group trade {side}/{symbol} were successfully merged. Fix message cannot be sent because {failedAllocationsCount} trades have error(s). Allocations processing cancelled.";
                    }

                    var groupedBySymbolAndSide = trades
                        .Except(missingTrades)
                        .GroupBy(ResolveGroupKey)
                        .ToList();

                    if (groupedBySymbolAndSide.Count == 0)
                    {
                        foreach (var trade in missingTrades)
                        {
                            var missingTradeTags = new List<string>();
                            if (IsMissing(trade, sideColumn)) missingTradeTags.Add("Side (tag 54)");
                            if (IsMissing(trade, symbolColumn)) missingTradeTags.Add("Symbol (tag 55)");
                            if (IsMissing(trade, accountColumn)) missingTradeTags.Add("AllocAccount (tag 79)");
                            if (IsMissing(trade, qtyColumn)) missingTradeTags.Add("AllocShares (tag 80)");
                            if (IsMissing(trade, priceColumn)) missingTradeTags.Add("AllocAvgPx (tag 153)");

                            report.Add(new ValidationResult(
                                DateTime.UtcNow.ToString("o"),
                                mapping.ClientId ?? string.Empty,
                                FormatAllocId($"MISSING_{trade.Id}", ResolveTradeDate(new[] { trade }, tradeDateColumn)),
                                FixValueNormalizer.FormatSideDisplay(ResolveSideValue(trade)),
                                ResolveSymbolValue(trade),
                                "Failed",
                                $"Missing required value(s): {string.Join(", ", missingTradeTags)}.",
                                string.Empty,
                                "Email",
                                dryRun,
                                1,
                                ValidationMetrics.CalculateGrossAmount(trade, qtyColumn, priceColumn)
                            ));
                        }

                        report.Save();
                        Console.WriteLine("No valid allocations to process after required field validation.");
                        continue;
                    }

                    Console.WriteLine(disableAllocationMerge
                        ? $"Found {groupedBySymbolAndSide.Count} allocation(s) to process individually (merge disabled)."
                        : $"Found {groupedBySymbolAndSide.Count} trade group(s) by symbol/side.");

                    // -------------------------------------------------------------------
                    // Build and send one AllocationInstruction (35=J) per symbol
                    // -------------------------------------------------------------------
                    var reportEntries = new List<ValidationResult>();
                    var groupErrors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    var groupTotals = trades
                        .GroupBy(ResolveGroupKey)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                    var groupMeta = trades
                        .GroupBy(ResolveGroupKey)
                        .ToDictionary(
                            g => g.Key,
                            g =>
                            {
                                var first = g.First();
                                return new
                                {
                                    Side = ResolveSideValue(first),
                                    Symbol = ResolveSymbolValue(first)
                                };
                            },
                            StringComparer.OrdinalIgnoreCase);
                    var missingTagsByGroup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                    void AddGroupError(string groupKey, string error)
                    {
                        if (string.IsNullOrWhiteSpace(error)) return;
                        if (!groupErrors.TryGetValue(groupKey, out var errors))
                        {
                            errors = new List<string>();
                            groupErrors[groupKey] = errors;
                        }
                        errors.Add(error);
                    }

                    foreach (var trade in missingTrades)
                    {
                        var key = ResolveGroupKey(trade);

                        if (!missingTagsByGroup.TryGetValue(key, out var tags))
                        {
                            tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            missingTagsByGroup[key] = tags;
                        }

                        if (IsMissing(trade, sideColumn)) tags.Add("Side (tag 54)");
                        if (IsMissing(trade, symbolColumn)) tags.Add("Symbol (tag 55)");
                        if (IsMissing(trade, accountColumn)) tags.Add("AllocAccount (tag 79)");
                        if (IsMissing(trade, qtyColumn)) tags.Add("AllocShares (tag 80)");
                        if (IsMissing(trade, priceColumn)) tags.Add("AllocAvgPx (tag 153)");
                    }

                    foreach (var kvp in missingTagsByGroup)
                    {
                        var groupTotal = groupTotals.TryGetValue(kvp.Key, out var count) ? count : 0;
                        var meta = groupMeta.TryGetValue(kvp.Key, out var details)
                            ? details
                            : new { Side = string.Empty, Symbol = string.Empty };
                        var message = BuildMergeFailureMessage(groupTotal, meta.Side, meta.Symbol, kvp.Value);
                        AddGroupError(kvp.Key, message);
                    }

                    foreach (var trade in missingTrades)
                    {
                        var missingTradeTags = new List<string>();
                        if (IsMissing(trade, sideColumn)) missingTradeTags.Add("Side (tag 54)");
                        if (IsMissing(trade, symbolColumn)) missingTradeTags.Add("Symbol (tag 55)");
                        if (IsMissing(trade, accountColumn)) missingTradeTags.Add("AllocAccount (tag 79)");
                        if (IsMissing(trade, qtyColumn)) missingTradeTags.Add("AllocShares (tag 80)");
                        if (IsMissing(trade, priceColumn)) missingTradeTags.Add("AllocAvgPx (tag 153)");

                        reportEntries.Add(new ValidationResult(
                            DateTime.UtcNow.ToString("o"),
                            mapping.ClientId ?? string.Empty,
                            FormatAllocId($"MISSING_{trade.Id}", ResolveTradeDate(new[] { trade }, tradeDateColumn)),
                            ResolveSideValue(trade),
                            ResolveSymbolValue(trade),
                            "Failed",
                            $"Missing required value(s): {string.Join(", ", missingTradeTags)}.",
                            string.Empty,
                            "Email",
                            dryRun,
                            1,
                            ValidationMetrics.CalculateGrossAmount(trade, qtyColumn, priceColumn)
                        ));
                    }

                    var preparedMessages = new List<(FixMessage Message, string AllocId, string Symbol, string Side, string GroupKey, int TradeCount, IEnumerable<TradeRecord> Trades)>();
                    int buildFailures = 0;
                    var allocIdToGroupKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var symbolGroup in groupedBySymbolAndSide)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        var groupKey = symbolGroup.Key;
                        var meta = groupMeta.TryGetValue(groupKey, out var details)
                            ? details
                            : new { Side = string.Empty, Symbol = string.Empty };
                        string symbol = meta.Symbol;
                        string side = meta.Side;
                        var allocId = fixBuilder.NextAllocId();
                        allocIdToGroupKey[allocId] = groupKey;
                        var groupTradeDate = ResolveTradeDate(symbolGroup, tradeDateColumn);
                        var reportAllocId = FormatAllocId(allocId, groupTradeDate);
                        string senderComp = ResolveSenderCompId(mapping, config.Fix);
                        string targetComp = ResolveTargetCompId(mapping, config.Fix);

                        FixMessage mergedMsg;
                        try
                        {
                            mergedMsg = fixBuilder.BuildFromAllocGroup(allocId, senderComp, targetComp, symbolGroup, mapping);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to build FIX allocation for symbol '{symbol}' / side '{side}': {ex.Message}");
                            reportEntries.Add(new ValidationResult(
                                DateTime.UtcNow.ToString("o"),
                                mapping.ClientId ?? string.Empty,
                                reportAllocId,
                                side,
                                symbol,
                                "Failed",
                                string.Empty,
                                string.Empty,
                                "Email",
                                dryRun,
                                symbolGroup.Count(),
                                ValidationMetrics.CalculateGrossAmount(symbolGroup, qtyColumn, priceColumn)
                            ));
                            AddGroupError(groupKey, ex.Message);
                            buildFailures++;
                            continue;
                        }

                        preparedMessages.Add((mergedMsg, allocId, symbol, side, groupKey, symbolGroup.Count(), symbolGroup));
                    }

                    bool canSendFixMessages = !missingTrades.Any() && buildFailures == 0;
                    if (!canSendFixMessages)
                    {
                        Console.WriteLine(disableAllocationMerge
                            ? "Allocations processing cancelled. One or more allocations failed to process."
                            : "Allocations processing cancelled. One or more merged allocations failed to process.");
                    }

                    var senderCompId = ResolveSenderCompId(mapping, config.Fix);
                    var targetCompId = ResolveTargetCompId(mapping, config.Fix);
                    var senderSubId = mapping.Predefined?.SenderSubID;
                    var targetSubId = mapping.Predefined?.TargetSubID;
                    foreach (var prepared in preparedMessages)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        var mergedMsg = prepared.Message;
                        var allocId = prepared.AllocId;
                        var symbol = prepared.Symbol;
                        var side = prepared.Side;
                        var groupKey = prepared.GroupKey;
                        var tradeCount = prepared.TradeCount;
                        var groupTradeDate = ResolveTradeDate(prepared.Trades, tradeDateColumn);

                        string status;
                        string errorMessage;

                        if (!canSendFixMessages)
                        {
                            status = "Failed";
                            errorMessage = "Skipped";
                            if (!groupErrors.ContainsKey(groupKey))
                            {
                                var groupTotal = groupTotals.TryGetValue(groupKey, out var count) ? count : tradeCount;
                                var cancelMessage = BuildSkippedSendMessage(groupTotal, side, symbol);
                                AddGroupError(groupKey, cancelMessage);
                            }
                        }
                        else
                        {
                            // Retrieve the correct session ID from the FIX engine
                            SessionID? sessionID = null;
                            if (fixEngine.SessionSettings != null)
                            {
                                foreach (var sid in fixEngine.SessionSettings.GetSessions())
                                {
                                    if (string.Equals(sid.SenderCompID, senderCompId, StringComparison.OrdinalIgnoreCase) &&
                                        string.Equals(sid.TargetCompID, targetCompId, StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (!string.IsNullOrWhiteSpace(senderSubId) &&
                                            !string.Equals(sid.SenderSubID, senderSubId, StringComparison.OrdinalIgnoreCase))
                                        {
                                            continue;
                                        }
                                        if (!string.IsNullOrWhiteSpace(targetSubId) &&
                                            !string.Equals(sid.TargetSubID, targetSubId, StringComparison.OrdinalIgnoreCase))
                                        {
                                            continue;
                                        }
                                        sessionID = sid;
                                        break;
                                    }
                                }
                            }

                            if (sessionID is null)
                            {
                                status = "Failed";
                                errorMessage = BuildSessionMissingMessage(senderCompId, targetCompId, senderSubId, targetSubId);
                                AddGroupError(groupKey, errorMessage);
                            }
                            else
                            {
                                var sendResult = dryRun ? "OK" : await fixClient.SendAsync(mergedMsg, sessionID);
                                status = sendResult == "OK" ? "Sent" : "Failed";
                                errorMessage = sendResult == "OK" ? string.Empty : sendResult;
                                if (sendResult != "OK")
                                {
                                    AddGroupError(groupKey, sendResult);
                                }
                            }
                        }

                        Console.WriteLine($"AllocID={allocId} ({side}/{symbol})  {status}");
                        if (disableAllocationMerge)
                        {
                            fixBuilderLogger.LogInformation(
                                "Sent allocation {AllocId} for {Side} of {Symbol} security: {Status}",
                                allocId, side, symbol, status);
                        }
                        else
                        {
                            fixBuilderLogger.LogInformation(
                                "Sent merged allocation {AllocId} for {Side} of {Symbol} security ({Count} trades): {Status}",
                                allocId, side, symbol, tradeCount, status);
                        }

                        string rawFix = mergedMsg.ToString().Replace('\u0001', '|').Replace("\r", "").Replace("\n", "");
                        reportEntries.Add(new ValidationResult(
                            DateTime.UtcNow.ToString("o"),
                            mapping.ClientId ?? string.Empty,
                            FormatAllocId(allocId,
                                mergedMsg.IsSetField(75)
                                    ? NormalizeTradeDate(mergedMsg.GetString(75))
                                    : groupTradeDate),
                            side,
                            symbol,
                            status,
                            string.Empty,
                            rawFix,
                            "Email",
                            dryRun,
                            tradeCount,
                            ValidationMetrics.CalculateGrossAmount(prepared.Trades, qtyColumn, priceColumn)
                        ));
                    }

                    if (reportEntries.Count > 0)
                    {
                        var uniqueEntries = new List<ValidationResult>();
                        var entryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var entry in reportEntries)
                        {
                            var key = string.Join("|", new[]
                            {
                                entry.ClientId,
                                entry.AllocID,
                                entry.Side,
                                entry.Symbol,
                                entry.ProcessingStatus,
                                entry.RawFixMessage
                            });
                            if (entryKeys.Add(key))
                            {
                                uniqueEntries.Add(entry);
                            }
                        }

                        foreach (var entry in uniqueEntries)
                        {
                            string errorDetails = string.Empty;
                            if (groupErrors.Count > 0)
                            {
                                var currentKey = allocIdToGroupKey.TryGetValue(entry.AllocID, out var mappedKey)
                                    ? mappedKey
                                    : $"{entry.Symbol}|{entry.Side}";
                                if (groupErrors.TryGetValue(currentKey, out var errors))
                                {
                                    var details = errors
                                        .Distinct()
                                        .ToList();
                                    if (details.Count > 0)
                                    {
                                        errorDetails = string.Join(" | ", details);
                                    }
                                }
                            }

                            if (string.IsNullOrWhiteSpace(errorDetails))
                            {
                                errorDetails = entry.ErrorDetails;
                            }

                            var sideDisplay = FixValueNormalizer.FormatSideDisplay(entry.Side);
                            report.Add(entry with { Side = sideDisplay, ErrorDetails = errorDetails });
                        }
                    }
                }
            }

            // -------------------------------------------------------------------
            // Wrap up
            // -------------------------------------------------------------------
            report.Save();
            Console.WriteLine("Processing complete.");

            if (!dryRun && !keepEngineRunning)
            {
                fixEngine.Stop();
                Log.Information("FIX engine stopped cleanly.");
            }
        }

        private static int ParseIntervalSeconds(string[] args, int defaultSeconds)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!args[i].Equals("--interval-seconds", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (int.TryParse(args[i + 1], out var parsed) && parsed > 0)
                {
                    return parsed;
                }
            }

            return defaultSeconds;
        }

        private static string BuildSessionMissingMessage(
            string senderComp,
            string targetComp,
            string? senderSubId,
            string? targetSubId)
        {
            var parts = new List<string> { $"SenderCompID={senderComp}, TargetCompID={targetComp}" };
            if (!string.IsNullOrWhiteSpace(senderSubId))
            {
                parts.Add($"SenderSubID={senderSubId}");
            }
            if (!string.IsNullOrWhiteSpace(targetSubId))
            {
                parts.Add($"TargetSubID={targetSubId}");
            }

            return "No FIX session found for " + string.Join(", ", parts) + ".";
        }

        private static string ResolveSenderCompId(MappingConfig mapping, FixConfig fixConfig)
        {
            var senderCompId = FirstNonEmpty(mapping.Predefined?.SenderCompID, mapping.ClientId, fixConfig.SenderCompId);
            if (string.IsNullOrWhiteSpace(senderCompId))
            {
                throw new InvalidOperationException("SenderCompID is not configured for the mapping or FixFlowService.");
            }

            return senderCompId.Trim();
        }

        private static string ResolveTargetCompId(MappingConfig mapping, FixConfig fixConfig)
        {
            var targetCompId = FirstNonEmpty(mapping.Predefined?.TargetCompID, fixConfig.TargetCompId);
            if (string.IsNullOrWhiteSpace(targetCompId))
            {
                throw new InvalidOperationException("TargetCompID is not configured for the mapping or FixFlowService.");
            }

            return targetCompId.Trim();
        }

        private static string? FirstNonEmpty(params string?[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

    }
}


