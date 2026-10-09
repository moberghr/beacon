using Microsoft.Extensions.Logging;

namespace Beacon.Tests.Common;

internal sealed record RecordedLog(string Category, LogLevel Level, string Message, Exception? Exception)
{
    /// <summary>The rendered message plus the exception text, for "never written anywhere" assertions.</summary>
    public string FullText => Exception == null ? Message : $"{Message} {Exception}";
}

/// <summary>
/// Records every log entry, of every category and level, for assertions. Register it as a logger provider, or hand
/// out typed loggers with <see cref="For{T}"/>.
/// </summary>
internal sealed class LogRecorder : ILoggerProvider
{
    private readonly List<RecordedLog> _entries = [];

    public IReadOnlyList<RecordedLog> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    /// <summary>Raised after every recorded entry, so a test can await a log line instead of polling for it.</summary>
    public event Action<RecordedLog>? Logged;

    public bool Contains(string text) => Entries.Any(x => x.FullText.Contains(text, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public ILogger<T> For<T>() => new TypedLogger<T>(new Logger(this, typeof(T).FullName ?? typeof(T).Name));

    public void Dispose()
    {
    }

    private void Add(RecordedLog entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }

        Logged?.Invoke(entry);
    }

    private sealed class Logger(LogRecorder recorder, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            recorder.Add(new RecordedLog(category, logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
