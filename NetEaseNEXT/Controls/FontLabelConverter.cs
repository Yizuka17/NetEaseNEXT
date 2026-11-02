using Microsoft.UI.Xaml.Data;

namespace NetEaseNEXT.Controls;

public sealed class FontLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var source = value?.ToString() ?? ""; var separator = source.LastIndexOf('#');
        return separator >= 0 ? Uri.UnescapeDataString(source[(separator + 1)..]) : source;
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
