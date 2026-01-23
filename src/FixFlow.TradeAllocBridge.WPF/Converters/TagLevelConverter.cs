using System;
using System.Globalization;
using System.Windows.Data;

namespace FixFlow.TradeAllocBridge.WPF.Converters;

public class TagLevelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var tag = value?.ToString() ?? string.Empty;
        return FixTagLevelResolver.GetLevel(tag);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
