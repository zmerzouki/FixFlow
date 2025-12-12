using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models;
using QuickFix.Fields;
using RJF.TradeAllocBridge.Core.Config;
using RJF.TradeAllocBridge.Core.Email;
using RJF.TradeAllocBridge.Core.Excel;
using RJF.TradeAllocBridge.Core.Fix;
using RJF.TradeAllocBridge.Core.Logging;
using RJF.TradeAllocBridge.Core.Mapping;
using RJF.TradeAllocBridge.Core.Reporting;
using Serilog;

namespace RJF.TradeAllocBridge.CLI
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.Title = "RJF.TradeAllocBridge CLI";

            if (args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  RJF.TradeAllocBridge process-now [--dry-run]");
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

                using var app = builder.Build();

                var fixEngine = app.Services.GetRequiredService<FixEngine>();
                var emailService = app.Services.GetRequiredService<GraphEmailService>();
                var excelParser = app.Services.GetRequiredService<ExcelParser>();
                var fixClient = app.Services.GetRequiredService<FixClient>();
                var report = app.Services.GetRequiredService<ValidationReport>();
                var fixApp = app.Services.GetRequiredService<FixApp>();
                fixApp.Engine = fixEngine;

                if (!dryRun)
                    fixEngine.Start();

                Console.WriteLine("===> FIX XML Group Definition Present? " +
                    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.xml"))
                        .Contains("NoAllocs"));
                
                // ✅ Diagnostic: Manually test if FIX42.xml loads correctly
                try
                {
                    var dictPath = Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.xml");
                    var dd = new QuickFix.DataDictionary.DataDictionary(dictPath);
                    Console.WriteLine($"✅ Dictionary loaded successfully: {dictPath}");
                    Console.WriteLine($"   Fields: {dd.FieldsByTag.Count}, Messages: {dd.Messages.Count}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Failed to load FIX dictionary manually: {ex.Message}");
                }

                Console.WriteLine(dryRun
                    ? "Running in DRY-RUN (validation-only) mode — no messages will be sent."
                    : "Starting manual trade allocation processing...");

                // Fetch emails or use local test data
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

                    var mapPath = Path.Combine(configDir, "RAYMONDJAMES_map.json");

                    emails = new List<AllocationEmail>
                    {
                        new AllocationEmail
                        {
                            Subject = "Local Test Allocation",
                            SenderEmail = "test@rjf.local",
                            ClientId = "RAYMONDJAMES",
                            MapPath = mapPath,
                            Attachments = new List<string>
                            {
                                Path.Combine(AppContext.BaseDirectory, "Allocations_RAYMONDJAMES_20251204.xlsx")
                            }
                        }
                    };
                }
                else
                {
                    emails = await emailService.FetchNewEmailsAsync();
                    Console.WriteLine($"Fetched {emails.Count} email(s) with attachments.");
                }

                // Process each email
                foreach (var email in emails)
                {
                    if (string.IsNullOrEmpty(email.MapPath) || !File.Exists(email.MapPath))
                    {
                        Console.WriteLine($"⚠️ No mapping found for client {email.ClientId} (sender {email.SenderEmail}). Skipping.");
                        continue;
                    }

                    MappingConfig mapping;
                    try
                    {
                        mapping = MappingConfig.Load(email.MapPath);
                        Console.WriteLine($"✅ Loaded mapping for client {email.ClientId} from {email.MapPath}");
                    }
                    catch (Exception exMap)
                    {
                        Console.WriteLine($"❌ Failed to load mapping for client {email.ClientId}: {exMap.Message}");
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

                        // Group trades by Security, Side, and TradeDate
                        var groupedAllocations = trades
                            .GroupBy(t =>
                            {
                                var row = t.Fields;
                                var (secId, idSrc) = FixValueNormalizer.GetSecurityIdAndSource(row);
                                string side = FixValueNormalizer.Normalize(Tags.Side, row.GetValueOrDefault("SIDE") ?? "BUY", row);
                                string tradeDate = FixValueNormalizer.Normalize(Tags.TradeDate, row.GetValueOrDefault("TRADE DATE") ?? "", row);
                                return (secId, idSrc, side, tradeDate);
                            })
                            .ToList();

                        Console.WriteLine($"📊 Found {groupedAllocations.Count} trade group(s).");

                        foreach (var tradeGroup in groupedAllocations)
                        {
                            var allocId = $"{fixBuilder.NextAllocId()}-{DateTime.Now:yyyyMMdd}";

                            string senderComp = mapping.Predefined?.SenderCompID ?? mapping.ClientId ?? "TRADEALLOC";
                            string targetComp = mapping.Predefined?.TargetCompID ?? "EXECUTOR";

                            // ✅ Build only one merged message per group
                            var mergedMsg = fixBuilder.BuildFromAllocGroup(allocId, senderComp, targetComp, tradeGroup.ToList());
                            var sendResult = dryRun ? "OK" : await fixClient.SendAsync(mergedMsg);
                            string status = sendResult == "OK" ? "OK" : "FAILED";

                            Console.WriteLine($"AllocID={allocId} → {status}");
                            fixBuilderLogger.LogInformation("✅ Sent merged allocation {AllocId} ({Count} trades): {Status}",
                                allocId, tradeGroup.Count(), status);

                            // Add merged allocation record to report
                            string rawFix = mergedMsg.ToString().Replace('\u0001', '|').Replace("\r", "").Replace("\n", "");
                            report.Add(new ValidationResult(
                                DateTime.Now.ToString("o"),
                                string.Join(",", tradeGroup.Select(t => t.Id)),
                                allocId,
                                mergedMsg.IsSetField(55) ? mergedMsg.GetString(55) : "",
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
                Log.Error(ex, "Unhandled exception in RJF.TradeAllocBridge CLI");
                return -1;
            }
        }
    }
}
