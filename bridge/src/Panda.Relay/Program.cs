using System.Globalization;
using System.Net;

namespace Panda.Relay;

/// <summary>
/// <c>panda-relay --public-host &lt;host[:port]&gt; [--listen &lt;ip&gt;] [--port &lt;n&gt;] [--bytes-per-day &lt;n&gt;]
/// [--streams-per-day &lt;n&gt;] [--trusted-proxy &lt;ip|cidr&gt;]... [--client-ip-header &lt;name&gt;] [--max-hosting &lt;n&gt;] [--internal-port &lt;n&gt;] [--noise-per-ip-per-hour &lt;n&gt;] [--hosted-admin-url &lt;http://127.0.0.1:port&gt; --hosted-admin-token-file &lt;path&gt;
/// --hosted-mcp-url &lt;https://host/mcp&gt;]</c> (the last three: URL-only mode, all or none). The relay listens on plain HTTP (127.0.0.1:8080 by default) behind a
/// TLS-terminating proxy; deployment and the real quota numbers are plan 7c.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!TryParse(args, out var options, out var error))
        {
            await Console.Error.WriteLineAsync($"panda-relay: {error}\nusage: panda-relay --public-host <host[:port]> [--listen <ip>] [--port <n>] [--bytes-per-day <n>] [--streams-per-day <n>] [--trusted-proxy <ip|cidr>]... [--client-ip-header <name>] [--max-hosting <n>] [--internal-port <n>] [--noise-per-ip-per-hour <n>] [--hosted-admin-url <http://127.0.0.1:port> --hosted-admin-token-file <path> --hosted-mcp-url <https://host/mcp>]");
            return 2;
        }

        try
        {
            await using var server = await RelayServer.StartAsync(options!, TimeProvider.System, logs: null, CancellationToken.None);
            await Console.Out.WriteLineAsync($"panda-relay listening on {options!.ListenAddress}:{server.Port} for {options.PublicHost}"
                + (server.InternalPort is { } internalPort ? $"; internal 127.0.0.1:{internalPort}" : ""));
            await server.WaitForShutdownAsync(CancellationToken.None); // Ctrl+C or SIGTERM, through the host's console lifetime
        }
        catch (IOException e)
        {
            await Console.Error.WriteLineAsync($"panda-relay: cannot listen: {e.Message}");
            return 1;
        }

        return 0;
    }

    internal static bool TryParse(string[] args, out RelayOptions? options, out string? error)
    {
        string? host = null;
        var listen = IPAddress.Loopback;
        var port = 8080;
        long? bytesPerDay = null;
        int? streamsPerDay = null;
        Uri? hostedAdminUrl = null;
        string? hostedTokenFile = null;
        string? hostedMcpUrl = null;
        List<IPNetwork> trustedProxies = [];
        string? clientIpHeader = null;
        int? maxHosting = null;
        int? internalPort = null;
        int? noisePerHour = null;
        options = null;
        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--public-host" when value is not null:
                    host = value;
                    break;
                case "--listen" when value is not null && IPAddress.TryParse(value, out var ip):
                    listen = ip;
                    break;
                case "--port" when value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 0 and <= 65535: // 0 = any free port, printed in "listening on" (tests)
                    port = p;
                    break;
                case "--bytes-per-day" when value is not null && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var b) && b > 0:
                    bytesPerDay = b;
                    break;
                case "--streams-per-day" when value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s > 0:
                    streamsPerDay = s;
                    break;
                case "--hosted-admin-url" when value is not null && Uri.TryCreate(value, UriKind.Absolute, out var adminUrl):
                    hostedAdminUrl = adminUrl;
                    break;
                case "--hosted-admin-token-file" when value is not null:
                    hostedTokenFile = value;
                    break;
                case "--hosted-mcp-url" when value is not null:
                    hostedMcpUrl = value;
                    break;
                case "--trusted-proxy" when value is not null && TryParseNetwork(value, out var network):
                    trustedProxies.Add(network);
                    break;
                case "--internal-port" when value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ip2) && ip2 is > 0 and <= 65535:
                    internalPort = ip2;
                    break;
                case "--noise-per-ip-per-hour" when value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n):
                    noisePerHour = n;
                    break;
                case "--max-hosting" when value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m > 0:
                    maxHosting = m;
                    break;
                case "--client-ip-header" when value is not null:
                    clientIpHeader = value;
                    break;
                default:
                    error = $"bad argument {args[i]}";
                    return false;
            }

            i++;
        }

        if (host is null)
        {
            error = "--public-host is required";
            return false;
        }

        if (clientIpHeader is not null && trustedProxies.Count == 0)
        {
            error = "--client-ip-header needs --trusted-proxy";
            return false;
        }

        string? hostedToken = null;
        if (hostedTokenFile is not null)
        {
            try
            {
                hostedToken = File.ReadAllText(hostedTokenFile).Trim(); // a secret: no error below ever prints it
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = "cannot read --hosted-admin-token-file";
                return false;
            }
        }

        options = new RelayOptions
        {
            PublicHost = host,
            ListenAddress = listen,
            Port = port,
            HostedAdminUrl = hostedAdminUrl,
            HostedAdminToken = hostedToken,
            HostedMcpUrl = hostedMcpUrl,
        };
        foreach (var network in trustedProxies) options.TrustedProxies.Add(network);
        if (clientIpHeader is not null) options.ClientIpHeader = clientIpHeader;
        if (maxHosting is { } cap) options.MaxHosting = cap;
        options.InternalPort = internalPort;
        if (noisePerHour is { } noise) options.NoisePerIpPerHour = noise;
        if (bytesPerDay is { } bytes) options.BytesPerDay = bytes;
        if (streamsPerDay is { } streams) options.StreamsPerDay = streams;
        try
        {
            options.Validate();
        }
        catch (ArgumentException e)
        {
            options = null;
            error = e.Message;
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// An address (a host network: /32 or /128) or a CIDR network whose host bits are zero. Refused: IPv4 not written
    /// canonically ("010.0.0.0" is octal 8.0.0.0, "127.1" is 127.0.0.1), IPv4-mapped IPv6 (never matches an IPv4
    /// peer) and prefix 0 (every peer would pick its own bucket) — C2 critic #2–#4.
    /// </summary>
    private static bool TryParseNetwork(string value, out IPNetwork network)
    {
        network = default;
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        var text = slash < 0 ? value : value[..slash];
        if (!IPAddress.TryParse(text, out var address) || address.IsIPv4MappedToIPv6
            || (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && address.ToString() != text))
        {
            return false;
        }

        if (slash < 0)
        {
            network = new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }

        // .NET 10 accepts (and masks) "10.0.0.1/8"; a typo there must not silently trust a wider network
        return IPNetwork.TryParse(value, out network) && network.PrefixLength > 0 && network.BaseAddress.Equals(address);
    }
}
