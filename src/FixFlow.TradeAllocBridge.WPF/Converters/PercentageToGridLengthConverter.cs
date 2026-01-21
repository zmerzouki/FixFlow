using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FixFlow.TradeAllocBridge.WPF.Converters
{
    /// <summary>
    /// Converts a numeric percentage into a star GridLength so column widths scale proportionally.
    /// </summary>
    public class PercentageToGridLengthConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is double d && !double.IsNaN(d) && d > 0)
            {
                return new GridLength(d, GridUnitType.Star);
            }

            return new GridLength(0, GridUnitType.Star);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
