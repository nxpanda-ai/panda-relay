using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Panda.Bridge.Protocol;

/// <summary>Result of parsing one WebSocket text frame (seam S1).</summary>
public abstract record ParseResult
{
    private ParseResult() { }

    public sealed record Ok(IMessage Message) : ParseResult;

    /// <summary>A well-formed object whose <c>type</c> this protocol version does not know.</summary>
    public sealed record Unknown(string Type) : ParseResult;

    /// <summary>Not JSON, wrong shape, or a constraint violation → close 4004 PROTOCOL_ERROR (§71.2).</summary>
    public sealed record Invalid(string Reason) : ParseResult;
}

/// <summary>Result of parsing one native-messaging frame (plan-native ND3). A WebSocket type is <c>Unknown</c> here.</summary>
public abstract record NativeParseResult
{
    private NativeParseResult() { }

    public sealed record Ok(INativeMessage Message) : NativeParseResult;

    public sealed record Unknown(string Type) : NativeParseResult;

    public sealed record Invalid(string Reason) : NativeParseResult;
}

/// <summary>Result of parsing one relay control text frame (plan-relay S1). Another channel's type is <c>Unknown</c> here.</summary>
public abstract record RelayParseResult
{
    private RelayParseResult() { }

    public sealed record Ok(IRelayMessage Message) : RelayParseResult;

    public sealed record Unknown(string Type) : RelayParseResult;

    public sealed record Invalid(string Reason) : RelayParseResult;
}

/// <summary>Strict (de)serialization of protocol messages. Parse never throws on bad input.</summary>
public static partial class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        // Missing required members, nulls in non-nullable members and unknown members are errors,
        // matching "required" and "additionalProperties": false in the schema.
        // Source: https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/required-properties
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        // Non-ASCII stays readable; JSON escaping of quotes/control characters is still applied.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // plan-aot: protocol types only from the generated source-gen context (Native AOT has no reflection JSON), under
        // these options; any other type is NotSupportedException (A5b removed A1's temporary reflection fallback).
        TypeInfoResolver = ProtocolJsonContext.Default,
    };

    /// <summary>A protocol type's metadata under <see cref="Options"/> (plan-aot: never the context's own options).</summary>
    public static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    public static ParseResult Parse(string text) => ParseCore(text, MessageTypes) switch
    {
        { Message: { } message } => new ParseResult.Ok((IMessage)message),
        { UnknownType: { } type } => new ParseResult.Unknown(type),
        var core => new ParseResult.Invalid(core.Reason!),
    };

    /// <summary>Parses one native-messaging frame (UTF-8 JSON text). Never throws on bad input.</summary>
    public static NativeParseResult ParseNative(string text) => ParseCore(text, NativeMessageTypes) switch
    {
        { Message: { } message } => new NativeParseResult.Ok((INativeMessage)message),
        { UnknownType: { } type } => new NativeParseResult.Unknown(type),
        var core => new NativeParseResult.Invalid(core.Reason!),
    };

    /// <summary>Serializes a valid native message. Throws on a message that breaks the schema — that is a host bug.</summary>
    public static string SerializeNative(INativeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SerializeCore(message, message.Type);
    }

    /// <summary>Parses one relay control text frame. Never throws on bad input.</summary>
    public static RelayParseResult ParseRelay(string text) => ParseCore(text, RelayMessageTypes) switch
    {
        { Message: { } message } => new RelayParseResult.Ok((IRelayMessage)message),
        { UnknownType: { } type } => new RelayParseResult.Unknown(type),
        var core => new RelayParseResult.Invalid(core.Reason!),
    };

    /// <summary>Serializes a valid relay message. Throws on a message that breaks the schema — that is a caller bug.</summary>
    public static string SerializeRelay(IRelayMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SerializeCore(message, message.Type);
    }

    /// <summary>Exactly one member is set: the valid message, the unknown type name, or the invalid reason.</summary>
    private readonly record struct Core(object? Message, string? UnknownType, string? Reason)
    {
        public static Core Invalid(string reason) => new(null, null, reason);
    }

    private static Core ParseCore(string text, Dictionary<string, Type> types)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return Core.Invalid("missing type");
            }

            // GetString throws on an escaped lone surrogate; the catch below turns that into Invalid.
            var type = typeElement.GetString()!;
            if (!types.TryGetValue(type, out var clrType))
            {
                return new Core(null, type, null);
            }

            // The schema has no nullable member: an optional member is absent, never null.
            if (ContainsNull(root))
            {
                return Core.Invalid("null is not allowed");
            }

            if (root.Deserialize(Options.GetTypeInfo(clrType)) is not { } message)
            {
                return Core.Invalid("null message");
            }

            var violation = ProtocolValidation.FirstViolation(message);
            return violation is null ? new Core(message, null, null) : Core.Invalid(violation);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            // Remote input must never escape as an exception (seam S1); the caller closes with 4004.
            return Core.Invalid(e.GetType().Name);
        }
    }

    /// <summary>
    /// plan-record-replay T4: one protocol value read back from a file, not a message (a saved <see cref="Recording"/>),
    /// under the same strict rules as <see cref="Parse"/> — a JSON object, no null, no unknown member, the schema's
    /// checks. Null when it breaks any of them; never throws on bad input.
    /// </summary>
    public static T? ParseValue<T>(ReadOnlyMemory<byte> utf8)
        where T : class
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || ContainsNull(doc.RootElement))
            {
                return null;
            }

            var value = doc.RootElement.Deserialize(Info<T>());
            return value is not null && ProtocolValidation.FirstViolation(value) is null ? value : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            return null; // a file anyone with the user's rights can edit: bad content is never an exception
        }
    }

    /// <summary>Serializes a valid message. Throws on a message that breaks the schema — that is a bridge bug.</summary>
    public static string Serialize(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SerializeCore(message, message.Type);
    }

    private static string SerializeCore(object message, string type)
    {
        var violation = ProtocolValidation.FirstViolation(message);
        if (violation is not null)
        {
            throw new InvalidOperationException($"refusing to send {type}: {violation}");
        }

        return JsonSerializer.Serialize(message, Options.GetTypeInfo(message.GetType()));
    }

    private static bool ContainsNull(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => true,
        JsonValueKind.Object => element.EnumerateObject().Any(p => ContainsNull(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(ContainsNull),
        _ => false,
    };
}
