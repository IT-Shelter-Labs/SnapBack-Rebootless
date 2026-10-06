using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using SnapBack.Core.Localization;

namespace SnapBack.App.Localization;

/// <summary>
/// Bindable source of the UI's localized strings. XAML binds to its indexer (through
/// <see cref="TrExtension"/>); when the language changes, every binding refreshes, so switching
/// language does not need a restart. Keys missing from the UI resources fall back to the core
/// resources, and missing translations fall back to English.
/// </summary>
public sealed class Tr : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new("SnapBack.App.Localization.Strings", typeof(Tr).Assembly);

    private Tr()
    {
        Loc.CultureChanged += (_, _) =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiFont)));
        };
    }

    public static Tr Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Get(key);

    /// <summary>A font with good coverage for the current language (CJK needs Microsoft YaHei UI).</summary>
    public FontFamily UiFont => Loc.Culture.Name.StartsWith("zh", StringComparison.Ordinal)
        ? new FontFamily("Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI")
        : new FontFamily("Segoe UI Variable Text, Segoe UI");

    public static string Get(string key)
    {
        var value = Loc.Lookup(Resources, key, Loc.Culture);
        return ReferenceEquals(value, key) || value == key ? Loc.Get(key) : value;
    }

    public static string Format(string key, params object?[] args) => string.Format(Loc.Culture, Get(key), args);

    public static CultureInfo Culture => Loc.Culture;
}

/// <summary><c>Text="{l:Tr Main_Refresh}"</c> — a live-updating localized string.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Tr.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
