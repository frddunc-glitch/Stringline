using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Stringline.Optimiser.Tests;

/// <summary>Writes log messages to a test's output.</summary>
internal sealed class OutputLogger<T>(ITestOutputHelper output) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        output.WriteLine($"[{logLevel}] {formatter(state, exception)}");
    }
}
