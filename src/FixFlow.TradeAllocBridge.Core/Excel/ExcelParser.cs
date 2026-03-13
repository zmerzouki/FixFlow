using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using NPOI.HSSF.UserModel;
using System.Globalization;
using FixFlow.TradeAllocBridge.Core.Config;

namespace FixFlow.TradeAllocBridge.Core.Excel;

public class TradeRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
        return Parse(filePath, null);
    }

    public List<TradeRecord> Parse(string filePath, MappingConfig? mapping)
    {
        var trades = new List<TradeRecord>();

        if (!File.Exists(filePath))
        {
            _logger?.LogWarning("File not found: {File}", filePath);
            return trades;
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        var useColumnPositions = ShouldUseColumnPositions(mapping);

        return extension switch
        {
            ".csv" => ParseCsv(filePath, useColumnPositions, mapping),
            ".xls" => ParseXls(filePath, useColumnPositions, mapping),
            ".xlsx" or ".xlsm" or ".xltx" or ".xltm" => ParseXls(filePath, useColumnPositions, mapping), // use NPOI for both xls and xlsx to avoid ClosedXML dependency issues
            _ => throw new NotSupportedException($"Extension '{extension}' is not supported. Supported extensions are '.xlsx', '.xlsm', '.xltx', '.xltm', '.xls', and '.csv'.")
        };
    }

    private List<TradeRecord> ParseCsv(string filePath, bool useColumnPositions, MappingConfig? mapping)
    {
        var trades = new List<TradeRecord>();
        var lines = File.ReadAllLines(filePath);

        if (lines.Length == 0)
        {
            _logger?.LogWarning("CSV file is empty: {File}", filePath);
            return trades;
        }

        int headerRowIndex = -1;
        var headerColumns = new List<(int ColumnIndex, string Header)>();

        if (useColumnPositions)
        {
            headerColumns = BuildPositionalHeaders(lines);
            if (headerColumns.Count == 0)
            {
                _logger?.LogWarning("No data columns found in headless CSV file {File}", filePath);
                return trades;
            }

            _logger?.LogInformation("Parsing headless CSV file {File} using {Count} positional columns.",
                Path.GetFileName(filePath), headerColumns.Count);
        }
        else
        {
            // Parse headers (detect header row)
            int maxScan = Math.Min(lines.Length - 1, 50);

            for (int rowIndex = 0; rowIndex <= maxScan; rowIndex++)
            {
                var values = ParseCsvLine(lines[rowIndex]);
                var headers = new List<(int ColumnIndex, string Header)>();
                for (int i = 0; i < values.Count; i++)
                {
                    var header = values[i].Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(header)) continue;
                    if (!headers.Any(h => string.Equals(h.Header, header, StringComparison.OrdinalIgnoreCase)))
                    {
                        headers.Add((i, header));
                    }
                }

                if (headers.Count < 2) continue;

                if (headers.Count > headerColumns.Count)
                {
                    headerColumns = headers;
                    headerRowIndex = rowIndex;
                }
            }

            if (headerRowIndex < 0 || headerColumns.Count == 0)
            {
                _logger?.LogWarning("No headers found in CSV file {File}", filePath);
                return trades;
            }

            var headerNames = headerColumns.Select(h => h.Header).ToList();
            _logger?.LogInformation("Parsing CSV file {File} with {Count} headers: {Headers}",
                Path.GetFileName(filePath), headerNames.Count, string.Join(", ", headerNames));
        }

        // Parse data rows
        var startRowIndex = headerRowIndex + 1;
        for (int rowIndex = startRowIndex; rowIndex < lines.Length; rowIndex++)
        {
            var line = lines[rowIndex];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var values = ParseCsvLine(line);
            var record = new TradeRecord();
            bool anyValue = false;

            foreach (var headerInfo in headerColumns)
            {
                string header = headerInfo.Header;
                string cellValue = headerInfo.ColumnIndex < values.Count
                    ? values[headerInfo.ColumnIndex].Trim().Trim('"')
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(header))
                {
                    if (!record.Fields.ContainsKey(header))
                        record.Fields[header] = cellValue;
                    else
                        _logger?.LogDebug("Duplicate header '{Header}' ignored in {File}", header, filePath);
                }

                if (!string.IsNullOrWhiteSpace(cellValue))
                    anyValue = true;
            }

            if (record.Fields.Count > 0 && anyValue)
            {
                if (ShouldSkipRecord(record, mapping))
                {
                    continue;
                }
                trades.Add(record);
            }
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

    private List<TradeRecord> ParseXls(string filePath, bool useColumnPositions, MappingConfig? mapping)
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

        int headerRowIndex;
        List<(int ColumnIndex, string Header)> headerColumns;

        if (useColumnPositions)
        {
            headerRowIndex = -1;
            headerColumns = BuildPositionalHeaders(sheet);
            if (headerColumns.Count == 0)
            {
                _logger?.LogWarning("No data columns found in headless Excel file {File}", filePath);
                return trades;
            }

            _logger?.LogInformation("Parsing headless Excel file {File} using {Count} positional columns.",
                Path.GetFileName(filePath), headerColumns.Count);
        }
        else
        {
            // Parse headers (detect header row)
            (headerRowIndex, headerColumns) = FindHeaderRow(sheet);
            if (headerRowIndex < 0 || headerColumns.Count == 0)
            {
                _logger?.LogWarning("No header row found in Excel file {File}", filePath);
                return trades;
            }

            var headers = headerColumns.Select(h => h.Header).ToList();
            _logger?.LogInformation("Parsing Excel file {File} with {Count} headers: {Headers}",
                Path.GetFileName(filePath), headers.Count, string.Join(", ", headers));
        }

        // Parse data rows
        var startRowIndex = headerRowIndex + 1;
        for (int rowIndex = startRowIndex; rowIndex <= sheet.LastRowNum; rowIndex++)
        {
            var row = sheet.GetRow(rowIndex);
            if (row == null)
                continue;

            var record = new TradeRecord();
            bool anyValue = false;

            foreach (var headerInfo in headerColumns)
            {
                string header = headerInfo.Header;
                var cell = row.GetCell(headerInfo.ColumnIndex);
                string cellValue = cell != null ? GetCellValue(cell).Trim() : string.Empty;

                if (!string.IsNullOrWhiteSpace(header))
                {
                    if (!record.Fields.ContainsKey(header))
                        record.Fields[header] = cellValue;
                    else
                        _logger?.LogDebug("Duplicate header '{Header}' ignored in {File}", header, filePath);
                }

                if (!string.IsNullOrWhiteSpace(cellValue))
                    anyValue = true;
            }

            if (record.Fields.Count > 0 && anyValue)
            {
                if (ShouldSkipRecord(record, mapping))
                {
                    continue;
                }
                trades.Add(record);
            }
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

    private (int RowIndex, List<(int ColumnIndex, string Header)> Headers) FindHeaderRow(ISheet sheet)
    {
        int bestRow = -1;
        List<(int ColumnIndex, string Header)> bestHeaders = new();
        int maxScan = Math.Min(sheet.LastRowNum, 50);

        for (int rowIndex = 0; rowIndex <= maxScan; rowIndex++)
        {
            var row = sheet.GetRow(rowIndex);
            if (row == null) continue;

            var headers = new List<(int ColumnIndex, string Header)>();
            for (int i = 0; i < row.LastCellNum; i++)
            {
                var cell = row.GetCell(i);
                if (cell == null) continue;
                string header = GetCellValue(cell).Trim();
                if (string.IsNullOrWhiteSpace(header)) continue;

                if (!headers.Any(h => string.Equals(h.Header, header, StringComparison.OrdinalIgnoreCase)))
                    headers.Add((i, header));
            }

            if (headers.Count < 2) continue;

            if (headers.Count > bestHeaders.Count)
            {
                bestRow = rowIndex;
                bestHeaders = headers;
            }
        }

        if (bestRow < 0)
        {
            var fallback = sheet.GetRow(0);
            if (fallback == null) return (-1, new List<(int, string)>());

            var headers = new List<(int ColumnIndex, string Header)>();
            for (int i = 0; i < fallback.LastCellNum; i++)
            {
                var cell = fallback.GetCell(i);
                if (cell == null) continue;
                string header = GetCellValue(cell).Trim();
                if (string.IsNullOrWhiteSpace(header)) continue;
                if (!headers.Any(h => string.Equals(h.Header, header, StringComparison.OrdinalIgnoreCase)))
                    headers.Add((i, header));
            }

            return (headers.Count > 0 ? 0 : -1, headers);
        }

        return (bestRow, bestHeaders);
    }

    private static bool ShouldUseColumnPositions(MappingConfig? mapping)
    {
        if (mapping?.TradeAllocations == null || mapping.TradeAllocations.Count == 0)
        {
            return false;
        }

        return mapping.TradeAllocations.Keys.Any(key =>
            int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index > 0);
    }

    private static List<(int ColumnIndex, string Header)> BuildPositionalHeaders(string[] lines)
    {
        var maxColumns = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = ParseCsvLineStatic(line);
            if (values.Count > maxColumns)
            {
                maxColumns = values.Count;
            }
        }

        if (maxColumns <= 0)
        {
            return new List<(int, string)>();
        }

        var headers = new List<(int ColumnIndex, string Header)>(maxColumns);
        for (int i = 0; i < maxColumns; i++)
        {
            headers.Add((i, (i + 1).ToString(CultureInfo.InvariantCulture)));
        }

        return headers;
    }

    private static List<(int ColumnIndex, string Header)> BuildPositionalHeaders(ISheet sheet)
    {
        var maxColumns = 0;
        for (int rowIndex = 0; rowIndex <= sheet.LastRowNum; rowIndex++)
        {
            var row = sheet.GetRow(rowIndex);
            if (row == null || row.LastCellNum <= 0) continue;

            var lastNonEmpty = -1;
            for (int i = 0; i < row.LastCellNum; i++)
            {
                var cell = row.GetCell(i);
                if (cell == null) continue;
                var value = GetCellValueStatic(cell).Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    lastNonEmpty = i;
                }
            }

            if (lastNonEmpty >= 0)
            {
                maxColumns = Math.Max(maxColumns, lastNonEmpty + 1);
            }
        }

        if (maxColumns <= 0)
        {
            return new List<(int, string)>();
        }

        var headers = new List<(int ColumnIndex, string Header)>(maxColumns);
        for (int i = 0; i < maxColumns; i++)
        {
            headers.Add((i, (i + 1).ToString(CultureInfo.InvariantCulture)));
        }

        return headers;
    }

    private static List<string> ParseCsvLineStatic(string line)
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
                    currentValue.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                values.Add(currentValue.ToString());
                currentValue.Clear();
            }
            else
            {
                currentValue.Append(c);
            }
        }

        values.Add(currentValue.ToString());
        return values;
    }

    private static string GetCellValueStatic(ICell cell)
    {
        if (cell == null)
            return string.Empty;

        if (cell.CellType == CellType.Formula)
        {
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

    private static bool ShouldSkipRecord(TradeRecord record, MappingConfig? mapping)
    {
        if (mapping?.TradeAllocations == null || mapping.TradeAllocations.Count == 0)
        {
            return false;
        }

        var mappedValues = new List<string>();
        foreach (var key in mapping.TradeAllocations.Keys)
        {
            if (record.Fields.TryGetValue(key, out var value))
            {
                mappedValues.Add(value ?? string.Empty);
            }
        }

        if (mappedValues.Count == 0)
        {
            return false;
        }

        var nonEmptyValues = mappedValues
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        if (nonEmptyValues.Count < 3)
        {
            return true;
        }

        return IsRepeatedCharRow(nonEmptyValues);
    }

    private static bool IsRepeatedCharRow(IReadOnlyList<string> values)
    {
        if (values.Count < 2)
        {
            return false;
        }

        char? repeatedChar = null;
        foreach (var value in values)
        {
            if (value.Length == 0)
            {
                continue;
            }

            var candidate = value[0];
            if (char.IsLetterOrDigit(candidate))
            {
                return false;
            }
            for (var i = 1; i < value.Length; i++)
            {
                if (value[i] != candidate)
                {
                    return false;
                }
            }

            if (repeatedChar == null)
            {
                repeatedChar = candidate;
            }
            else if (repeatedChar.Value != candidate)
            {
                return false;
            }
        }

        return repeatedChar != null;
    }

    // ClosedXML parser removed in favor of NPOI-based path to avoid font-related dependency issues.
}
