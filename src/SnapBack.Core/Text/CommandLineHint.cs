using System.Text;
using SnapBack.Core.Classification;

namespace SnapBack.Core.Text;

/// <summary>
/// Derives a short hint ("train.py", "module http.server") from an interpreter command line so that
/// the user can tell several python.exe / node.exe processes apart.
/// </summary>
/// <remarks>Only a file name or module name is extracted; arguments (which may contain secrets) are never returned.</remarks>
public static class CommandLineHint
{
    private static readonly string[] ScriptExtensions =
    [
        ".py", ".pyw", ".js", ".mjs", ".cjs", ".ts", ".mts", ".jar", ".ps1", ".bat", ".cmd", ".rb", ".pl", ".php",
        ".lua", ".r", ".jl", ".sh", ".vbs",
    ];

    /// <summary>Whether a hint is worth computing for this executable.</summary>
    public static bool IsInteresting(string imageName) =>
        KnownProcesses.ScriptsAndRuntimes.Contains(imageName) || KnownProcesses.Shells.Contains(imageName);

    public static string? FromCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var args = Split(commandLine);
        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "-m" && i + 1 < args.Count)
            {
                return "module " + args[i + 1];
            }

            if (a == "-c")
            {
                return "inline code";
            }

            if (a.StartsWith('-') || a.StartsWith('/'))
            {
                if (a is "-jar" or "-File" or "-file" && i + 1 < args.Count)
                {
                    return PathUtil.GetFileName(args[i + 1]);
                }

                continue;
            }

            if (ScriptExtensions.Any(e => a.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            {
                return PathUtil.GetFileName(a);
            }
        }

        return null;
    }

    /// <summary>Splits a command line following the Windows (CommandLineToArgvW) quoting rules.</summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var slashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    slashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else
                {
                    current.Append('\\', slashes);
                    i--;
                }

                hasToken = true;
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
