using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AbsolverModTool.Gui;

/// <summary>True -> Collapsed, False -> Visible - the opposite of the built-in
/// BooleanToVisibilityConverter, used to show a plain TextBox editor except when a cell has
/// AttackChoices (in which case the ComboBox editor, bound with the normal converter, shows instead).</summary>
public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
