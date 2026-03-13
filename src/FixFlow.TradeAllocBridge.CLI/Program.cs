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
                Console.Title = "TradeAllocBridge CLI";
            }

            if (args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  TradeAllocBridge process-now [--dry-run]");
                Console.WriteLine("  TradeAllocBridge run [--dry-run] [--interval-seconds N]");
                Console.WriteLine();
                Console.WriteLine("Description:");
                Console.WriteLine("  Processes mailbox allocations, builds FIX 4.2 messages, and optionally sends them.");
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
                    Console.WriteLine("Starting TradeAllocBridge CLI in hosted mode.");
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
                Log.Error(ex, "Unhandled exception in TradeAllocBridge CLI");
                return -1;
            }
        }

        private static void ConfigureServices(HostApplicationBuilder builder, AppConfig config)
        {
            builder.Services.AddSingleton(config);
            builder.Services.AddSingleton(config.Email);
            builder.Services.AddSingleton(config.Fix);
            builder.Services.AddSingleton(config.Logging);

            builder.Services.AddSingleton<GraphEmailService>();
            builder.Services.AddSingleton<ExcelParser>();
            builder.Services.AddSingleton<FixApp>();
            builder.Services.AddSingleton<FixEngine>();
            builder.Services.AddSingleton<FixClient>();
            builder.Services.AddSingleton<ValidationReport>();

            // Prepare canonical paths used by CLI
            var activeConfigs = ResolveCliConfigsDir();
            var incoming = Path.Combine(activeConfigs, "incoming");

            // Register FixMappingRepository for the CLI active configs directory
            builder.Services.AddSingleton<FixMappingRepository>(sp =>
            {
                var loggerRepo = sp.GetRequiredService<ILogger<FixMappingRepository>>();
                return new FixMappingRepository(activeConfigs, loggerRepo);
            });

            // Register the MappingStagingWatcher so CLI watches configs/incoming
            builder.Services.AddSingleton(sp =>
                new MappingStagingWatcher(incoming, activeConfigs, sp.GetRequiredService<ILogger<MappingStagingWatcher>>())
            );
        }

        private static string ResolveCliConfigsDir()
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
            var fixEngine = services.GetRequiredService<FixEngine>();
            var emailService = services.GetRequiredService<GraphEmailService>();
            var excelParser = services.GetRequiredService<ExcelParser>();
            var fixClient = services.GetRequiredService<FixClient>();
            var report = services.GetRequiredService<ValidationReport>();
            var fixApp = services.GetRequiredService<FixApp>();
            fixApp.Engine = fixEngine;

            Console.WriteLine("===> FIX XML Group Definition Present? " +
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.xml"))
                    .Contains("NoAllocs"));

            // Manual verification of dictionary load
            try
            {
                var dictPath = Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.xml");
                var dd = new QuickFix.DataDictionary.DataDictionary(dictPath);
                Console.WriteLine($" Dictionary loaded successfully: {dictPath}");
                Console.WriteLine($"   Fields: {dd.FieldsByTag.Count}, Messages: {dd.Messages.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load FIX dictionary manually: {ex.Message}");
            }

            Console.WriteLine(dryRun
                ? "Running in DRY-RUN (validation-only) mode - no messages will be sent."
                : "Starting manual trade allocation processing...");

            // -------------------------------------------------------------------
            // Fetch emails or use local test data
            // -------------------------------------------------------------------
            List<AllocationEmail> emails;
            if (dryRun)
            {
                Console.WriteLine("Dry-run mode: using local test spreadsheet instead of mailbox.");

                string configDir;
                var probePaths = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "configs"),
                    Path.Combine(Directory.GetCurrentDirectory(), "configs"),
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "configs")),
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "configs"))
                };
                configDir = probePaths.FirstOrDefault(Directory.Exists)
                            ?? Path.Combine(AppContext.BaseDirectory, "configs");

                var mapPath = Path.Combine(configDir, "RAJA_map.json");

                emails = new List<AllocationEmail>
                {
                    new AllocationEmail
                    {
                        Subject = "Local Test Allocation",
                        SenderEmail = "test@test.local",
                        ClientId = "RAJA",
                        MapPath = mapPath,
                        Attachments = new List<string>
                        {
                            Path.Combine(AppContext.BaseDirectory, "Allocations_Multiple.csv")
                        }
                    }
                };
            }
            else
            {
                emails = await emailService.FetchNewEmailsAsync();
                Console.WriteLine($"Fetched {emails.Count} email(s) with attachments.");
            }

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
                    string senderComp = mapping.Predefined?.SenderCompID ?? mapping.ClientId ?? "DEFAULTSENDER";
                    string targetComp = mapping.Predefined?.TargetCompID ?? "RJSYN";
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

                    var groupedBySymbolAndSide = trades
                        .Except(missingTrades)
                        .GroupBy(t => new
                        {
                            Symbol = t.Fields.TryGetValue(symbolColumn, out var symbolValue) ? symbolValue.Trim() : string.Empty,
                            Side = t.Fields.TryGetValue(sideColumn, out var sideValue) ? sideValue.Trim() : string.Empty
                        })
                        .ToList();

                    if (groupedBySymbolAndSide.Count == 0)
                    {
                        Console.WriteLine("No valid allocations to process after required field validation.");
                        continue;
                    }

                    Console.WriteLine($"Found {groupedBySymbolAndSide.Count} trade group(s) by symbol/side.");

                    // -------------------------------------------------------------------
                    // Build and send one AllocationInstruction (35=J) per symbol
                    // -------------------------------------------------------------------
                    var reportEntries = new List<ValidationResult>();
                    var groupErrors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    var groupTotals = trades
                        .GroupBy(t => new
                        {
                            Symbol = t.Fields.TryGetValue(symbolColumn, out var symbolValue) ? symbolValue.Trim() : string.Empty,
                            Side = t.Fields.TryGetValue(sideColumn, out var sideValue) ? sideValue.Trim() : string.Empty
                        })
                        .ToDictionary(g => $"{g.Key.Symbol}|{g.Key.Side}", g => g.Count(), StringComparer.OrdinalIgnoreCase);
                    var missingTagsByGroup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                    void AddGroupError(string symbol, string side, string error)
                    {
                        if (string.IsNullOrWhiteSpace(error)) return;
                        var key = $"{symbol}|{side}";
                        if (!groupErrors.TryGetValue(key, out var errors))
                        {
                            errors = new List<string>();
                            groupErrors[key] = errors;
                        }
                        errors.Add(error);
                    }

                    foreach (var trade in missingTrades)
                    {
                        var symbol = trade.Fields.TryGetValue(symbolColumn, out var symbolValue) ? symbolValue.Trim() : string.Empty;
                        var side = trade.Fields.TryGetValue(sideColumn, out var sideValue) ? sideValue.Trim() : string.Empty;
                        var key = $"{symbol}|{side}";

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
                        var parts = kvp.Key.Split('|');
                        var symbol = parts.Length > 0 ? parts[0] : string.Empty;
                        var side = parts.Length > 1 ? parts[1] : string.Empty;
                        var groupTotal = groupTotals.TryGetValue(kvp.Key, out var count) ? count : 0;
                        var message =
                            $"{groupTotal} out of {totalTrades} allocations identified for group trade {side}/{symbol} failed to merge because it is missing required value(s): {string.Join(", ", kvp.Value)}. Allocations processing cancelled.";
                        AddGroupError(symbol, side, message);
                    }

                    var preparedMessages = new List<(FixMessage Message, string AllocId, string Symbol, string Side, int TradeCount, IEnumerable<TradeRecord> Trades)>();
                    int buildFailures = 0;

                    foreach (var symbolGroup in groupedBySymbolAndSide)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        string symbol = symbolGroup.Key.Symbol;
                        string side = symbolGroup.Key.Side;
                        var allocId = fixBuilder.NextAllocId();
                        var groupTradeDate = ResolveTradeDate(symbolGroup, tradeDateColumn);
                        var reportAllocId = FormatAllocId(allocId, groupTradeDate);
                        string senderComp = mapping.Predefined?.SenderCompID ?? mapping.ClientId ?? "TRADEALLOC";
                        string targetComp = mapping.Predefined?.TargetCompID ?? "EXECUTOR";

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
                                string.Empty
                            ));
                            AddGroupError(symbol, side, ex.Message);
                            buildFailures++;
                            continue;
                        }

                        preparedMessages.Add((mergedMsg, allocId, symbol, side, symbolGroup.Count(), symbolGroup));
                    }

                    bool canSendFixMessages = !missingTrades.Any() && buildFailures == 0;
                    if (!canSendFixMessages)
                    {
                        Console.WriteLine("Allocations processing cancelled. One or more merged allocations failed to process.");
                    }

                    var senderCompId = mapping.Predefined?.SenderCompID ?? mapping.ClientId ?? "TRADEALLOC";
                    var targetCompId = mapping.Predefined?.TargetCompID ?? "EXECUTOR";
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
                        var tradeCount = prepared.TradeCount;
                        var groupTradeDate = ResolveTradeDate(prepared.Trades, tradeDateColumn);

                        string status;
                        string errorMessage;

                        if (!canSendFixMessages)
                        {
                            status = "Failed";
                            errorMessage = "Skipped";
                            var groupKey = $"{symbol}|{side}";
                            if (!groupErrors.ContainsKey(groupKey))
                            {
                                var groupTotal = groupTotals.TryGetValue(groupKey, out var count) ? count : tradeCount;
                                var failedAllocationsCount = totalTrades - groupTotal;
                                var cancelMessage =
                                    $" {groupTotal} out of {totalTrades} Allocations identified for group trade {side}/{symbol} were successfully merged. Fix message cannot be sent because {failedAllocationsCount} trades have error(s). Allocations processing cancelled.";
                                AddGroupError(symbol, side, cancelMessage);
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
                                AddGroupError(symbol, side, errorMessage);
                            }
                            else
                            {
                                var nonNullSession = sessionID; // local alias
                                var sendResult = dryRun ? "OK" : await fixClient.SendAsync(mergedMsg, nonNullSession);
                                status = sendResult == "OK" ? "Sent" : "Failed";
                                errorMessage = sendResult == "OK" ? string.Empty : sendResult;
                                if (sendResult != "OK")
                                {
                                    AddGroupError(symbol, side, sendResult);
                                }
                            }
                        }

                        Console.WriteLine($"AllocID={allocId} ({side}/{symbol})  {status}");
                        fixBuilderLogger.LogInformation(
                            "Sent merged allocation {AllocId} for {Side} of {Symbol} security ({Count} trades): {Status}",
                            allocId, side, symbol, tradeCount, status);

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
                            rawFix
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
                                var currentKey = $"{entry.Symbol}|{entry.Side}";
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

    }
}


