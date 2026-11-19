using System.Text.Json;
using System.Xml.Linq;
using Panda.Bridge.Relay;
using Panda.Bridge.Tests;

namespace Panda.Relay.Conformance;

/// <summary>plan-relay-conformance D1 / D2: the suite is black-box, names what it tests, and its own client is pinned.</summary>
public class ConformanceGuardTests
{
    /// <summary>
    /// Skipped (never failed) whenever PANDA_RELAY_CMD is set, so every run summary shows which relay was tested
    /// (critic #23 / #37: a leftover variable is visible, not silent).
    /// </summary>
    [Fact]
    public void The_relay_under_test_is_the_one_of_this_repository() =>
        Assert.SkipWhen(RelayUnderTest.Cmd is not null, $"testing {RelayUnderTest.Describe()}");

    [Fact]
    public void The_suite_never_references_or_loads_the_relay_assembly()
    {
        var self = typeof(ConformanceGuardTests).Assembly;
        Assert.DoesNotContain(self.GetReferencedAssemblies(), a => IsRelay(a.Name));
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => IsRelay(a.GetName().Name));
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "panda-relay.dll")), "panda-relay.dll was copied next to the suite");

        var deps = Path.Combine(AppContext.BaseDirectory, self.GetName().Name + ".deps.json");
        Assert.DoesNotContain("panda-relay", File.ReadAllText(deps), StringComparison.OrdinalIgnoreCase);

        // the only Panda.Relay reference orders the build (the default relay is built first) and brings no assembly
        var csproj = XDocument.Load(Path.Combine(RepoPaths.Root, "bridge", "tests", "Panda.Relay.Conformance", "Panda.Relay.Conformance.csproj"));
        var relayRefs = csproj.Descendants("ProjectReference")
            .Where(r => ((string?)r.Attribute("Include") ?? "").Replace('/', '\\').EndsWith(@"Panda.Relay\Panda.Relay.csproj", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var reference = Assert.Single(relayRefs);
        Assert.Equal("false", (string?)reference.Attribute("ReferenceOutputAssembly"), ignoreCase: true);

        static bool IsRelay(string? name) => string.Equals(name, "panda-relay", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The client builds relayIds and dial tokens with Panda.Protocol, the code the C# relay uses too: pinned against the
    /// independent vectors (schema/relay-vectors.py), so a bug shared by both cannot hide (D1, critic #14).
    /// </summary>
    [Fact]
    public void The_client_derives_relayIds_and_dial_tokens_as_the_independent_vectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoPaths.Schema("relay-vectors.json")));
        var ids = doc.RootElement.GetProperty("relayId").EnumerateArray().ToList();
        Assert.NotEmpty(ids);
        foreach (var v in ids)
        {
            Assert.Equal(v.GetProperty("relayId").GetString(), DialTokens.RelayIdFromSpki(Convert.FromHexString(v.GetProperty("spkiHex").GetString()!)));
        }

        var tokens = doc.RootElement.GetProperty("dialTokens").EnumerateArray().ToList();
        Assert.Contains(tokens, t => t.GetProperty("kind").GetString() == "link");
        Assert.Contains(tokens, t => t.GetProperty("kind").GetString() == "pair");
        foreach (var v in tokens)
        {
            var relayId = v.GetProperty("relayId").GetString()!;
            var token = v.GetProperty("kind").GetString() == "link"
                ? DialTokens.LinkToken(Convert.FromHexString(v.GetProperty("secretHex").GetString()!), relayId)
                : DialTokens.PairToken(v.GetProperty("code").GetString()!, relayId);
            Assert.Equal(v.GetProperty("token").GetString(), token);
            Assert.True(DialTokens.TryHash(token, out var hash));
            Assert.Equal(v.GetProperty("tokenHash").GetString(), hash);
        }
    }

    [Fact]
    public void A_script_is_refused_as_the_relay_under_test()
    {
        var dir = Directory.CreateTempSubdirectory("panda-conformance-");
        try
        {
            var script = Path.Combine(dir.FullName, "relay");
            File.WriteAllText(script, "#!/bin/sh\nexec ./real-relay \"$@\"\n");
            Assert.True(RelayUnderTest.IsScript(script));
            var cmd = Path.Combine(dir.FullName, "relay.cmd");
            File.WriteAllText(cmd, "@echo off\r\n");
            Assert.True(RelayUnderTest.IsScript(cmd));
            var native = Path.Combine(dir.FullName, "relay.bin");
            File.WriteAllBytes(native, [0x7F, (byte)'E', (byte)'L', (byte)'F']);
            Assert.False(RelayUnderTest.IsScript(native));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
