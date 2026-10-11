using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace UClear;
// XAML text in both languages: Content="{u:T 'Kaldır', 'Uninstall'}". Windows are rebuilt after a language change, so the value is read once.
public sealed class TExtension(string tr, string en) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(tr, en);
}
// Shows engine identifiers (kinds, results, categories…) in the current language: {Binding Kind, Converter={StaticResource Display}}.
public sealed class DisplayText : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => L.V(value?.ToString());
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
