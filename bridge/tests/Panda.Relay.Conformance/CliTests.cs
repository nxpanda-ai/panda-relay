using System.Globalization;
using System.Security.Cryptography;

namespace Panda.Relay.Conformance;

/// <summary>
/// G5: a command line the relay cannot run with ends the process with a non-zero exit code. Only "≠ 0" is the contract
/// (the C# relay's 2 is not required of another implementation). Three classes, so their rows run in parallel (each
/// row is a process start). Placeholders: <c>{port}</c> = the <c>--port</c> given, <c>{file}</c> = a file holding a
/// well-formed hosted admin token.
/// </summary>
internal static class Cli
{
    private static readonly TimeSpan ExitLimit = TimeSpan.FromSeconds(60); // a cold start took up to 13 s here

    public const string Host = "relay.test:443";

    /// <summary>A well-formed hosted admin token (32 bytes, base64url) — the content of <c>{file}</c>.</summary>
    public static readonly string Token = new('A', 43);

    public static async Task<(int ExitCode, IReadOnlyList<string> Output)> RunAsync(string[] args)
    {
        var port = TestPorts.Next().ToString(CultureInfo.InvariantCulture);
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, Token, TestContext.Current.CancellationToken);
            var argv = args.Select(a => a.Replace("{port}", port, StringComparison.Ordinal).Replace("{file}", file, StringComparison.Ordinal));
            return await RelayProcess.RunToExitAsync([.. argv, "--listen", "127.0.0.1", "--port", port], ExitLimit);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static async Task AssertRefusedAsync(string why, string[] args)
    {
        var (exit, output) = await RunAsync(args);
        Assert.False(output.Any(line => line.Contains(Token, StringComparison.Ordinal)), $"{why}: the refusal printed the hosted token");
        Assert.True(exit != 0, $"{why}: exit 0; output: {string.Join(" | ", output)}");
    }
}

public class CliTests
{
    public static TheoryData<string, string[]> Refused => new()
    {
        { "no --public-host", [] },
        { "an upper-case public host", ["--public-host", "Relay.Test:443"] },
        { "an unknown option", ["--public-host", Cli.Host, "--no-such-option", "1"] },
        { "a negative quota", ["--public-host", Cli.Host, "--bytes-per-day", "-1"] },
        { "a zero daily stream quota", ["--public-host", Cli.Host, "--streams-per-day", "0"] },
        { "a zero daily byte quota", ["--public-host", Cli.Host, "--bytes-per-day", "0"] },
        { "an internal port equal to the public port", ["--public-host", Cli.Host, "--internal-port", "{port}"] },
        { "a hosting cap below one", ["--public-host", Cli.Host, "--max-hosting", "0"] },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public Task A_command_line_the_relay_cannot_run_with_exits_non_zero(string why, string[] args) => Cli.AssertRefusedAsync(why, args);
}

/// <summary>G5: who may pick a client's per-IP bucket is fixed at start — a trusted proxy that could be anyone is refused.</summary>
public class CliProxyTests
{
    public static TheoryData<string, string[]> Refused => new()
    {
        { "a trusted proxy written non-canonically (octal)", ["--public-host", Cli.Host, "--trusted-proxy", "010.0.0.1"] },
        { "a trusted proxy written short (127.1)", ["--public-host", Cli.Host, "--trusted-proxy", "127.1"] },
        { "a trusted proxy with host bits set", ["--public-host", Cli.Host, "--trusted-proxy", "10.0.0.1/8"] },
        { "a trusted proxy /0", ["--public-host", Cli.Host, "--trusted-proxy", "0.0.0.0/0"] },
        { "a trusted proxy ::/0", ["--public-host", Cli.Host, "--trusted-proxy", "::/0"] },
        { "an IPv4-mapped trusted proxy (never matches an IPv4 peer)", ["--public-host", Cli.Host, "--trusted-proxy", "::ffff:203.0.113.7"] },
        { "a client IP header with no proxy to trust", ["--public-host", Cli.Host, "--client-ip-header", "CF-Connecting-IP"] },
        { "a client IP header that is no header name", ["--public-host", Cli.Host, "--trusted-proxy", "127.0.0.1", "--client-ip-header", "X Bad"] },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public Task A_trusted_proxy_setting_that_could_trust_anyone_exits_non_zero(string why, string[] args) => Cli.AssertRefusedAsync(why, args);
}

/// <summary>G5: the URL-only hosting options are all or none, the admin URL is http on 127.0.0.1, the MCP URL is on the public host.</summary>
public class CliHostedTests
{
    private const string Mcp = "https://relay.test/mcp";

    public static TheoryData<string, string[]> Refused => new()
    {
        { "one hosted option without the other two", ["--public-host", Cli.Host, "--hosted-mcp-url", Mcp] },
        { "no token file", ["--public-host", Cli.Host, "--hosted-admin-url", "http://127.0.0.1:47822", "--hosted-mcp-url", Mcp] },
        { "an admin URL off loopback", ["--public-host", Cli.Host, "--hosted-admin-url", "http://admin.example.com:47822", "--hosted-admin-token-file", "{file}", "--hosted-mcp-url", Mcp] },
        { "an https admin URL", ["--public-host", Cli.Host, "--hosted-admin-url", "https://127.0.0.1:47822", "--hosted-admin-token-file", "{file}", "--hosted-mcp-url", Mcp] },
        { "an admin URL with a path", ["--public-host", Cli.Host, "--hosted-admin-url", "http://127.0.0.1:47822/admin", "--hosted-admin-token-file", "{file}", "--hosted-mcp-url", Mcp] },
        { "an http MCP URL off loopback", ["--public-host", Cli.Host, "--hosted-admin-url", "http://127.0.0.1:47822", "--hosted-admin-token-file", "{file}", "--hosted-mcp-url", "http://relay.test/mcp"] },
        { "an MCP URL on another host", ["--public-host", Cli.Host, "--hosted-admin-url", "http://127.0.0.1:47822", "--hosted-admin-token-file", "{file}", "--hosted-mcp-url", "https://other.test/mcp"] },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public Task A_hosted_option_set_the_relay_cannot_run_with_exits_non_zero(string why, string[] args) => Cli.AssertRefusedAsync(why, args);

    /// <summary>Positive control: the refusals above are for the one wrong option, not for any hosted set (critic T2-T5 round 2).</summary>
    [Fact]
    public async Task A_well_formed_hosted_set_starts()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, Cli.Token, TestContext.Current.CancellationToken);
            var admin = $"http://127.0.0.1:{TestPorts.Next().ToString(CultureInfo.InvariantCulture)}"; // never dialled: no RELAY_HOST is sent
            await using var relay = await RelayProcess.StartAsync(
                ["--hosted-admin-url", admin, "--hosted-admin-token-file", file, "--hosted-mcp-url", $"https://{RelayProcess.PublicHostName}/mcp"]);
            relay.Secret(Cli.Token);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_hosted_token_file_that_holds_no_token_is_refused_and_never_printed()
    {
        var marker = "secret-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, marker, TestContext.Current.CancellationToken);
            var (exit, output) = await Cli.RunAsync(
                ["--public-host", Cli.Host, "--hosted-admin-url", "http://127.0.0.1:47822", "--hosted-admin-token-file", file, "--hosted-mcp-url", Mcp]);
            Assert.True(exit != 0, "a token file without a token: exit 0");
            Assert.False(output.Any(line => line.Contains(marker, StringComparison.Ordinal)), "the refusal echoed the token file"); // never the value
        }
        finally
        {
            File.Delete(file);
        }
    }
}
