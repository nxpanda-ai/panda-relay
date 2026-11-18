using Microsoft.Extensions.Logging;

namespace Panda.Relay;

/// <summary>
/// plan-relay-deploy C2 (seam S9): a trusted proxy that sends no usable client IP leaves every client in the proxy's
/// own bucket (one attacker could then 4530 every bridge). The relay still serves the request, and says so at most
/// once a minute with the number of such requests since the previous line. Never logs a header value.
/// </summary>
internal sealed partial class ProxyWarning(TimeProvider time, ILogger<ProxyWarning> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private long _held;
    private DateTimeOffset? _last;

    /// <summary>Records one request that kept the proxy's address; logs when a minute has passed since the last line.</summary>
    public void Note()
    {
        long count;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            _held++;
            if (_last is { } last && now - last < Interval)
            {
                return;
            }

            count = _held;
            _held = 0;
            _last = now;
        }

        LogNoClientIp(count);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "trusted proxy sent no client IP; requests without one since the last warning: {Count}")]
    private partial void LogNoClientIp(long count);
}
