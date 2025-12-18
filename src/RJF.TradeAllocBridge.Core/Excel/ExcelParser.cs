using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using NPOI.HSSF.UserModel;
using System.Globalization;

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
            _logger?.LogWarning("File not found: {File}", filePath);
            return trades;
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        return extension switch
        {
            ".csv" => ParseCsv(filePath),
            ".xls" => ParseXls(filePath),
            ".xlsx" or ".xlsm" or ".xltx" or ".xltm" => ParseXlsx(filePath),
            _ => throw new NotSupportedException($"Extension '{extension}' is not supported. Supported extensions are '.xlsx', '.xlsm', '.xltx', '.xltm', '.xls', and '.csv'.")
        };
    }

    private List<TradeRecord> ParseCsv(string filePath)
    {
        var trades = new List<TradeRecord>();
        var lines = File.ReadAllLines(filePath);

        if (lines.Length == 0)
        {
            _logger?.LogWarning("CSV file is empty: {File}", filePath);
            return trades;
        }

        // Parse headers (first line)
        var headers = lines[0]
            .Split(',')
            .Select(h => h.Trim().Trim('"'))
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (headers.Count == 0)
        {
            _logger?.LogWarning("No headers found in CSV file {File}", filePath);
            return trades;
        }

        _logger?.LogInformation("Parsing CSV file {File} with {Count} headers: {Headers}",
            Path.GetFileName(filePath), headers.Count, string.Join(", ", headers));

        // Parse data rows
        for (int rowIndex = 1; rowIndex < lines.Length; rowIndex++)
        {
            var line = lines[rowIndex];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var values = ParseCsvLine(line);
            var record = new TradeRecord();

            for (int i = 0; i < headers.Count && i < values.Count; i++)
            {
                string header = headers[i];
                string cellValue = values[i].Trim().Trim('"');

                if (!string.IsNullOrWhiteSpace(header))
                {
                    if (!record.Fields.ContainsKey(header))
                        record.Fields[header] = cellValue;
                    else
                        _logger?.LogDebug("Duplicate header '{Header}' ignored in {File}", header, filePath);
                }
            }

            if (record.Fields.Count > 0)
                trades.Add(record);
        }

        _logger?.LogInformation("Parsed {Count} trade record(s) from CSV {File}", trades.Count, Path.GetFileName(filePath));
        return trades;
    }

    private List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var currentValue = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    // Escaped quote
                    currentValue.Append('"');
                    i++;
                }
                else
                {
                    // Toggle quote state
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                // End of field
                values.Add(currentValue.ToString());
                currentValue.Clear();
            }
            else
            {
                currentValue.Append(c);
            }
        }

        // Add last field
        values.Add(currentValue.ToString());
        return values;
    }

    private List<TradeRecord> ParseXls(string filePath)
    {
        var trades = new List<TradeRecord>();

        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        IWorkbook workbook;

        if (Path.GetExtension(filePath).Equals(".xls", StringComparison.OrdinalIgnoreCase))
        {
            workbook = new HSSFWorkbook(fileStream);
        }
        else
        {
            workbook = new XSSFWorkbook(fileStream);
        }

        var sheet = workbook.GetSheetAt(0);
        if (sheet == null || sheet.LastRowNum < 0)
        {
            _logger?.LogWarning("No data found in Excel file {File}", filePath);
            return trades;
        }

        // Parse headers (first row)
        var headerRow = sheet.GetRow(0);
        if (headerRow == null)
        {
            _logger?.LogWarning("No header row found in Excel file {File}", filePath);
            return trades;
        }

        var headers = new List<string>();
        for (int i = 0; i < headerRow.LastCellNum; i++)
        {
            var cell = headerRow.GetCell(i);
            if (cell != null)
            {
                string header = GetCellValue(cell).Trim();
                if (!string.IsNullOrWhiteSpace(header) && !headers.Contains(header, StringComparer.OrdinalIgnoreCase))
                    headers.Add(header);
            }
        }

        if (headers.Count == 0)
        {
            _logger?.LogWarning("No valid headers found in Excel file {File}", filePath);
            return trades;
        }

        _logger?.LogInformation("Parsing Excel file {File} with {Count} headers: {Headers}",
            Path.GetFileName(filePath), headers.Count, string.Join(", ", headers));

        // Parse data rows
        for (int rowIndex = 1; rowIndex <= sheet.LastRowNum; rowIndex++)
        {
            var row = sheet.GetRow(rowIndex);
            if (row == null)
                continue;

            var record = new TradeRecord();

            for (int i = 0; i < headers.Count; i++)
            {
                string header = headers[i];
                var cell = row.GetCell(i);
                string cellValue = cell != null ? GetCellValue(cell).Trim() : string.Empty;

                if (!string.IsNullOrWhiteSpace(header))
                {
                    if (!record.Fields.ContainsKey(header))
                        record.Fields[header] = cellValue;
                    else
                        _logger?.LogDebug("Duplicate header '{Header}' ignored in {File}", header, filePath);
                }
            }

            if (record.Fields.Count > 0)
                trades.Add(record);
        }

        _logger?.LogInformation("Parsed {Count} trade record(s) from Excel {File}", trades.Count, Path.GetFileName(filePath));
        return trades;
    }

    private string GetCellValue(ICell cell)
    {
        if (cell == null)
            return string.Empty;

        if (cell.CellType == CellType.Formula)
        {
            // Evaluate formula and get the cached result
            if (cell.CachedFormulaResultType == CellType.Numeric && DateUtil.IsCellDateFormatted(cell))
            {
                var dateValue = cell.DateCellValue;
                return dateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
            }
            
            return cell.CachedFormulaResultType switch
            {
                CellType.String => cell.StringCellValue ?? string.Empty,
                CellType.Numeric => cell.NumericCellValue.ToString(CultureInfo.InvariantCulture),
                CellType.Boolean => cell.BooleanCellValue.ToString(),
                _ => string.Empty
            };
        }

        if (cell.CellType == CellType.Numeric && DateUtil.IsCellDateFormatted(cell))
        {
            var dateValue = cell.DateCellValue;
            return dateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return cell.CellType switch
        {
            CellType.String => cell.StringCellValue ?? string.Empty,
            CellType.Numeric => cell.NumericCellValue.ToString(CultureInfo.InvariantCulture),
            CellType.Boolean => cell.BooleanCellValue.ToString(),
            _ => string.Empty
        };
    }

    private List<TradeRecord> ParseXlsx(string filePath)
    {
        var trades = new List<TradeRecord>();

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
