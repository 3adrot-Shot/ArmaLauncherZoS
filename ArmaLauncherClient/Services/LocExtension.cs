using System.Windows.Data;
using System.Windows.Markup;

namespace ArmaLauncherClient.Services;

/// <summary>
/// XAML markup extension for localized strings.
/// Usage: Text="{loc:Loc nav_game}"
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public class LocExtension : MarkupExtension
{
    public string Key { get; set; }

    public LocExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationManager.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
