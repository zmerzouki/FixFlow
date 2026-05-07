using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Excel;
using FixFlow.TradeAllocBridge.Core.Fix;

namespace FixFlow.TradeAllocBridge.Tests.Core.Fix;

public class FixValueNormalizerTests
{
    [Theory]
    [InlineData("BUY", "1")]
    [InlineData("sell", "2")]
    [InlineData("Buy Minus", "3")]
    [InlineData("SELL PLUS", "4")]
    [InlineData("short", "5")]
    [InlineData("short exempt", "6")]
    [InlineData("cross", "8")]
    [InlineData("cross short", "9")]
    [InlineData("5", "5")]
    public void Normalize_SideValues_ReturnsExpectedFixEnum(string raw, string expected)
    {
        var actual = FixValueNormalizer.Normalize(54, raw);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Normalize_InvalidSideValue_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FixValueNormalizer.Normalize(54, "12"));

        Assert.Contains("invalid value", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSecurityIdAndSource_PrioritizesCusipThenIsinThenSedol()
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ISIN"] = "us0378331005",
            ["CUSIP"] = "037833100",
            ["SEDOL"] = "2046251"
        };

        var result = FixValueNormalizer.GetSecurityIdAndSource(row);

        Assert.Equal("037833100", result.secId);
        Assert.Equal("1", result.idSource);
    }

    [Fact]
    public void FindNumericFieldIssues_ReturnsMappedNonNumericValues()
    {
        var trades = new[]
        {
            new TradeRecord
            {
                Id = "T1",
                Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Quantity"] = "not-a-number",
                    ["Price"] = "12.50"
                }
            },
            new TradeRecord
            {
                Id = "T2",
                Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Quantity"] = "100",
                    ["Price"] = "oops"
                }
            }
        };

        var mapping = new MappingConfig
        {
            TradeAllocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Quantity"] = "80",
                ["Price"] = "153",
                ["Description"] = "58"
            }
        };

        var issues = FixValueNormalizer.FindNumericFieldIssues(trades, mapping);

        Assert.Collection(
            issues.OrderBy(i => i.TradeId).ThenBy(i => i.Tag),
            issue =>
            {
                Assert.Equal("T1", issue.TradeId);
                Assert.Equal("Quantity", issue.ColumnKey);
                Assert.Equal(80, issue.Tag);
                Assert.Equal("not-a-number", issue.SampleValue);
            },
            issue =>
            {
                Assert.Equal("T2", issue.TradeId);
                Assert.Equal("Price", issue.ColumnKey);
                Assert.Equal(153, issue.Tag);
                Assert.Equal("oops", issue.SampleValue);
            });
    }
}
