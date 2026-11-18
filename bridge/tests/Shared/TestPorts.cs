using System.Net;
using System.Net.Sockets;

namespace Panda.TestSupport;

/// <summary>
/// Loopback ports for test servers (plan-goal-1-5 A3), picked BELOW every OS's dynamic range — Windows 49152+, Linux
/// 32768+ — so no outgoing test socket can take a port between its pick and the server's bind. Port 0 picks from the
/// dynamic range, which the suite's thousands of client sockets also draw from: 6 "address already in use" failures in
/// 3 full runs on 2026-11-10, ports 51061..58462. One walk per process from a random start (the bridge and relay test
/// processes run at once); a port that cannot be bound now — in use, or excluded by the OS — is skipped. Two processes
/// can still race only if their walks meet on the same port within its pick-to-bind moment (accepted, critic A3).
/// Compiled into each test project (a linked file), not a shared assembly.
/// </summary>
internal static class TestPorts
{
    public const int First = 10_000;
    public const int End = 32_000; // exclusive

    private static int _next = Random.Shared.Next(First, End);

    public static int Next()
    {
        for (var tried = 0; tried < End - First; tried++)
        {
            var port = First + ((Interlocked.Increment(ref _next) - First) % (End - First));
            if (CanBind(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException($"no free loopback port in {First}..{End - 1}");
    }

    private static bool CanBind(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
