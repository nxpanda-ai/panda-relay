using System.Net.WebSockets;
using Panda.Bridge.Protocol;

namespace Panda.Relay.Tests;

[CollectionDefinition(nameof(ProcessEnvironment), DisableParallelization = true)]
public sealed class ProcessEnvironment;

/// <summary>
/// plan-relay-deploy C2 (C2 critic #1, #5): the process environment must not widen who picks a bucket or what is
/// logged. These tests set process-wide variables, so they run alone.
/// </summary>
[Collection(nameof(ProcessEnvironment))]
public class ClientIpEnvironmentTests
{
    [Fact]
    public async Task ASPNETCORE_FORWARDEDHEADERS_ENABLED_does_not_let_an_untrusted_peer_pick_its_bucket()
    {
        // the host's own startup filter would add a middleware that trusts EVERY peer (MS Learn, proxy-load-balancer)
        using var _ = new EnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true");
        await using var relay = await RelayFixture.StartAsync(o => o.RegistrationsPerIpPerHour = 1);
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.30"));
        Assert.False(await AdmittedAsync(relay, "X-Forwarded-For: 203.0.113.31"));
    }

    [Theory]
    [InlineData("Logging__LogLevel__Microsoft.AspNetCore.HttpOverrides")]
    [InlineData("Logging__LogLevel__Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware")] // more specific
    [InlineData("Logging__LogLevel__Microsoft")]
    [InlineData("Logging__LogLevel__Default")]
    [InlineData("Logging__Panda.Relay.Tests.LogSink__LogLevel__Microsoft")] // a provider-specific rule ranks first
    public async Task A_configured_debug_level_for_the_forwarded_headers_middleware_never_logs_a_header_value(string variable)
    {
        // the middleware logs "Unparsable IP: <value>" at Debug; an operator's Logging__LogLevel__… must not open it
        using var _ = new EnvironmentVariable(variable, "Debug");
        await using var relay = await RelayFixture.StartAsync(o => o.TrustedProxies.Add(System.Net.IPNetwork.Parse("127.0.0.1/32")));
        Assert.True(await AdmittedAsync(relay, "X-Forwarded-For: secret-looking-value"));
        Assert.DoesNotContain(relay.Logs.Lines, l => l.Contains("secret-looking-value", StringComparison.Ordinal));
    }

    private static async Task<bool> AdmittedAsync(RelayFixture relay, string headerLine)
    {
        using var ws = await relay.RawConnectAsync("/v1/browser", headerLine);
        var (type, _) = await Wire.ReceiveRawAsync(ws);
        if (type == WebSocketMessageType.Text)
        {
            return true;
        }

        Assert.Equal(RelayCloseCodes.DialerLimited, (int)ws.CloseStatus!.Value);
        return false;
    }

    private sealed class EnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _old;

        public EnvironmentVariable(string name, string value)
        {
            _name = name;
            _old = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _old);
    }
}
