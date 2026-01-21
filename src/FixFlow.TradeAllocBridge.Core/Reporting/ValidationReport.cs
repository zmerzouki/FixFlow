using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace FixFlow.TradeAllocBridge.Core.Reporting;

public class ValidationReport
{
    private readonly string _reportPath;
    private readonly List<ValidationResult> _entries = new();
    private readonly ILogger<ValidationReport> _logger;

    public ValidationReport(ILogger<ValidationReport> logger)
    {
        _logger = logger;
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "reports");
        Directory.CreateDirectory(dir);
        _reportPath = Path.Combine(dir, $"fix_validation_{DateTime.UtcNow:yyyyMMdd}.csv");

        if (!File.Exists(_reportPath))
        {
            File.WriteAllText(_reportPath, "DateTime,TradeId,AllocID,Symbol,Quantity,Price,Account,ValidationStatus,ErrorMessage,RawFixMessage\n");
        }
    }

    public void Add(ValidationResult result)
    {
        lock (_entries)
        {
            _entries.Add(result);
        }
    }

    public void Save()
    {
        var sb = new StringBuilder();
        foreach (var entry in _entries)
        {
            sb.AppendLine(string.Join(",", new string[]
            {
                entry.DateTime,
                entry.TradeId,
                entry.AllocID,
                entry.Symbol,
                entry.Quantity,
                entry.Price,
                entry.Account,
                entry.ValidationStatus,
                entry.ErrorMessage.Replace(",", ";"),
                "\"" + entry.RawFixMessage.Replace("\"", "'") + "\"" // quote and escape
            }));
        }

        File.AppendAllText(_reportPath, sb.ToString(), Encoding.UTF8);
        _logger.LogInformation("FIX validation report written to {Path}", _reportPath);
    }
}

public record ValidationResult(
    string DateTime,
    string TradeId,
    string AllocID,
    string Symbol,
    string Quantity,
    string Price,
    string Account,
    string ValidationStatus,
    string ErrorMessage,
    string RawFixMessage
);
