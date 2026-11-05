using Microsoft.Extensions.Logging;

namespace DeadSwitch.Tests;

// Remembers everything the application logs, from every class and on every level. The exception's full
// text is included, so that a test can show that a secret is not hiding in there either.
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly object _sync = new object();
    private readonly List<CapturedLogEntry> _entries = new List<CapturedLogEntry>();

    public List<CapturedLogEntry> GetEntries()
    {
        lock (_sync)
        {
            // A copy, because the application can log on another thread while the test reads the list.
            return new List<CapturedLogEntry>(_entries);
        }
    }

    public bool Contains(LogLevel level, string text)
    {
        foreach (CapturedLogEntry entry in GetEntries())
        {
            if (entry.Level == level && entry.Text.Contains(text))
            {
                return true;
            }
        }

        return false;
    }

    public void AssertNothingContains(string text)
    {
        foreach (CapturedLogEntry entry in GetEntries())
        {
            Assert.DoesNotContain(text, entry.Text);
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new CapturingLogger(this);
    }

    public void Dispose()
    {
    }

    private void Add(LogLevel level, string text)
    {
        lock (_sync)
        {
            _entries.Add(new CapturedLogEntry(level, text));
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly CapturedLogs _owner;

        public CapturingLogger(CapturedLogs owner)
        {
            _owner = owner;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string text = formatter(state, exception);

            if (exception != null)
            {
                text += " " + exception;
            }

            _owner.Add(logLevel, text);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }
    }
}

internal sealed record CapturedLogEntry(LogLevel Level, string Text);
