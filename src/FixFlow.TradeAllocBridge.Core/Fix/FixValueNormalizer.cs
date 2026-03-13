using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using FixFlow.TradeAllocBridge.Core.Excel;
using FixFlow.TradeAllocBridge.Core.Config;
using System.Xml.Linq;

namespace FixFlow.TradeAllocBridge.Core.Fix;

/// <summary>
/// Converts free-form Excel values into FIX 4.2 compliant tag values,
/// using context from the current spreadsheet row when available.
/// </summary>
public static class FixValueNormalizer
{
    private static readonly Lazy<HashSet<int>> NumericTags = new(LoadNumericTags);
    private static readonly Regex AllocAccountRegex = new(@"(?:^|\s)@([A-Za-z0-9][A-Za-z0-9._-]*)", RegexOptions.Compiled);
    private static readonly Regex AlphaNumRegex = new(@"^[A-Za-z0-9]+$", RegexOptions.Compiled);
    /// <summary>
    /// Backwards-compatible normalization (no row context).
    /// </summary>
    public static string Normalize(int tag, string raw) => Normalize(tag, raw, null);

    /// <summary>
    /// Normalizes a FIX field value based on tag number and, optionally, the entire spreadsheet row.
    /// </summary>
    public static string Normalize(int tag, string? raw, IReadOnlyDictionary<string, string>? row)
    {
        raw ??= string.Empty;

        // --- Tag 48/22 logic: try row-level inference first ---
        if (row != null && (tag == 48 || tag == 22))
        {
            var (secId, idSource) = GetSecurityIdAndSource(row);

            if (tag == 48 && !string.IsNullOrEmpty(secId))
                return secId;

            if (tag == 22 && !string.IsNullOrEmpty(idSource))
                return idSource;
        }

        // --- Default per-tag normalization ---
        raw = raw.Trim();

        return tag switch
        {
            48 => NormalizeSecID(raw),
            22 => raw, // no default; only explicit or inferred values
            54 => NormalizeSide(raw),
            13 => NormalizeCommType(raw),
            79 => NormalizeAllocAccount(raw),
            53 => NormalizeQuantity(raw),
            31 => NormalizePrice(raw),
            6 => NormalizePrice(raw),
            32 => NormalizeQuantity(raw),
            38 => NormalizeQuantity(raw),
            44 => NormalizePrice(raw),
            80 => NormalizeQuantity(raw),
            153 => NormalizePrice(raw),
            75 => NormalizeTradeDate(raw),
            15 => raw.ToUpperInvariant(), // Currency
            _ => NormalizeNumericIfNeeded(tag, raw)
        };
    }

    public static string FormatSideDisplay(string? raw)
    {
        var trimmed = raw?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var upper = trimmed.ToUpperInvariant();
        string? normalized = null;

        if (IsSideEnumValue(upper))
        {
            normalized = upper;
        }
        else if (IsKnownSideToken(upper))
        {
            normalized = NormalizeSide(upper);
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return trimmed;
        }

        var description = normalized switch
        {
            "1" => "BUY",
            "2" => "SELL",
            "3" => "BUY_MINUS",
            "4" => "SELL_PLUS",
            "5" => "SELL_SHORT",
            "6" => "SELL_SHORT_EXEMPT",
            "7" => "UNDISCLOSED",
            "8" => "CROSS",
            "9" => "CROSS_SHORT",
            _ => string.Empty
        };

        return string.IsNullOrWhiteSpace(description)
            ? normalized
            : $"{normalized} ({description})";
    }

    /// <summary>
    /// Determines SecurityID and IDSource based on CUSIP / ISIN / SEDOL priority.
    /// </summary>
    public static (string secId, string idSource) GetSecurityIdAndSource(IReadOnlyDictionary<string, string> row)
    {
        if (TryGetNonEmpty(row, "CUSIP", out var cusip))
            return (cusip.Trim(), "1"); // CUSIP

        if (TryGetNonEmpty(row, "ISIN", out var isin))
            return (isin.Trim().ToUpperInvariant(), "4"); // ISIN

        if (TryGetNonEmpty(row, "SEDOL", out var sedol))
            return (sedol.Trim().ToUpperInvariant(), "2"); // SEDOL

        // None found → empty values (handled by builder defaults)
        return (string.Empty, string.Empty);
    }

