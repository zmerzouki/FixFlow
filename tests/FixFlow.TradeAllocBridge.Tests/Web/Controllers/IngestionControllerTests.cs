using System.Text;
using System.Text.Json;
using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Excel;
using FixFlow.TradeAllocBridge.Core.Fix;
using FixFlow.TradeAllocBridge.Core.Mapping;
using FixFlow.TradeAllocBridge.Core.Reporting;
using FixFlow.TradeAllocBridge.Tests.TestSupport;
using FixFlow.TradeAllocBridge.Web.Controllers;
using FixFlow.TradeAllocBridge.Web.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using QuickFix;

namespace FixFlow.TradeAllocBridge.Tests.Web.Controllers;

public class IngestionControllerTests
{
    [Fact]
    public async Task Preview_ReturnsBadRequest_WhenDefaultClientMissingFixId()
    {
        var mappingDir = Directory.CreateTempSubdirectory();
        try
        {
            var controller = CreateController(
                mappingDir.FullName,
                new FakeTradeFileParser(),
                new FakeFixSessionEngine(),
                new FakeFixMessageClient());

            var result = await controller.Preview(new IngestionFormRequest
            {
                ClientId = "DEFAULT",
                File = CreateFormFile("allocations.csv", "ignored")
            }, CancellationToken.None);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal("FIX ID (tag 115) is required for DEFAULT client.", badRequest.Value);
        }
        finally
        {
            mappingDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Preview_ReturnsNoTradesResponse_WhenParserReturnsNoRecords()
    {
        var mappingDir = Directory.CreateTempSubdirectory();
        try
        {
            WriteMappingFile(mappingDir.FullName, "PEACE");

            var parser = new FakeTradeFileParser();
            var fixEngine = new FakeFixSessionEngine();
            var controller = CreateController(mappingDir.FullName, parser, fixEngine, new FakeFixMessageClient());

            var result = await controller.Preview(new IngestionFormRequest
            {
                ClientId = "PEACE",
                File = CreateFormFile("allocations.csv", "Symbol,Side")
            }, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var response = Assert.IsType<IngestionPreviewResponse>(ok.Value);
            Assert.Contains("No trade records found", response.Status, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, response.TotalTrades);
            Assert.Equal(0, fixEngine.AppendCalls);
            Assert.Equal(0, fixEngine.StartCalls);
        }
        finally
        {
            mappingDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Process_SendsAllocationAndStopsEngine_WhenSessionExists()
    {
        using var workingDirectory = new TemporaryWorkingDirectoryScope();
        var mappingDir = Directory.CreateTempSubdirectory();
        try
        {
            WriteMappingFile(mappingDir.FullName, "PEACE");

            var parser = new FakeTradeFileParser
            {
                Trades =
                [
                    new TradeRecord
                    {
                        Id = "1",
                        Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Symbol"] = "MSFT",
                            ["Side"] = "BUY",
                            ["Alloc Account"] = "ACC-1",
                            ["Quantity"] = "10",
                            ["Price"] = "25",
                            ["TRADE DATE"] = "2026-05-01"
                        }
                    }
                ]
            };

            var fixEngine = new FakeFixSessionEngine
            {
                SessionSettings = CreateSessionSettings(Path.Combine(workingDirectory.Path, "test_FIX42.cfg"))
            };
            var fixClient = new FakeFixMessageClient();
            var controller = CreateController(mappingDir.FullName, parser, fixEngine, fixClient);

            var result = await controller.Process(new IngestionFormRequest
            {
                ClientId = "PEACE",
                DryRun = false,
                File = CreateFormFile("allocations.csv", "ignored")
            }, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var response = Assert.IsType<IngestionPreviewResponse>(ok.Value);
            Assert.Equal(1, response.AllocationsSubmitted);
            Assert.Equal(1, response.AllocationsProcessed);
            Assert.Equal(1, response.AllocationsSent);
            Assert.Equal(0, response.FailedAllocations);
            Assert.Equal(1, fixEngine.AppendCalls);
            Assert.Equal(1, fixEngine.ReloadCalls);
            Assert.Equal(1, fixEngine.StartCalls);
            Assert.Equal(1, fixEngine.StopCalls);
            Assert.Equal(1, fixClient.SendCalls);
        }
        finally
        {
            mappingDir.Delete(recursive: true);
        }
    }

    private static IngestionController CreateController(
        string mappingDirectory,
        FakeTradeFileParser parser,
        FakeFixSessionEngine fixEngine,
        FakeFixMessageClient fixClient)
    {
        var fixConfig = new FixConfig
        {
            SenderCompId = "FIXFLOW",
            TargetCompId = "BROKER"
        };

        var validationReport = new ValidationReport(NullLogger<ValidationReport>.Instance);
        var fixApp = new FixApp(NullLogger<FixApp>.Instance, validationReport);
        return new IngestionController(
            parser,
            fixConfig,
            new FixMappingRepository(mappingDirectory),
            validationReport,
            fixApp,
            fixEngine,
            fixClient,
            NullLoggerFactory.Instance,
            NullLogger<IngestionController>.Instance);
    }

    private static void WriteMappingFile(string directory, string clientId)
    {
        Directory.CreateDirectory(directory);
        var payload = JsonSerializer.Serialize(new
        {
            clientId,
            organizationName = clientId,
            tradeAllocations = new Dictionary<string, string>
            {
                ["Symbol"] = "55",
                ["Side"] = "54",
                ["Alloc Account"] = "79",
                ["Quantity"] = "80",
                ["Price"] = "153",
                ["TRADE DATE"] = "75"
            },
            predefined = new Dictionary<string, string>
            {
                ["49"] = "FIXFLOW",
                ["56"] = "BROKER"
            }
        });

        File.WriteAllText(Path.Combine(directory, $"{clientId}_map.json"), payload);
    }

    private static IFormFile CreateFormFile(string fileName, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", fileName);
    }

    private static SessionSettings CreateSessionSettings(string configPath)
    {
        var contents = """
            [DEFAULT]
            ConnectionType=initiator
            StartTime=00:00:00
            EndTime=23:59:59
            HeartBtInt=30
            SocketConnectHost=127.0.0.1
            SocketConnectPort=5001
            FileStorePath=store

            [SESSION]
            BeginString=FIX.4.2
            SenderCompID=FIXFLOW
            TargetCompID=BROKER
            """;

        File.WriteAllText(configPath, contents);
        return new SessionSettings(configPath);
    }

    private sealed class FakeTradeFileParser : ITradeFileParser
    {
        public List<TradeRecord> Trades { get; init; } = [];

        public List<TradeRecord> Parse(string filePath) => Trades;

        public List<TradeRecord> Parse(string filePath, MappingConfig? mapping) => Trades;
    }

    private sealed class FakeFixMessageClient : IFixMessageClient
    {
        public int SendCalls { get; private set; }

        public Task<string> SendAsync(Message msg, SessionID? sessionID = null)
        {
            SendCalls++;
            return Task.FromResult("OK");
        }

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

        public void AppendSessionsIfMissing(IEnumerable<(string Sender, string Target, string? SenderSubId, string? TargetSubId)> sessions)
        {
            AppendCalls++;
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
