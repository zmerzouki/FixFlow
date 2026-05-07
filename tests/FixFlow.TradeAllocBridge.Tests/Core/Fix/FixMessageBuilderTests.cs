using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Excel;
using FixFlow.TradeAllocBridge.Core.Fix;
using Microsoft.Extensions.Logging.Abstractions;
using QuickFix.Fields;

namespace FixFlow.TradeAllocBridge.Tests.Core.Fix;

public class FixMessageBuilderTests
{
    [Fact]
    public void Build_ThrowsWhenBothSymbolAndSecurityIdAreMissing()
    {
        using var workingDir = new WorkingDirectoryScope();
        var builder = new FixMessageBuilder(CreateMapping(), CreateFixConfig(), NullLogger<FixMessageBuilder>.Instance);
        var trade = new TradeRecord
        {
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Side"] = "BUY",
                ["Alloc Account"] = "ACC-1",
                ["Quantity"] = "10",
                ["Price"] = "12.5",
                ["TRADE DATE"] = "2026-05-01"
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build(trade));

        Assert.Contains("tag 48", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tag 55", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildFromAllocGroup_MergesTradesAndNormalizesCoreFields()
    {
        using var workingDir = new WorkingDirectoryScope();
        var builder = new FixMessageBuilder(CreateMapping(), CreateFixConfig(), NullLogger<FixMessageBuilder>.Instance);
        var trades = new[]
        {
            new TradeRecord
            {
                Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Symbol"] = "MSFT",
                    ["Side"] = "BUY",
                    ["Alloc Account"] = "ACC-1",
                    ["Quantity"] = "10",
                    ["Price"] = "20",
                    ["TRADE DATE"] = "2026-05-01"
                }
            },
            new TradeRecord
            {
                Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Symbol"] = "MSFT",
                    ["Side"] = "BUY",
                    ["Alloc Account"] = "ACC-2",
                    ["Quantity"] = "5",
                    ["Price"] = "22",
                    ["TRADE DATE"] = "2026-05-01"
                }
            }
        };

        var message = builder.BuildFromAllocGroup("ALLOC-1", "FIXFLOW", "BROKER", trades, CreateMapping());

        Assert.Equal("FIXFLOW", message.Header.GetString(Tags.SenderCompID));
        Assert.Equal("BROKER", message.Header.GetString(Tags.TargetCompID));
        Assert.Equal("ALLOC-1_20260501", message.GetString(Tags.AllocID));
        Assert.Equal("MSFT", message.GetString(Tags.Symbol));
        Assert.Equal("1", message.GetString(Tags.Side));
        Assert.Equal("20260501", message.GetString(Tags.TradeDate));
        Assert.Equal("15", decimal.Parse(message.GetString(Tags.Shares)).ToString("0"));
        Assert.Equal("2", message.GetString(78));
    }

    private static MappingConfig CreateMapping()
    {
        return new MappingConfig
        {
            ClientId = "PEACE",
            TradeAllocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Symbol"] = "55",
                ["Side"] = "54",
                ["Alloc Account"] = "79",
                ["Quantity"] = "80",
                ["Price"] = "153",
                ["TRADE DATE"] = "75"
            }
        };
    }

    private static FixConfig CreateFixConfig()
    {
        return new FixConfig
        {
            SenderCompId = "FIXFLOW",
            TargetCompId = "BROKER"
        };
    }

    private sealed class WorkingDirectoryScope : IDisposable
    {
        private readonly string _originalDirectory;
        private readonly string _tempDirectory;

        public WorkingDirectoryScope()
        {
            _originalDirectory = Directory.GetCurrentDirectory();
            _tempDirectory = Directory.CreateTempSubdirectory().FullName;
            Directory.SetCurrentDirectory(_tempDirectory);
        }

        public void Dispose()
        {
            Directory.SetCurrentDirectory(_originalDirectory);
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
