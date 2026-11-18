using System.Text.Json;

namespace Panda.Bridge.Protocol;

public static partial class ProtocolInfo
{
    /// <summary>
    /// plan-phase2 D4: the bridge and the native host serve every version in <see cref="MinVersion"/>..<see cref="Version"/>,
    /// so a half-updated install keeps Phase 1 in both directions (AC9).
    /// </summary>
    public static bool IsSupported(long version) => version is >= MinVersion and <= Version;

    /// <summary>
    /// plan-phase3 D2: a browser of this protocol version understands this message — its first version (schema
    /// <c>x-since</c>, <see cref="MessageSince"/>) is not newer, and the version is one this bridge serves.
    /// </summary>
    public static bool Supports(string messageType, long version) =>
        MessageSince.TryGetValue(messageType, out var since) && version >= since && IsSupported(version);

    /// <summary>
    /// plan-phase8 D2 / seam S7: the lowest protocol version that can carry <paramref name="message"/> as built — its
    /// <see cref="MessageSince"/>, raised by the <see cref="FieldSince"/> of every newer field it carries (a field at
    /// its default counts too: the tools leave it out to fit an older link). Read from the wire form under
    /// <see cref="ProtocolJson.Options"/> (source-generated, Native AOT safe), so the names are FieldSince's own.
    /// </summary>
    public static long NeededVersion(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var needed = MessageSince[message.Type];
        if (!FieldSince.TryGetValue(message.Type, out var fields))
        {
            return needed;
        }

        var wire = JsonSerializer.SerializeToElement(message, ProtocolJson.Options.GetTypeInfo(message.GetType()));
        foreach (var property in wire.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Null && fields.TryGetValue(property.Name, out var since))
            {
                needed = Math.Max(needed, since);
            }
        }

        return needed;
    }

    /// <summary>The Phase 2 commands (plan-phase2 D4): a v1 browser never receives one.</summary>
    public static bool SupportsCommands(long version) => Supports("CMD_SNAPSHOT", version);

    /// <summary>The Phase 3 page actions (plan-phase3 D2): a v1 or v2 browser never receives one.</summary>
    public static bool SupportsWrites(long version) => Supports("CMD_POINTER", version);
}
