using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AccessLab.Api.Tests;

public class LoggingTests
{
    [Fact]
    public async Task Framework_request_logging_does_not_record_token_shaped_query_values()
    {
        var sink = new CaptureLogs();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureLogging(l => l.AddProvider(sink))
        );
        await factory
            .CreateClient()
            .GetAsync("/api/session?code=SECRET_QUERY_SENTINEL&state=SECRET_STATE_SENTINEL");
        Assert.DoesNotContain(
            sink.Messages,
            m => m.Contains("SECRET_QUERY_SENTINEL") || m.Contains("SECRET_STATE_SENTINEL")
        );
    }
}

public class CaptureLogs : ILoggerProvider
{
    public ConcurrentBag<string> Messages { get; } = [];

    public ILogger CreateLogger(string category) => new CaptureLogger(Messages);

    public void Dispose() { }

    class CaptureLogger(ConcurrentBag<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel level,
            EventId id,
            TState state,
            Exception? ex,
            Func<TState, Exception?, string> formatter
        ) => messages.Add(formatter(state, ex));
    }
}
