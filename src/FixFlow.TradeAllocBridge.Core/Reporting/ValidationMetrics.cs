using System.Globalization;
using FixFlow.TradeAllocBridge.Core.Excel;

namespace FixFlow.TradeAllocBridge.Core.Reporting;

public static class ValidationMetrics
{
    public static decimal CalculateGrossAmount(IEnumerable<TradeRecord> trades, string? quantityColumn, string? priceColumn)
    {
        if (trades == null)
        {
            return 0m;
        }

        decimal total = 0m;
        foreach (var trade in trades)
        {
            total += CalculateGrossAmount(trade, quantityColumn, priceColumn);
        }

        return total;
    }

    public static decimal CalculateGrossAmount(TradeRecord? trade, string? quantityColumn, string? priceColumn)
    {
        if (trade == null ||
            string.IsNullOrWhiteSpace(quantityColumn) ||
            string.IsNullOrWhiteSpace(priceColumn))
        {
            return 0m;
        }

        var quantity = GetDecimalFieldValue(trade, quantityColumn);
        var price = GetDecimalFieldValue(trade, priceColumn);
        if (quantity is null || price is null)
        {
            return 0m;
        }

        return quantity.Value * price.Value;
    }

    public static decimal? GetDecimalFieldValue(TradeRecord trade, string? columnName)
    {
        if (trade == null || string.IsNullOrWhiteSpace(columnName))
        {
            return null;
        }

        if (!trade.Fields.TryGetValue(columnName, out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        return TryParseDecimal(rawValue, out var parsed) ? parsed : null;
    }

    public static bool TryParseDecimal(string? rawValue, out decimal parsed)
    {
        parsed = 0m;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        var trimmed = rawValue.Trim();
        if (decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
        {
            return true;
        }

        if (decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.CurrentCulture, out parsed))
        {
            return true;
        }

        var normalized = trimmed.Replace(",", string.Empty);
        if (!string.Equals(normalized, trimmed, StringComparison.Ordinal) &&
            decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
        {
            return true;
        }

        return false;
    }
}
