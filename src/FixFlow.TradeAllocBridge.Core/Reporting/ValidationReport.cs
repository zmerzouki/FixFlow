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
            File.WriteAllText(_reportPath, "DateTime,ClientID,AllocID,Side,Symbol,ProcessingStatus,ErrorDetails,RawFixMessage\n");
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
        var existingKeys = LoadExistingKeys();
        var sb = new StringBuilder();
        foreach (var entry in _entries)
        {
            var key = BuildKey(entry.DateTime, entry.ClientId, entry.AllocID, entry.Side, entry.Symbol,
                entry.ProcessingStatus, entry.ErrorDetails, entry.RawFixMessage);
            if (existingKeys.Contains(key))
            {
                continue;
            }

            existingKeys.Add(key);
            sb.AppendLine(string.Join(",", new[]
            {
                EscapeCsv(entry.DateTime),
                EscapeCsv(entry.ClientId),
                EscapeCsv(entry.AllocID),
                EscapeCsv(entry.Side),
                EscapeCsv(entry.Symbol),
                EscapeCsv(entry.ProcessingStatus),
                EscapeCsv(entry.ErrorDetails),
                EscapeCsv(entry.RawFixMessage)
            }));
        }

        if (sb.Length > 0)
        {
            File.AppendAllText(_reportPath, sb.ToString(), Encoding.UTF8);
        }
        _logger.LogInformation("FIX validation report written to {Path}", _reportPath);
    }

    private HashSet<string> LoadExistingKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_reportPath))
        {
            return keys;
        }

        try
        {
            foreach (var line in File.ReadLines(_reportPath).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var fields = SplitCsv(line);
                if (fields.Count < 8) continue;
                keys.Add(BuildKey(
                    fields[0],
                    fields[1],
                    fields[2],
                    fields[3],
                    fields[4],
                    fields[5],
                    fields[6],
                    fields[7]));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read existing validation report for de-duplication.");
        }

        return keys;
    }

    private static string BuildKey(
        string dateTime,
        string clientId,
        string allocId,
        string side,
        string symbol,
        string processingStatus,
        string errorDetails,
        string rawFixMessage)
    {
        return string.Join("|", new[]
        {
            dateTime,
            clientId,
            allocId,
            side,
            symbol,
            processingStatus,
            errorDetails,
            rawFixMessage
        });
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        result.Add(sb.ToString());
        return result;
    }

    private static string EscapeCsv(string? value)
    {
        var normalized = value ?? string.Empty;
        return $"\"{normalized.Replace("\"", "\"\"")}\"";
    }
}

public record ValidationResult(
    string DateTime,
    string ClientId,
    string AllocID,
    string Side,
    string Symbol,
    string ProcessingStatus,
    string ErrorDetails,
    string RawFixMessage
);
