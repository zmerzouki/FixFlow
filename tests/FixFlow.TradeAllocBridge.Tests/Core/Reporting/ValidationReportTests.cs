using FixFlow.TradeAllocBridge.Core.Reporting;
using FixFlow.TradeAllocBridge.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FixFlow.TradeAllocBridge.Tests.Core.Reporting;

public class ValidationReportTests
{
    [Fact]
    public void Constructor_UpgradesLegacyHeader()
    {
        var reportPath = GetTodayReportPath();
        using var scope = new TestFileScope(reportPath);

        scope.WriteAllText("""
        DateTime,ClientID,AllocID,Side,Symbol,ProcessingStatus,ErrorDetails,RawFixMessage
        "2026-05-01T00:00:00Z","PEACE","A1","1","MSFT","Sent","",""
        """);

        _ = new ValidationReport(NullLogger<ValidationReport>.Instance);

        var header = File.ReadLines(reportPath).First();
        Assert.Equal("DateTime,ClientID,AllocID,Side,Symbol,ProcessingStatus,ErrorDetails,RawFixMessage,Source,IsDryRun,TradeCount,GrossAmount", header);
    }

    [Fact]
    public void Save_DeduplicatesRepeatedEntries()
    {
        var reportPath = GetTodayReportPath();
        using var scope = new TestFileScope(reportPath);

        if (File.Exists(reportPath))
        {
            File.Delete(reportPath);
        }

        var report = new ValidationReport(NullLogger<ValidationReport>.Instance);
        var entry = new ValidationResult(
            "2026-05-01T10:00:00Z",
            "PEACE",
            "ALLOC-1",
            "1",
            "MSFT",
            "Sent",
            string.Empty,
            "8=FIX.4.2|35=J|54=1",
            "Direct",
            false,
            2,
            100m);

        report.Add(entry);
        report.Add(entry);
        report.Save();

        var lines = File.ReadAllLines(reportPath);
        Assert.Equal(2, lines.Length);
    }

    private static string GetTodayReportPath()
    {
        var reportsDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "reports");
        Directory.CreateDirectory(reportsDir);
        return System.IO.Path.Combine(reportsDir, $"fix_validation_{DateTime.UtcNow:yyyyMMdd}.csv");
    }
}
