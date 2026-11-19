namespace Panda.Relay.Conformance;

/// <summary>
/// plan-relay-conformance D4: one relay process per test class, default flags. Every test uses fresh Chrome keys, so
/// relayIds never collide; a test that needs flags or a fresh per-IP budget starts its own <see cref="RelayProcess"/>.
/// </summary>
public sealed class ClassRelay : IAsyncLifetime
{
    private RelayProcess? _relay;

    internal RelayProcess Relay => _relay ?? throw new InvalidOperationException("the class relay is not started");

    public async ValueTask InitializeAsync() =>
        _relay = await RelayProcess.StartAsync(log: line => TestContext.Current.SendDiagnosticMessage(line));

    public async ValueTask DisposeAsync()
    {
        if (_relay is not null)
        {
            await _relay.DisposeAsync();
        }
    }
}
