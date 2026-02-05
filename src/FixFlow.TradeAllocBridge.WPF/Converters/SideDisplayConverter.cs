using System;
using System.Globalization;
using System.Windows.Data;
using FixFlow.TradeAllocBridge.Core.Fix;

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

        return FixValueNormalizer.FormatSideDisplay(raw);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
