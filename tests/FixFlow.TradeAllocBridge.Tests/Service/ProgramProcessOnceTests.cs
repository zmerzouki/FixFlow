using System.Text.Json;
using FixFlow.TradeAllocBridge.CLI;
using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Email;
using FixFlow.TradeAllocBridge.Core.Excel;
using FixFlow.TradeAllocBridge.Core.Fix;
using FixFlow.TradeAllocBridge.Core.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuickFix;

namespace FixFlow.TradeAllocBridge.Tests.Service;

public class ProgramProcessOnceTests
{
    [Fact]
    public async Task ProcessOnceAsync_DryRunWithValidMappings_ReloadsSessionsWithoutStartingEngine()
    {
        var mappingDir = Directory.CreateTempSubdirectory();
        try
        {
            var mapPath = WriteMappingFile(mappingDir.FullName, "PEACE");
            var emailService = new FakeAllocationEmailService
            {
                Emails =
                [
                    new AllocationEmail
                    {
                        ClientId = "PEACE",
                        SenderEmail = "ops@peace.example",
                        MapPath = mapPath,
                        Attachments = []
                    }
                ]
            };
            var fixEngine = new FakeFixSessionEngine();

            var services = BuildServices(fixEngine, emailService);

            await Program.ProcessOnceAsync(services, dryRun: true, keepEngineRunning: false, CancellationToken.None);

            Assert.Equal(1, fixEngine.AppendCalls);
            Assert.Equal(1, fixEngine.ReloadCalls);
            Assert.Equal(0, fixEngine.StartCalls);
            Assert.Equal(0, fixEngine.StopCalls);
            var session = Assert.Single(fixEngine.AppendedSessions);
            Assert.Equal("FIXFLOW", session.Sender);
            Assert.Equal("BROKER", session.Target);
        }
        finally
        {
            mappingDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ProcessOnceAsync_LiveMode_StartsAndStopsEngineWhenMappingsExist()
    {
        var mappingDir = Directory.CreateTempSubdirectory();
        try
        {
            var mapPath = WriteMappingFile(mappingDir.FullName, "PEACE");
            var emailService = new FakeAllocationEmailService
            {
                Emails =
                [
                    new AllocationEmail
                    {
                        ClientId = "PEACE",
                        SenderEmail = "ops@peace.example",
                        MapPath = mapPath,
                        Attachments = []
                    }
                ]
            };
            var fixEngine = new FakeFixSessionEngine { IsStarted = false };

            var services = BuildServices(fixEngine, emailService);

            await Program.ProcessOnceAsync(services, dryRun: false, keepEngineRunning: false, CancellationToken.None);

            Assert.Equal(1, fixEngine.AppendCalls);
            Assert.Equal(1, fixEngine.ReloadCalls);
            Assert.Equal(1, fixEngine.StartCalls);
            Assert.Equal(1, fixEngine.StopCalls);
        }
        finally
        {
            mappingDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ProcessOnceAsync_SkipsFixInitializationWhenNoValidMappingsAreAvailable()
    {
        var emailService = new FakeAllocationEmailService
        {
            Emails =
            [
                new AllocationEmail
                {
                    ClientId = "PEACE",
                    SenderEmail = "ops@peace.example",
                    MapPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json"),
                    Attachments = []
                }
            ]
        };
        var fixEngine = new FakeFixSessionEngine();

        var services = BuildServices(fixEngine, emailService);

        await Program.ProcessOnceAsync(services, dryRun: false, keepEngineRunning: false, CancellationToken.None);

        Assert.Equal(0, fixEngine.AppendCalls);
        Assert.Equal(0, fixEngine.ReloadCalls);
        Assert.Equal(0, fixEngine.StartCalls);
        Assert.Equal(1, fixEngine.StopCalls);
    }

    private static IServiceProvider BuildServices(FakeFixSessionEngine fixEngine, FakeAllocationEmailService emailService)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();

        var appConfig = new AppConfig
        {
            Email = new EmailConfig
            {
                MailboxAddress = "allocations@example.com",
                DownloadPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
            },
            Fix = new FixConfig
            {
                SenderCompId = "FIXFLOW",
                TargetCompId = "BROKER",
                SessionQualifier = "FIXFLOWSERVICE"
            }
        };

        var validationReport = new ValidationReport(NullLogger<ValidationReport>.Instance);
        var fixApp = new FixApp(NullLogger<FixApp>.Instance, validationReport);

        serviceCollection.AddSingleton(appConfig);
        serviceCollection.AddSingleton(validationReport);
        serviceCollection.AddSingleton(fixApp);
        serviceCollection.AddSingleton<IFixSessionEngine>(fixEngine);
        serviceCollection.AddSingleton<IAllocationEmailService>(emailService);
        serviceCollection.AddSingleton<ITradeFileParser>(new FakeTradeFileParser());
        serviceCollection.AddSingleton<IFixMessageClient>(new FakeFixMessageClient());

        return serviceCollection.BuildServiceProvider();
    }

    private static string WriteMappingFile(string directory, string clientId)
    {
        Directory.CreateDirectory(directory);
        var payload = JsonSerializer.Serialize(new
        {
            clientId,
            organizationName = clientId,
            tradeAllocations = new Dictionary<string, string>(),
            predefined = new Dictionary<string, string>
            {
                ["49"] = "FIXFLOW",
                ["56"] = "BROKER"
            }
        });

        var path = Path.Combine(directory, $"{clientId}_map.json");
        File.WriteAllText(path, payload);
        return path;
    }

    private sealed class FakeAllocationEmailService : IAllocationEmailService
    {
        public List<AllocationEmail> Emails { get; init; } = [];

        public Task<List<AllocationEmail>> FetchNewEmailsAsync() => Task.FromResult(Emails);
    }

    private sealed class FakeTradeFileParser : ITradeFileParser
    {
        public List<TradeRecord> Parse(string filePath) => [];

        public List<TradeRecord> Parse(string filePath, MappingConfig? mapping) => [];
    }

    private sealed class FakeFixMessageClient : IFixMessageClient
    {
        public Task<string> SendAsync(Message msg, SessionID? sessionID = null) => Task.FromResult("OK");

        public List<string> ValidateAllocation(Message msg) => [];
    }

    private sealed class FakeFixSessionEngine : IFixSessionEngine
    {
        public SessionSettings? SessionSettings { get; set; }
        public bool IsStarted { get; set; }
        public int AppendCalls { get; private set; }
        public int ReloadCalls { get; private set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public List<(string Sender, string Target, string? SenderSubId, string? TargetSubId)> AppendedSessions { get; } = [];

        public void AppendSessionsIfMissing(IEnumerable<(string Sender, string Target, string? SenderSubId, string? TargetSubId)> sessions)
        {
            AppendCalls++;
            AppendedSessions.AddRange(sessions);
        }

        public void ReloadSettings(FixApp app)
        {
            ReloadCalls++;
        }

        public void Start()
        {
            StartCalls++;
            IsStarted = true;
        }

        public void Stop()
        {
            StopCalls++;
            IsStarted = false;
        }
    }
}
