using System.Globalization;
using System.Resources;

namespace SnapBack.Core.Localization;

/// <summary>
/// Access to SnapBack's localized strings (<c>Strings.resx</c> and its <c>ru</c> / <c>zh-CN</c>
/// translations). The culture is explicit rather than thread-bound so that background work
/// produces text in the language the user chose. Missing translations fall back to English.
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Resources = new("SnapBack.Core.Localization.Strings", typeof(Loc).Assembly);
    private static CultureInfo _culture = CultureInfo.GetCultureInfo(Languages.English);

    /// <summary>Raised after <see cref="Culture"/> changed.</summary>
    public static event EventHandler? CultureChanged;

    public static CultureInfo Culture
    {
        get => Volatile.Read(ref _culture);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (Equals(Interlocked.Exchange(ref _culture, value), value))
            {
                return;
            }

            CultureChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>The localized string for <paramref name="key"/> (English, then the key itself, as fallback).</summary>
    public static string Get(string key) => Lookup(Resources, key, Culture);

    /// <summary>Formats the localized string with culture-aware formatting of the arguments.</summary>
    public static string Format(string key, params object?[] args) => string.Format(Culture, Get(key), args);

    /// <summary>Lookup helper shared with the UI's own resource set.</summary>
    public static string Lookup(ResourceManager resources, string key, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(resources);
        try
        {
            return resources.GetString(key, culture)
                ?? resources.GetString(key, CultureInfo.InvariantCulture)
                ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
    }

    /// <summary>Exposed for tests: the resource manager behind <see cref="Get"/>.</summary>
    internal static ResourceManager ResourceManager => Resources;
}
