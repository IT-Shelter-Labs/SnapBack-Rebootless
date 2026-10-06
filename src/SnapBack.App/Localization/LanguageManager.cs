using System.Globalization;
using SnapBack.Core.Localization;

namespace SnapBack.App.Localization;

/// <summary>Applies the language setting to the whole app at runtime.</summary>
public static class LanguageManager
{
    /// <summary>The Windows display language, captured before SnapBack changes any culture.</summary>
    public static CultureInfo SystemUiCulture { get; } = CultureInfo.CurrentUICulture;

    public static void Apply(string? setting)
    {
        var culture = Languages.Resolve(setting, SystemUiCulture);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Loc.Culture = culture;
    }
}
