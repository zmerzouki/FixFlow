using ClosedXML.Excel;
using Microsoft.Extensions.Logging;

namespace RJF.TradeAllocBridge.Core.Excel;

public class TradeRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public Dictionary<string, string> Fields { get; set; } = new();
}

public class ExcelParser
{
    private readonly ILogger<ExcelParser>? _logger;

    public ExcelParser(ILogger<ExcelParser>? logger = null)
    {
        _logger = logger;
    }

    public List<TradeRecord> Parse(string filePath)
    {
        var trades = new List<TradeRecord>();

        if (!File.Exists(filePath))
        {
            _logger?.LogWarning("Excel file not found: {File}", filePath);
            return trades;
        }

        using var workbook = new XLWorkbook(filePath);
        var ws = workbook.Worksheets.FirstOrDefault();

        if (ws == null)
        {
            _logger?.LogWarning("No worksheets found in {File}", filePath);
            return trades;
        }

        var headers = ws.FirstRowUsed()
            ?.Cells()
            .Select(c => c.GetValue<string>().Trim())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (headers == null || headers.Count == 0)
        {
            _logger?.LogWarning("No headers found in Excel file {File}", filePath);
            return trades;
        }

        _logger?.LogInformation("Parsing Excel file {File} with {Count} headers: {Headers}",
            Path.GetFileName(filePath), headers.Count, string.Join(", ", headers));

        foreach (var row in ws.RowsUsed().Skip(1))
        {
            var record = new TradeRecord();

            for (int i = 0; i < headers.Count; i++)
            {
                string header = headers[i];
                string cellValue = string.Empty;

                try
                {
                    cellValue = row.Cell(i + 1).GetValue<string>()?.Trim() ?? string.Empty;
                }
                catch
                {
                    _logger?.LogDebug("Invalid cell at Row {Row}, Col {Col} in {File}", row.RowNumber(), i + 1, filePath);
                }

                // Guard: prevent duplicate or null keys
                if (!string.IsNullOrWhiteSpace(header))
                {
                    if (!record.Fields.ContainsKey(header))
                        record.Fields[header] = cellValue;
                    else
                        _logger?.LogDebug("Duplicate header '{Header}' ignored in {File}", header, filePath);
                }
            }

            // Guard: ensure required structure
            if (record.Fields.Count > 0)
                trades.Add(record);
        }

        _logger?.LogInformation("Parsed {Count} trade record(s) from {File}", trades.Count, Path.GetFileName(filePath));
        return trades;
    }
}
