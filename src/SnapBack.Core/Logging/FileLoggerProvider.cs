using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SnapBack.Core.Logging;

/// <summary>
/// Minimal, dependency-free file logger: one file per day, old files removed after
/// <see cref="RetentionDays"/> days. Logging failures are swallowed - they must never take the app down.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int RetentionDays = 14;

    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);

    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information)
    {
        _directory = directory;
        _minLevel = minLevel;
        try
        {
            Directory.CreateDirectory(directory);
            CleanupOldFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is best effort.
        }
    }

    public string CurrentFile => Path.Combine(_directory, $"snapback-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, c => new FileLogger(this, c));

    public void Dispose() => _loggers.Clear();

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var sb = new StringBuilder(256)
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(Abbreviate(level)).Append("] ")
            .Append(category).Append(": ")
            .Append(message);
        if (exception is not null)
        {
            sb.AppendLine().Append("    ").Append(exception.GetType().FullName).Append(": ").Append(exception.Message);
            if (exception.StackTrace is { } st)
            {
                sb.AppendLine().Append(st);
            }
        }

        sb.AppendLine();
        lock (_gate)
        {
            try
            {
                File.AppendAllText(CurrentFile, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging is best effort.
            }
        }
    }

    private void CleanupOldFiles()
    {
        var cutoff = DateTime.Now.AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "snapback-*.log"))
        {
            if (File.GetLastWriteTime(file) < cutoff)
            {
                File.Delete(file);
            }
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider._minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
