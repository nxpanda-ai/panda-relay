namespace Panda.Bridge.Tests;

internal static class RepoPaths
{
    /// <summary>Repository root: the first ancestor of the test binaries that contains schema/protocol.v1.json.</summary>
    public static string Root { get; } = FindRoot();

    public static string Schema(params string[] parts) => Path.Combine([Root, "schema", .. parts]);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "schema", "protocol.v1.json")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }
}
