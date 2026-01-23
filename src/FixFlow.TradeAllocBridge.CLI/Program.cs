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
using System.Runtime.Versioning;
using FixMessage = QuickFix.Message;
using FixFlow.TradeAllocBridge.CLI.Services; // <- added
using System.IO;

[assembly: SupportedOSPlatform("windows7.0")]

namespace FixFlow.TradeAllocBridge.CLI
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.Title = "TradeAllocBridge CLI";

            if (args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  TradeAllocBridge process-now [--dry-run]");
                Console.WriteLine();
                Console.WriteLine("Description:");
                Console.WriteLine("  Processes mailbox allocations, builds FIX 4.2 messages, and optionally sends them.");
                Console.WriteLine("  Use --dry-run to validate and log FIX messages without transmitting them.");
                return 0;
            }

            bool dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);

            try
            {
                var builder = Host.CreateApplicationBuilder(args);
                builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

                var config = builder.Configuration.Get<AppConfig>()!;
                LogService.Configure(config.Logging);

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
                var baseDirCli = AppContext.BaseDirectory;
                var activeConfigs = Path.Combine(baseDirCli, "configs");
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

                using var app = builder.Build();

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

                var fixEngine = app.Services.GetRequiredService<FixEngine>();
                var emailService = app.Services.GetRequiredService<GraphEmailService>();
                var excelParser = app.Services.GetRequiredService<ExcelParser>();
                var fixClient = app.Services.GetRequiredService<FixClient>();
                var report = app.Services.GetRequiredService<ValidationReport>();
                var fixApp = app.Services.GetRequiredService<FixApp>();
                fixApp.Engine = fixEngine;

                Console.WriteLine("===> FIX XML Group Definition Present? " +
                    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.xml"))
                        .Contains("NoAllocs"));

                // ? Manual verification of dictionary load
                try
                {
                       var dictPath = Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.xml");
                    var dd = new QuickFix.DataDictionary.DataDictionary(dictPath);
                    Console.WriteLine($"? Dictionary loaded successfully: {dictPath}");
                    Console.WriteLine($"   Fields: {dd.FieldsByTag.Count}, Messages: {dd.Messages.Count}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"? Failed to load FIX dictionary manually: {ex.Message}");
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
                            SenderEmail = "test@rjf.local",
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

                // -------------------------------------------------------------------
                // ? Dynamic FIX Session Configuration (from all mappings)
                // -------------------------------------------------------------------
                var allMappings = new List<(string Sender, string Target)>();

                foreach (var email in emails)
                {
                    if (string.IsNullOrEmpty(email.MapPath) || !File.Exists(email.MapPath))
                    {
                        Console.WriteLine($"?? No mapping found for client {email.ClientId} (sender {email.SenderEmail}). Skipping.");
                        continue;
                    }

                    try
                    {
                        var mapping = MappingConfig.Load(email.MapPath);
                        string senderComp = mapping.Predefined?.SenderCompID ?? mapping.ClientId ?? "DEFAULTSENDER";
                        string targetComp = mapping.Predefined?.TargetCompID ?? "RJSYN";
                        allMappings.Add((senderComp, targetComp));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"? Failed to load mapping file {email.MapPath}: {ex.Message}");
                    }
                }

                if (allMappings.Count > 0)
                {
                    fixEngine.AppendSessionsIfMissing(allMappings);
                    fixEngine.ReloadSettings(fixApp);

                    if (!dryRun)
                        fixEngine.Start();
                }
                else
                {
                    Console.WriteLine("?? No valid mappings found - skipping FIX session initialization.");
                }

                // -------------------------------------------------------------------
                // Process emails (main allocation loop)
                // -------------------------------------------------------------------
                foreach (var email in emails)
                {
                    if (string.IsNullOrEmpty(email.MapPath) || !File.Exists(email.MapPath))
                        continue;

                    MappingConfig mapping;
                    try
                    {
                        mapping = MappingConfig.Load(email.MapPath);
                        Console.WriteLine($"? Loaded mapping for client {email.ClientId} from {email.MapPath}");
                    }
                    catch (Exception exMap)
                    {
                        Console.WriteLine($"? Failed to load mapping for client {email.ClientId}: {exMap.Message}");
                        continue;
                    }

                    var fixBuilderLogger = app.Services.GetRequiredService<ILogger<FixMessageBuilder>>();
                    var fixBuilder = new FixMessageBuilder(mapping, config.Fix, fixBuilderLogger);

                    foreach (var attachmentPath in email.Attachments)
                    {
                        if (!File.Exists(attachmentPath))
                        {
                            Console.WriteLine($"Attachment not found: {attachmentPath}");
                            continue;
                        }

                        Console.WriteLine($"Processing file: {Path.GetFileName(attachmentPath)}");
                        var trades = excelParser.Parse(attachmentPath);
                        var totalTrades = trades.Count;
                        // -------------------------------------------------------------------
                        // ? Group allocations by required fields (54,55,79,80,153)
                        // -------------------------------------------------------------------
                        var symbolColumn = mapping.TradeAllocations.FirstOrDefault(kvp => kvp.Value == "55").Key ?? "SYMBOL";
                        var sideColumn = mapping.TradeAllocations.FirstOrDefault(kvp => kvp.Value == "54").Key ?? "SIDE";
                        var accountColumn = mapping.TradeAllocations.FirstOrDefault(kvp => kvp.Value == "79").Key ?? "ALLOC ACCOUNT";
                        var qtyColumn = mapping.TradeAllocations.FirstOrDefault(kvp => kvp.Value == "80").Key ?? "QUANTITY";
                        var priceColumn = mapping.TradeAllocations.FirstOrDefault(kvp => kvp.Value == "153").Key ?? "PRICE";

                        bool IsMissing(TradeRecord t, string column) =>
                            !t.Fields.TryGetValue(column, out var val) || string.IsNullOrWhiteSpace(val?.Trim());

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
                            Console.WriteLine($"?? {missingTrades.Count} out of {totalTrades} Allocations have failed to process because it is missing required value(s): {string.Join(", ", missingTags)}");
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
                            Console.WriteLine("?? No valid allocations to process after required field validation.");
                            continue;
                        }

                        Console.WriteLine($"?? Found {groupedBySymbolAndSide.Count} trade group(s) by symbol/side.");

                        // -------------------------------------------------------------------
                        // ? Build and send one AllocationInstruction (35=J) per symbol
                        // -------------------------------------------------------------------
                        foreach (var symbolGroup in groupedBySymbolAndSide)
                        {
                            string symbol = symbolGroup.Key.Symbol;
                            string side = symbolGroup.Key.Side;
                            var allocId = fixBuilder.NextAllocId();
                            string senderComp = mapping.Predefined?.SenderCompID ?? mapping.ClientId ?? "TRADEALLOC";
                            string targetComp = mapping.Predefined?.TargetCompID ?? "EXECUTOR";

                            FixMessage mergedMsg;
                            try
                            {
                                mergedMsg = fixBuilder.BuildFromAllocGroup(allocId, senderComp, targetComp, symbolGroup, mapping);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"?? Failed to build FIX allocation for symbol '{symbol}' / side '{side}': {ex.Message}");
                                report.Add(new ValidationResult(
                                    DateTime.Now.ToString("o"),
                                    string.Join(",", symbolGroup.Select(t => t.Id)),
                                    allocId,
                                    symbol,
                                    string.Empty,
                                    string.Empty,
                                    string.Empty,
                                    "Failed",
                                    ex.Message,
                                    string.Empty
                                ));
                                continue;
                            }

                            // Retrieve the correct session ID from the FIX engine
                            SessionID? sessionID = null;
                            if (fixEngine.SessionSettings != null)
                            {
                                foreach (var sid in fixEngine.SessionSettings.GetSessions())
                                {
                                    if (string.Equals(sid.SenderCompID, senderComp, StringComparison.OrdinalIgnoreCase))
                                    {
                                        sessionID = sid;
                                        break;
                                    }
                                }
                            }

                            if (sessionID is null)
                            {
                                Console.WriteLine($"?? No FIX session found for SenderCompID={senderComp}. Skipping.");
                                continue;
                            }

                            var nonNullSession = sessionID; // local alias
                            var sendResult = dryRun ? "OK" : await fixClient.SendAsync(mergedMsg, nonNullSession);
                            string status = sendResult == "OK" ? "OK" : "FAILED";

                            Console.WriteLine($"AllocID={allocId} ({symbol}/{side})  {status}");
                            fixBuilderLogger.LogInformation(
                                "? Sent merged allocation {AllocId} for {Symbol}/{Side} ({Count} trades): {Status}",
                                allocId, symbol, side, symbolGroup.Count(), status);

                            string rawFix = mergedMsg.ToString().Replace('\u0001', '|').Replace("\r", "").Replace("\n", "");
                            report.Add(new ValidationResult(
                                DateTime.Now.ToString("o"),
                                string.Join(",", symbolGroup.Select(t => t.Id)),
                                allocId,
                                $"{symbol}/{side}",
                                mergedMsg.IsSetField(80) ? mergedMsg.GetString(80) : "",
                                mergedMsg.IsSetField(153) ? mergedMsg.GetString(153) : "",
                                mergedMsg.IsSetField(79) ? mergedMsg.GetString(79) : "",
                                status,
                                status == "OK" ? "" : sendResult,
                                rawFix
                            ));
                        }

                    }
                }

                // -------------------------------------------------------------------
                // Wrap up
                // -------------------------------------------------------------------
                report.Save();
                Console.WriteLine("Processing complete.");

                if (!dryRun)
                {
                    fixEngine.Stop();
                    Log.Information("FIX engine stopped cleanly.");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fatal error: {ex.Message}");
                Log.Error(ex, "Unhandled exception in TradeAllocBridge CLI");
                return -1;
            }
        }
    }
}