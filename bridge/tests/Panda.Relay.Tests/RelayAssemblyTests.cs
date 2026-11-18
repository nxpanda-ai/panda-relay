using System.Xml.Linq;
using Panda.Bridge.Tests;

namespace Panda.Relay.Tests;

/// <summary>
/// plan-protocol-project AC1 / AC2 (critic #1): the relay depends on the shared Panda.Protocol only — never on the user's
/// app (panda-browser). The test project itself references the bridge (its tests run the real bridge), so a relay →
/// bridge reference added back would still compile and pass everything else: these tests are the guard.
/// </summary>
public class RelayAssemblyTests
{
    private const string BridgeAssembly = "panda-browser";

    [Fact]
    public void The_relay_assembly_references_Panda_Protocol_and_not_the_bridge()
    {
        var names = typeof(RelayHub).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain(BridgeAssembly, names);
        Assert.Contains("Panda.Protocol", names);
    }

    [Fact]
    public void The_relays_dependency_list_has_no_bridge_by_any_path()
    {
        // diff critic #1: GetReferencedAssemblies lists only what the code uses, and the csproj test sees only a direct
        // reference — panda-relay.deps.json is what a publish ships from, transitive references included (AC2)
        var deps = Path.ChangeExtension(typeof(RelayHub).Assembly.Location, ".deps.json");
        var text = File.ReadAllText(deps);
        Assert.DoesNotContain(BridgeAssembly, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Panda.Protocol", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_relay_project_has_no_reference_to_the_bridge_project()
    {
        var csproj = Path.Combine(RepoPaths.Root, "bridge", "src", "Panda.Relay", "Panda.Relay.csproj");
        var references = XDocument.Load(csproj).Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? "")
            .ToList();
        Assert.DoesNotContain(references, r => r.Contains("Panda.Bridge", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(references, r => r.EndsWith("Panda.Protocol.csproj", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// plan-oss-relay D2: this test project is exported as is (the open-source relay repo has no bridge), so it never
    /// references the bridge — the relay tests that run the real bridge live in Panda.Relay.Bridge.Tests.
    /// </summary>
    [Fact]
    public void The_relay_tests_do_not_reference_the_bridge_by_any_path()
    {
        var self = typeof(RelayAssemblyTests).Assembly;
        Assert.DoesNotContain(BridgeAssembly, self.GetReferencedAssemblies().Select(a => a.Name));
        var deps = File.ReadAllText(Path.ChangeExtension(self.Location, ".deps.json"));
        Assert.DoesNotContain(BridgeAssembly, deps, StringComparison.OrdinalIgnoreCase);
        var csproj = Path.Combine(RepoPaths.Root, "bridge", "tests", "Panda.Relay.Tests", "Panda.Relay.Tests.csproj");
        Assert.DoesNotContain(XDocument.Load(csproj).Descendants("ProjectReference"),
            e => ((string?)e.Attribute("Include") ?? "").Contains("Panda.Bridge", StringComparison.OrdinalIgnoreCase));
    }
}
