using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models;
using QuickFix;
using QuickFix.Fields;
using RJF.TradeAllocBridge.Core.Config;
using RJF.TradeAllocBridge.Core.Email;
using RJF.TradeAllocBridge.Core.Excel;
using RJF.TradeAllocBridge.Core.Fix;
using RJF.TradeAllocBridge.Core.Logging;
using RJF.TradeAllocBridge.Core.Mapping;
using RJF.TradeAllocBridge.Core.Reporting;
using Serilog;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows7.0")]

namespace RJF.TradeAllocBridge.CLI
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

                using var app = builder.Build();

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

                // ✅ Manual verification of dictionary load
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
                // ✅ Dynamic FIX Session Configuration (from all mappings)
                // -------------------------------------------------------------------
                var allMappings = new List<(string Sender, string Target)>();

                foreach (var email in emails)
                {
                    if (string.IsNullOrEmpty(email.MapPath) || !File.Exists(email.MapPath))
                    {
                        Console.WriteLine($"⚠️ No mapping found for client {email.ClientId} (sender {email.SenderEmail}). Skipping.");
                        continue;
                    }

                    try
                    {
                        var mapping = MappingConfig.Load(email.MapPath);
                        string senderComp = mapping.Predefined?.SenderCompID ?? mapping.ClientId ?? "DEFAULTSENDER";
                        string targetComp = mapping.Predefined?.TargetCompID ?? "RJSYNUAT";
                        allMappings.Add((senderComp, targetComp));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Failed to load mapping file {email.MapPath}: {ex.Message}");
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
                    Console.WriteLine("⚠️ No valid mappings found — skipping FIX session initialization.");
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
                        
// -------------------------------------------------------------------
// ✅ Group allocations by Symbol (tag 55)
// -------------------------------------------------------------------
var symbolColumn = mapping.TradeAllocations
    .FirstOrDefault(kvp => kvp.Value == "55").Key ?? "SYMBOL";

var groupedBySymbol = trades
    .GroupBy(t => t.Fields.GetValueOrDefault(symbolColumn, "UNKNOWN").Trim())
    .ToList();

Console.WriteLine($"📊 Found {groupedBySymbol.Count} trade group(s) by symbol.");

// -------------------------------------------------------------------
// ✅ Build and send one AllocationInstruction (35=J) per symbol
// -------------------------------------------------------------------
foreach (var symbolGroup in groupedBySymbol)
{
    string symbol = symbolGroup.Key;
    var allocId = $"{fixBuilder.NextAllocId()}-{DateTime.UtcNow:yyyyMMdd}";
    string senderComp = mapping.Predefined?.SenderCompID ?? mapping.ClientId ?? "TRADEALLOC";
    string targetComp = mapping.Predefined?.TargetCompID ?? "EXECUTOR";

    var mergedMsg = fixBuilder.BuildFromAllocGroup(allocId, senderComp, targetComp, symbolGroup, mapping);

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
        Console.WriteLine($"⚠️ No FIX session found for SenderCompID={senderComp}. Skipping.");
        continue;
    }

    var nonNullSession = sessionID; // local alias
    var sendResult = dryRun ? "OK" : await fixClient.SendAsync(mergedMsg, nonNullSession);
    string status = sendResult == "OK" ? "OK" : "FAILED";

    Console.WriteLine($"AllocID={allocId} ({symbol}) → {status}");
    fixBuilderLogger.LogInformation(
        "✅ Sent merged allocation {AllocId} for {Symbol} ({Count} trades): {Status}",
        allocId, symbol, symbolGroup.Count(), status);

    string rawFix = mergedMsg.ToString().Replace('\u0001', '|').Replace("\r", "").Replace("\n", "");
    report.Add(new ValidationResult(
        DateTime.Now.ToString("o"),
        string.Join(",", symbolGroup.Select(t => t.Id)),
        allocId,
        symbol,
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
