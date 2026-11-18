namespace Panda.Bridge.Protocol;

/// <summary>Hand-written addition to the generated validator: reuses its schema patterns instead of copying them.</summary>
internal static partial class ProtocolValidation
{
    /// <summary>True when <paramref name="value"/> matches the schema's browserInstanceId / browserId pattern (§71.6).</summary>
    public static bool IsBrowserId(string value) => P1.IsMatch(value); // P1 = "^[a-z0-9]+-[a-z2-7]{8}\z" (locked by IsBrowserId_uses_the_browserId_pattern)
}
