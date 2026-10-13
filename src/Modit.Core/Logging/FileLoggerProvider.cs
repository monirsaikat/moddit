using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Modit.Core.Logging;

/// <summary>
/// Daily rolling text log in the app's logs folder, kept for seven days. Writes go through a
/// background queue so logging never blocks the UI thread. File names and contents of user
/// documents are never logged at Information level or above (docs/plan/02, "Logging").
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetentionDays = 7;

    private readonly string _directory;
    private readonly string _prefix;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 4096);
    private readonly Thread _writer;

    public FileLoggerProvider(string directory, string prefix = "modit")
    {
        _directory = directory;
        _prefix = prefix;
        Directory.CreateDirectory(directory);
        DeleteOldFiles();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Modit log writer" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted)
        {
            // Dropping under extreme pressure is better than blocking the caller.
            _queue.TryAdd(line);
        }
    }

    private void WriteLoop()
    {
        foreach (string line in _queue.GetConsumingEnumerable())
        {
            try
            {
                string path = Path.Combine(
                    _directory, $"{_prefix}-{DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never fail the application.
            }
        }
    }

    private void DeleteOldFiles()
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (string file in Directory.EnumerateFiles(_directory, $"{_prefix}-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retention is best effort.
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTime.UtcNow:O} [{logLevel}] {category} ({eventId.Id}): {formatter(state, exception)}");
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            provider.Enqueue(line);
        }
    }
}
