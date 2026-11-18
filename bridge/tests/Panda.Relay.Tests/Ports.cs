namespace Panda.Relay.Tests;

internal static class Ports
{
    /// <summary>A loopback port free when picked, below the OS's dynamic range (<see cref="Panda.TestSupport.TestPorts"/>).</summary>
    public static int Free() => Panda.TestSupport.TestPorts.Next();
}
