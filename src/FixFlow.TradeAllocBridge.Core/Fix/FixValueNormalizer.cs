using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace FixFlow.TradeAllocBridge.Core.Fix;

/// <summary>
/// Converts free-form Excel values into FIX 4.2 compliant tag values,
/// using context from the current spreadsheet row when available.
/// </summary>
public static class FixValueNormalizer
{
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
            22 => string.IsNullOrEmpty(raw) ? "8" : raw.Trim(), // fallback to custom code if missing
            54 => NormalizeSide(raw),
            13 => NormalizeCommType(raw),
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
            _ => raw
        };
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

    private static string NormalizeSide(string value)
    {
        value = value.ToUpperInvariant();
        if (value == "BUY" || value == "B" || value == "COVER") return "1";
        if (value == "SELL" || value == "S" || value == "LONG") return "2";
        if (value.Contains("SSE") || value.Contains("SHORT EXEMPT")) return "6";
        if (value.Contains("SHORT")) return "5";
        return "1";
    }

    private static string NormalizeCommType(string value)
    {
        value = value.ToUpperInvariant();
        if (value.Contains("ABS") || value.StartsWith("A")) return "3";
        if (value.Contains("PER") && value.Contains("SHARE")) return "1";
        if (value.Contains("PCT") || value.Contains("PERCENT")) return "2";
        if (value.Contains("BOND")) return "6";
        return "1";
    }

    private static string NormalizeQuantity(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty)
            ? Math.Abs(qty).ToString("0.######", CultureInfo.InvariantCulture)
            : "0";

    private static string NormalizePrice(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
            ? Math.Abs(price).ToString("0.#####", CultureInfo.InvariantCulture)
            : "0";

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
}
