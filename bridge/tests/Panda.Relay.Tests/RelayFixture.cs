using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Panda.Relay.Tests;

/// <summary>A real relay on 127.0.0.1:0 with a fake clock and captured logs (the client helpers: tests/Shared/RelayClient.cs).</summary>
internal sealed class RelayFixture : IRelayEndpoint, IAsyncDisposable
{
    public const string PublicHost = "relay.test:443";

    private RelayFixture(RelayServer server, FakeTimeProvider time, LogSink logs)
    {
        Server = server;
        Time = time;
        Logs = logs;
    }

    public RelayServer Server { get; }
    public FakeTimeProvider Time { get; }
    public LogSink Logs { get; }
    public Uri Ws => new($"ws://127.0.0.1:{Server.Port}");
    public int Port => Server.Port;

    string IRelayEndpoint.PublicHost => Server.Options.PublicHost; // what the relay checks a register against

    public static async Task<RelayFixture> StartAsync(Action<RelayOptions>? configure = null, DateTimeOffset? now = null)
    {
        var options = new RelayOptions { PublicHost = PublicHost };
        configure?.Invoke(options);
        var time = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        var logs = new LogSink();
        var server = await RelayServer.StartAsync(options, time, logs, TestContext.Current.CancellationToken);
        return new RelayFixture(server, time, logs);
    }

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}

/// <summary>Captures every log line the relay writes (no-secret checks).</summary>
internal sealed class LogSink : ILoggerProvider
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(LogSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (sink._lines)
            {
                sink._lines.Add($"{logLevel} {formatter(state, exception)} {exception}");
            }
        }
    }
}
