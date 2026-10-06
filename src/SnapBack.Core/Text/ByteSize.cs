using System.Globalization;
using SnapBack.Core.Localization;

namespace SnapBack.Core.Text;

public static class ByteSize
{
    /// <summary>Formats a byte count the way Windows does ("650 MB", "1.4 GB"), in the current UI language.</summary>
    public static string Format(long bytes) => Format(bytes, Loc.Culture);

    public static string Format(long bytes, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (bytes < 0)
        {
            return "-" + Format(-bytes, culture);
        }

        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        return bytes switch
        {
            >= (long)gb => (bytes / gb).ToString(bytes >= 100 * gb ? "0" : "0.0", culture) + " " + Loc.Lookup(Loc.ResourceManager, "Unit_GB", culture),
            >= (long)mb => (bytes / mb).ToString("0", culture) + " " + Loc.Lookup(Loc.ResourceManager, "Unit_MB", culture),
            >= (long)kb => (bytes / kb).ToString("0", culture) + " " + Loc.Lookup(Loc.ResourceManager, "Unit_KB", culture),
            _ => bytes.ToString(culture) + " " + Loc.Lookup(Loc.ResourceManager, "Unit_B", culture),
        };
    }

    /// <summary>A signed difference ("+1.2 GB", "−350 MB").</summary>
    public static string FormatDelta(long bytes) =>
        bytes switch
        {
            > 0 => "+" + Format(bytes),
            < 0 => "−" + Format(-bytes),
            _ => Format(0),
        };
}
