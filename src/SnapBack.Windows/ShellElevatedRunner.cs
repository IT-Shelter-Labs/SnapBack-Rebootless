using System.ComponentModel;
using System.Diagnostics;
using SnapBack.Core.Memory;

namespace SnapBack.Windows;

/// <summary>
/// Starts SnapBack's elevated helper with the "runas" verb (one UAC prompt for this single
/// operation). The helper's only output is its exit code.
/// </summary>
public sealed class ShellElevatedRunner : IElevatedRunner
{
    private const int ERROR_CANCELLED = 1223;
    private readonly string _executable;

    public ShellElevatedRunner(string executable)
    {
        _executable = executable;
    }

    public async Task<int?> RunElevatedAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(a => a.Any(c => char.IsWhiteSpace(c) || c == '"')))
        {
            throw new ArgumentException("Helper arguments must be simple tokens.", nameof(arguments));
        }

        var start = new ProcessStartInfo(_executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(' ', arguments),
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return null; // the user said "No" in the UAC prompt
        }

        if (process is null)
        {
            throw new InvalidOperationException("The elevated helper did not start.");
        }

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
    }
}
