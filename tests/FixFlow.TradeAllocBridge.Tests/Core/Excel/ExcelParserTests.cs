using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Excel;
using Microsoft.Extensions.Logging.Abstractions;

namespace FixFlow.TradeAllocBridge.Tests.Core.Excel;

public class ExcelParserTests
{
    [Fact]
    public void ParseCsv_DetectsHeaderRow_AndSkipsSeparatorRows()
    {
        var parser = new ExcelParser(NullLogger<ExcelParser>.Instance);
        var mapping = new MappingConfig
        {
            TradeAllocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Symbol"] = "55",
                ["Side"] = "54",
                ["Alloc Account"] = "79",
                ["Quantity"] = "80",
                ["Price"] = "153"
            }
        };

        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}.csv");

        try
        {
            File.WriteAllText(tempPath, """
            Intro line
            Another intro line
            Symbol,Side,Alloc Account,Quantity,Price
            -----,-----,-----,-----,-----
            MSFT,BUY,ACC-1,10,25.5
            """);

            var records = parser.Parse(tempPath, mapping);

            var record = Assert.Single(records);
            Assert.Equal("MSFT", record.Fields["Symbol"]);
            Assert.Equal("BUY", record.Fields["Side"]);
            Assert.Equal("ACC-1", record.Fields["Alloc Account"]);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void ParseCsv_WithPositionalMapping_UsesColumnIndexesAsHeaders()
    {
        var parser = new ExcelParser(NullLogger<ExcelParser>.Instance);
        var mapping = new MappingConfig
        {
            TradeAllocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["1"] = "55",
                ["2"] = "54",
                ["3"] = "80"
            }
        };

        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}.csv");

        try
        {
            File.WriteAllText(tempPath, """
            AAPL,BUY,100
            MSFT,SELL,200
            """);

            var records = parser.Parse(tempPath, mapping);

            Assert.Equal(2, records.Count);
            Assert.Equal("AAPL", records[0].Fields["1"]);
            Assert.Equal("BUY", records[0].Fields["2"]);
            Assert.Equal("100", records[0].Fields["3"]);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
