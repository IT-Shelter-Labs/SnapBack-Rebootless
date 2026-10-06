using System.Globalization;

namespace SnapBack.Core.Localization;

/// <summary>The UI languages SnapBack ships with, and how a setting maps to one of them.</summary>
public static class Languages
{
    /// <summary>Setting value meaning "follow the Windows display language".</summary>
    public const string System = "system";

    public const string English = "en";

    public const string Russian = "ru";

    public const string ChineseSimplified = "zh-CN";

    public static IReadOnlyList<string> Supported { get; } = [English, Russian, ChineseSimplified];

    /// <summary>Native display names (shown in the language picker regardless of the current language).</summary>
    public static string NativeName(string code) => code switch
    {
        English => "English",
        Russian => "Русский",
        ChineseSimplified => "简体中文",
        _ => code,
    };

    /// <summary>
    /// Resolves a language setting to a supported culture. Unknown settings and unsupported system
    /// languages fall back to English.
    /// </summary>
    public static CultureInfo Resolve(string? setting, CultureInfo systemUiCulture)
    {
        ArgumentNullException.ThrowIfNull(systemUiCulture);
        var code = string.IsNullOrWhiteSpace(setting) || string.Equals(setting, System, StringComparison.OrdinalIgnoreCase)
            ? FromSystem(systemUiCulture)
            : Supported.FirstOrDefault(s => string.Equals(s, setting, StringComparison.OrdinalIgnoreCase)) ?? English;
        return CultureInfo.GetCultureInfo(code);
    }

    /// <summary>Maps a Windows display language to a supported language.</summary>
    public static string FromSystem(CultureInfo culture)
    {
        for (var c = culture; !Equals(c, CultureInfo.InvariantCulture); c = c.Parent)
        {
            switch (c.Name)
            {
                case "ru":
                    return Russian;
                case "zh-CN" or "zh-SG" or "zh-Hans":
                    return ChineseSimplified;
                case "en":
                    return English;
            }
        }

        return English;
    }

    /// <summary>Normalizes a stored setting (unknown values become "system").</summary>
    public static string NormalizeSetting(string? setting) =>
        Supported.FirstOrDefault(s => string.Equals(s, setting, StringComparison.OrdinalIgnoreCase)) ?? System;
}
