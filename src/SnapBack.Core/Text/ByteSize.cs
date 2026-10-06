using System.Globalization;

namespace SnapBack.Core.Text;

public static class ByteSize
{
    /// <summary>Formats a byte count the way Windows does ("650 MB", "1.4 GB").</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + Format(-bytes);
        }

        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        var c = CultureInfo.InvariantCulture;
        return bytes switch
        {
            >= (long)gb => (bytes / gb).ToString(bytes >= 100 * gb ? "0" : "0.0", c) + " GB",
            >= (long)mb => (bytes / mb).ToString("0", c) + " MB",
            >= (long)kb => (bytes / kb).ToString("0", c) + " KB",
            _ => bytes.ToString(c) + " B",
        };
    }
}
