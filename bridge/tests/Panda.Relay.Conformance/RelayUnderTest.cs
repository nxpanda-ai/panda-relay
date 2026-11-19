using Panda.Bridge.Tests;

namespace Panda.Relay.Conformance;

/// <summary>
/// plan-relay-conformance D2 (seam S1): which relay executable the suite runs. <c>PANDA_RELAY_CMD</c> = a native
/// executable (any language); unset = the C# relay built from this repository, in the test assembly's configuration.
/// </summary>
internal static class RelayUnderTest
{
    public const string CmdVariable = "PANDA_RELAY_CMD";
    public const string LabelVariable = "PANDA_RELAY_LABEL";

    /// <summary>The configuration folder this test assembly was built into (<c>bin/&lt;configuration&gt;/net10.0/</c>).</summary>
    public static string Configuration { get; } = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;

    public static string? Cmd { get; } = Environment.GetEnvironmentVariable(CmdVariable) is { Length: > 0 } cmd ? cmd : null;

    public static string Label { get; } = Environment.GetEnvironmentVariable(LabelVariable) is { Length: > 0 } label
        ? label
        : Cmd is null ? $"csharp-{Configuration}" : Path.GetFileName(Cmd);

    /// <summary>The file to start and the arguments that come before the relay's own (the dll for the dotnet host).</summary>
    public static (string File, string[] Prefix) Resolve()
    {
        if (Cmd is not null)
        {
            var full = Path.GetFullPath(Cmd);
            if (!File.Exists(full))
            {
                throw new InvalidOperationException($"{CmdVariable}: no such file: {full}");
            }

            if (IsScript(full))
            {
                // the measured PID would be the script's, not the relay's (plan D2, critic #10 / #33)
                throw new InvalidOperationException($"{CmdVariable} must be a native executable, not a script: {full}");
            }

            return (full, []);
        }

        var dll = Path.Combine(RepoPaths.Root, "bridge", "src", "Panda.Relay", "bin", Configuration, "net10.0", "panda-relay.dll");
        if (!File.Exists(dll))
        {
            throw new InvalidOperationException($"the C# relay is not built: {dll}");
        }

        return (DotnetHost(), [dll]);
    }

    /// <summary>One line naming the relay under test (printed by the skipped marker test and the fixtures).</summary>
    public static string Describe() => Cmd is null ? $"{Label}: the C# relay of this repository" : $"{Label}: {Cmd}";

    /// <summary>A script by its extension or a <c>#!</c> first line: its PID would be the interpreter's.</summary>
    internal static bool IsScript(string path)
    {
        if (Path.GetExtension(path).ToUpperInvariant() is ".CMD" or ".BAT" or ".PS1" or ".SH")
        {
            return true;
        }

        using var file = File.OpenRead(path);
        Span<byte> head = stackalloc byte[2];
        return file.Read(head) == 2 && head[0] == (byte)'#' && head[1] == (byte)'!';
    }

    /// <summary>The dotnet host of the runtime running these tests (…/dotnet/shared/Microsoft.NETCore.App/&lt;v&gt;/ → …/dotnet/).</summary>
    private static string DotnetHost()
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var host = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        return File.Exists(host) ? host : "dotnet";
    }
}
