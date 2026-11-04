using DeadSwitch.Api.Monitoring;
using Microsoft.Extensions.Logging;

namespace DeadSwitch.Tests;

// Lets a test see what the worker logs, and wait until a pass is done.
internal sealed class TestLogger : ILogger<MonitoringWorker>
{
    private readonly object _sync = new object();
    private readonly List<string> _errors = new List<string>();

    // Släpps när ett pass är klart, så att testet vet när det är säkert att flytta klockan.
    private readonly SemaphoreSlim _passFinished = new SemaphoreSlim(0);

    public List<string> GetErrors()
    {
        lock (_sync)
        {
            // A copy, because the worker can log on another thread while the test reads the list.
            return new List<string>(_errors);
        }
    }

    public async Task WaitForPassToFinishAsync()
    {
        // The limit is only there so that a bug gives a red test instead of a test that hangs.
        bool finished = await _passFinished.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(finished, "The worker did not finish a pass in time.");
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        string message = formatter(state, exception);

        if (logLevel == LogLevel.Error)
        {
            lock (_sync)
            {
                _errors.Add(message);
            }
        }

        if (message == MonitoringWorker.PassFinishedMessage)
        {
            _passFinished.Release();
        }
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
