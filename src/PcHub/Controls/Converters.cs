using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PcHub.Controls;

/// <summary>Group keys are "1|SFW" so they sort in a fixed order; this shows the part after the bar.</summary>
public sealed class GroupNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string s = value?.ToString() ?? "";
        int bar = s.IndexOf('|');
        return bar >= 0 ? s[(bar + 1)..] : s;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
