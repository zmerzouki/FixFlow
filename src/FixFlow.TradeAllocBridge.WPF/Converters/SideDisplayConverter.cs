using System;
using System.Globalization;
using System.Windows.Data;

namespace FixFlow.TradeAllocBridge.WPF.Converters;

public class SideDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var raw = value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return raw switch
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
            _ => raw
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
