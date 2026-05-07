using FixFlow.TradeAllocBridge.Core.Excel;
using FixFlow.TradeAllocBridge.Core.Reporting;

namespace FixFlow.TradeAllocBridge.Tests.Core.Reporting;

public class ValidationMetricsTests
{
    [Theory]
    [InlineData("1234.56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    public void TryParseDecimal_AcceptsCommonNumericFormats(string raw, decimal expected)
    {
        var success = ValidationMetrics.TryParseDecimal(raw, out var actual);

        Assert.True(success);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CalculateGrossAmount_SumsAcrossTrades()
    {
        var trades = new[]
        {
            new TradeRecord
            {
                Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Quantity"] = "10",
                    ["Price"] = "5.25"
                }
            },
            new TradeRecord
            {
                Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Quantity"] = "4",
                    ["Price"] = "12.50"
                }
            }
        };

        var total = ValidationMetrics.CalculateGrossAmount(trades, "Quantity", "Price");

        Assert.Equal(102.50m, total);
    }
}
