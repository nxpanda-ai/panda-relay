namespace Panda.Relay.Tests;

/// <summary>plan-goal-1-5 A3: the relay tests pick ports the same way as the bridge tests (tests/Shared/TestPorts.cs).</summary>
public sealed class PortsTests
{
    [Fact]
    public void Picked_ports_are_below_the_dynamic_range()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.InRange(Ports.Free(), 1024, 32767);
        }
    }
}
