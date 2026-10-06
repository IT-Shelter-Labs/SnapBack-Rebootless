using System.Diagnostics;
using System.Text;
using SnapBack.Core.Environments;

namespace SnapBack.Windows.Environments;

/// <summary>
/// Runs a command-line tool directly (no shell, no window) with a fixed argument list and captures
/// its output. A command that does not finish in time is reported as timed out and left alone:
/// SnapBack does not kill the tools it starts.
/// </summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    private const int MaxOutputChars = 64 * 1024;

    public async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!File.Exists(executable))
        {
            return new CommandResult(-1, "", NotFound: true);
        }

        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.SystemDirectory,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            psi.Environment[key] = value;
        }

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                if (output.Length < MaxOutputChars)
                {
                    output.AppendLine(line);
                }
            }
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        if (!process.Start())
        {
            return new CommandResult(-1, "", NotFound: true);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            lock (output)
            {
                return new CommandResult(-1, output.ToString(), TimedOut: true);
            }
        }

        lock (output)
        {
            return new CommandResult(process.ExitCode, output.ToString());
        }
    }
}