    /// <summary>
    /// Infers SecurityIDSource (tag 22) from the SecurityID (tag 48) format.
    /// </summary>
    public static string? InferSecurityIdSourceFromValue(string? securityId)
    {
        if (string.IsNullOrWhiteSpace(securityId))
        {
            return null;
        }

        var trimmed = securityId.Trim();
        if (!AlphaNumRegex.IsMatch(trimmed))
        {
            return null;
        }

        return trimmed.Length switch
        {
            9 => "1",   // CUSIP
            7 => "2",   // SEDOL
            4 => "3",   // QUIK
            5 => "3",   // QUIK
            12 => "4",  // ISIN
            _ => null
        };
    }

    public static bool IsValidSecurityIdSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 1 && char.IsLetterOrDigit(trimmed[0]);
    }

    private static bool TryGetNonEmpty(
        IReadOnlyDictionary<string, string> row,
        string key,
        [NotNullWhen(true)] out string? value)
    {
        static bool HasValue(string? v) => !string.IsNullOrWhiteSpace(v);

        // Direct, lowercase, and uppercase variants
        if (row.TryGetValue(key, out var v1) && HasValue(v1)) { value = v1; return true; }
        if (row.TryGetValue(key.ToLowerInvariant(), out var v2) && HasValue(v2)) { value = v2; return true; }
        if (row.TryGetValue(key.ToUpperInvariant(), out var v3) && HasValue(v3)) { value = v3; return true; }

        value = null;
        return false;
    }

    private static string NormalizeSecID(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeAllocAccount(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        var match = AllocAccountRegex.Match(trimmed);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return trimmed;
    }

    private static string NormalizeSide(string value)
    {
        value = value.ToUpperInvariant();
        if (value == "BUY" || value == "B" || value == "BOUGHT" || value.Contains("COVER")) return "1";
        if (value == "SELL" || value == "S" || value == "SOLD" || value == "LONG") return "2";
        if (value.Contains("SSE") || value.Contains("EXEMPT") || value.Contains("SHORT EXEMPT")) return "6";
        if (value.Contains("SHORT")) return "5";
        return "1";
    }

    private static bool IsSideEnumValue(string value) =>
        value.Length == 1 && value[0] >= '1' && value[0] <= '9';

    private static bool IsKnownSideToken(string value) =>
        value == "BUY"
        || value == "B"
        || value == "BOUGHT" 
        || value == "COVER"
        || value == "SELL"
        || value == "S"
        || value == "LONG"
        || value.Contains("SSE")
        || value.Contains("SHORT EXEMPT")
        || value.Contains("SHORT");

        private static string MapCommType(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 1 && char.IsDigit(trimmed[0]))
        {
            return trimmed; // already a FIX comm type code
        }

        var upper = trimmed.ToUpperInvariant();

        if (upper.Contains("CENT") || (upper.Contains("PER") && upper.Contains("SHARE")))
            return "1"; // cents per share

        if (upper.Contains("ABS") || upper.Contains("ABSOLUTE") || upper.StartsWith("A"))
            return "3"; // absolute

        if (upper.Contains("PCT") || upper.Contains("PERCENT"))
            return "2"; // percent

        if (upper.Contains("BOND"))
            return "6";

        return "1"; // default
    }

    private static string NormalizeCommType(string value) => MapCommType(value);

    public static bool TryNormalizeCommission(string commTypeRaw, string commissionRaw, out string commType, out string commValue)
    {
        commType = string.Empty;
        commValue = string.Empty;

        if (string.IsNullOrWhiteSpace(commTypeRaw) || string.IsNullOrWhiteSpace(commissionRaw))
        {
            return false;
        }

        commType = MapCommType(commTypeRaw);

        if (!decimal.TryParse(commissionRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var commission))
        {
            return false;
        }

        var normalized = commType switch
        {
            // For commType "1" (cents per share) if the provided value is >= 1 treat it as cents and divide by 100,
            // otherwise assume it's already a decimal representation and leave it unchanged.
            "1" => commission >= 1m ? commission / 100m : commission,
            "2" => commission,
            "3" => Math.Abs(commission),
            _ => commission
        };

        commValue = normalized.ToString("0.######", CultureInfo.InvariantCulture);
        return true;
    }

    private static string NormalizeQuantity(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty)
            ? Math.Abs(qty).ToString("0.######", CultureInfo.InvariantCulture)
            : "0";

    private static string NormalizePrice(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
            ? Math.Abs(price).ToString("0.#####", CultureInfo.InvariantCulture)
            : "0";

    private static string NormalizeNumericIfNeeded(int tag, string value)
    {
        var raw = value?.Trim() ?? string.Empty;
        if (raw.Length == 0) return raw;

        if (!NumericTags.Value.Contains(tag))
        {
            return raw;
        }

        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            return raw;
        }

        var normalized = Math.Abs(number);
        return normalized.ToString(CultureInfo.InvariantCulture);
    }

    private static string NormalizeTradeDate(string value)
    {
        var formats = new[] { "yyyyMMdd", "yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy" };

        foreach (var fmt in formats)
        {
            if (DateTime.TryParseExact(value, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return date.ToString("yyyyMMdd");
        }

        // fallback to today's date
        return DateTime.UtcNow.ToString("yyyyMMdd");
    }

    private static HashSet<int> LoadNumericTags()
    {
        var tags = new HashSet<int>();

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.xml");
            if (!File.Exists(path))
            {
                return tags;
            }

            var numericTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AMT",
                "PRICE",
                "QTY",
                "FLOAT",
                "INT",
                "NUMINGROUP",
                "LENGTH",
                "SEQNUM",
                "PRICEOFFSET",
                "PERCENTAGE",
                "DAYOFMONTH"
            };

            var doc = XDocument.Load(path);
            var fields = doc.Root?.Element("fields")?.Elements("field");
            if (fields == null) return tags;

            foreach (var field in fields)
            {
                var type = (string?)field.Attribute("type");
                var number = (string?)field.Attribute("number");

                if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(number)) continue;
                if (!numericTypes.Contains(type)) continue;
                if (!int.TryParse(number, out var tag)) continue;

                tags.Add(tag);
            }
        }
        catch
        {
            return tags;
        }

        return tags;
    }

    public static bool IsNumericTag(int tag) => NumericTags.Value.Contains(tag);

    public static IReadOnlyList<NumericFieldIssue> FindNumericFieldIssues(
        IEnumerable<TradeRecord> trades,
        MappingConfig mapping)
    {
        var issues = new List<NumericFieldIssue>();
        if (mapping?.TradeAllocations == null || mapping.TradeAllocations.Count == 0)
        {
            return issues;
        }

        foreach (var kvp in mapping.TradeAllocations)
        {
            if (!TryParseTagNumber(kvp.Value, out var tag))
            {
                continue;
            }

            if (!IsNumericTag(tag))
            {
                continue;
            }

            foreach (var trade in trades)
            {
                if (!trade.Fields.TryGetValue(kvp.Key, out var raw))
                {
                    continue;
                }

                var trimmed = raw?.Trim() ?? string.Empty;
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (!decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                {
                    issues.Add(new NumericFieldIssue(kvp.Key, tag, trimmed));
                    break;
                }
            }
        }

        return issues;
    }

    public static string FormatColumnLabel(string columnKey)
    {
        return int.TryParse(columnKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            ? $"Column {index}"
            : columnKey;
    }

    public static bool TryParseTagNumber(string? value, out int tag)
    {
        tag = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var match = Regex.Match(trimmed, @"\d+");
        if (!match.Success)
        {
            return false;
        }

        return int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out tag);
    }
}

public record NumericFieldIssue(string ColumnKey, int Tag, string SampleValue);
