using FixFlow.TradeAllocBridge.Tests.TestSupport;
using FixFlow.TradeAllocBridge.Web.Controllers;
using FixFlow.TradeAllocBridge.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace FixFlow.TradeAllocBridge.Tests.Web.Controllers;

public class DashboardControllerTests
{
    [Fact]
    public void GetSummary_ExcludesDryRunsFromSourceAndOutcomeTotals_AndAggregatesSideAmounts()
    {
        var utcDate = DateTime.UtcNow.Date;
        var directPath = GetDirectReportPath(utcDate);
        using var directScope = new TestFileScope(directPath);
        directScope.WriteAllText(BuildReport(
            CreateRow(utcDate.AddHours(10), "PEACE", "D1", "", "MSFT", "Sent", "", "8=FIX.4.2|35=J|54=1", "Direct", false, 2, 100m),
            CreateRow(utcDate.AddHours(11), "PEACE", "D2", "8", "MSFT", "Valid. Not Sent", "", "8=FIX.4.2|35=J|54=8", "Direct", true, 3, 60m)));

        var emailDir = Directory.CreateTempSubdirectory();
        var emailPath = System.IO.Path.Combine(emailDir.FullName, $"fix_validation_{utcDate:yyyyMMdd}.csv");
        File.WriteAllText(emailPath, BuildReport(
            CreateRow(utcDate.AddHours(12), "WHALE", "E1", "5", "AAPL", "Failed", "Broker reject", "8=FIX.4.2|35=J|54=5", "Email", false, 1, 75m)));

        var originalEmailReports = Environment.GetEnvironmentVariable("FIXFLOW_CLI_REPORTS");
        Environment.SetEnvironmentVariable("FIXFLOW_CLI_REPORTS", emailDir.FullName);

        try
        {
            var controller = new DashboardController();
            var result = controller.GetSummary(
                new DateTimeOffset(utcDate, TimeSpan.Zero),
                new DateTimeOffset(utcDate.AddDays(1), TimeSpan.Zero),
                localOffsetMinutes: 0,
                rangeMode: "last7");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var summary = Assert.IsType<DashboardSummaryResponse>(ok.Value);

            Assert.Contains(summary.SourceTotals, item => item.SourceKey == "Direct" && item.AllocationCount == 2);
            Assert.Contains(summary.SourceTotals, item => item.SourceKey == "Email" && item.AllocationCount == 1);
            Assert.DoesNotContain(summary.SourceTotals, item => item.SourceKey == "Direct" && item.AllocationCount == 5);

            Assert.Contains(summary.OutcomeTotals, item => item.SourceKey == "Direct" && item.OutcomeKey == "Passed" && item.AllocationCount == 2);
            Assert.Contains(summary.OutcomeTotals, item => item.SourceKey == "Email" && item.OutcomeKey == "Failed" && item.AllocationCount == 1);
            Assert.DoesNotContain(summary.OutcomeTotals, item => item.SourceKey == "Direct" && item.OutcomeKey == "Failed");

            Assert.Contains(summary.SideAmountTotals, item => item.SourceKey == "Direct" && item.OutcomeKey == "Passed" && item.Label == "BUY" && item.GrossAmount == 100m);
            Assert.Contains(summary.SideAmountTotals, item => item.SourceKey == "Direct" && item.OutcomeKey == "" && item.Label == "CROSS" && item.GrossAmount == 60m);
            Assert.Contains(summary.SideAmountTotals, item => item.SourceKey == "Email" && item.OutcomeKey == "Failed" && item.Label == "SELL_SHORT" && item.GrossAmount == 75m);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FIXFLOW_CLI_REPORTS", originalEmailReports);
            emailDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetSummary_CustomRange_UsesPerDateBuckets()
    {
        var dayOne = DateTime.UtcNow.Date.AddDays(-3);
        var dayTwo = dayOne.AddDays(2);
        var firstPath = GetDirectReportPath(dayOne);
        var secondPath = GetDirectReportPath(dayTwo);

        using var firstScope = new TestFileScope(firstPath);
        using var secondScope = new TestFileScope(secondPath);
        firstScope.WriteAllText(BuildReport(
            CreateRow(dayOne.AddHours(9), "PEACE", "A1", "1", "MSFT", "Sent", "", "8=FIX.4.2|35=J|54=1", "Direct", false, 2, 50m)));
        secondScope.WriteAllText(BuildReport(
            CreateRow(dayTwo.AddHours(9), "PEACE", "A2", "2", "AAPL", "Sent", "", "8=FIX.4.2|35=J|54=2", "Direct", false, 1, 25m)));

        var controller = new DashboardController();
        var result = controller.GetSummary(
            new DateTimeOffset(dayOne, TimeSpan.Zero),
            new DateTimeOffset(dayTwo.AddDays(1), TimeSpan.Zero),
            localOffsetMinutes: 0,
            rangeMode: "custom");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<DashboardSummaryResponse>(ok.Value);

        Assert.Contains(summary.TimeSeriesTotals, item => item.BucketKey == dayOne.ToString("yyyy-MM-dd") && item.AllocationCount == 2);
        Assert.Contains(summary.TimeSeriesTotals, item => item.BucketKey == dayTwo.ToString("yyyy-MM-dd") && item.AllocationCount == 1);
    }

    private static string GetDirectReportPath(DateTime utcDate)
    {
        var reportsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "reports");
        Directory.CreateDirectory(reportsDir);
        return System.IO.Path.Combine(reportsDir, $"fix_validation_{utcDate:yyyyMMdd}.csv");
    }

    private static string BuildReport(params string[] rows)
    {
        const string header = "DateTime,ClientID,AllocID,Side,Symbol,ProcessingStatus,ErrorDetails,RawFixMessage,Source,IsDryRun,TradeCount,GrossAmount";
        return string.Join(Environment.NewLine, new[] { header }.Concat(rows)) + Environment.NewLine;
    }

    private static string CreateRow(
        DateTime timestampUtc,
        string clientId,
        string allocId,
        string side,
        string symbol,
        string status,
        string errorDetails,
        string rawFix,
        string source,
        bool isDryRun,
        int tradeCount,
        decimal grossAmount)
    {
        return string.Join(",",
            Quote(timestampUtc.ToString("o")),
            Quote(clientId),
            Quote(allocId),
            Quote(side),
            Quote(symbol),
            Quote(status),
            Quote(errorDetails),
            Quote(rawFix),
            Quote(source),
            Quote(isDryRun ? "true" : "false"),
            Quote(tradeCount.ToString()),
            Quote(grossAmount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
