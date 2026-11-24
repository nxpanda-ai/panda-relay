namespace Panda.Relay;

/// <summary>What used an alias host (plan-relay-rename D6).</summary>
internal enum AliasUse
{
    /// <summary>A RELAY_REGISTER whose proof verified for an alias host.</summary>
    Registration,

    /// <summary>A /v1/stream dial whose request Host is an alias host.</summary>
    Dial,
}

/// <summary>
/// plan-relay-rename D6 (AC6): how much a renamed relay's old name is still used, so its owner can decide when to
/// retire it. Counted per UTC clock hour; the first use of each hour is logged with the counts of the last hour that had
/// a use (named, so a gap between uses loses nothing — diff critic F2), the rest of the hour only counted — never one
/// line per event, never a relayId. Not thread-safe: the hub calls it under its gate.
/// </summary>
internal sealed class AliasUseLog
{
    private DateTime? _hour;
    private int _registrations;
    private int _dials;

    /// <summary>
    /// Counts one use. True = the first of this hour: log it, with the last hour that had a use (null: none before)
    /// and that hour's counts.
    /// </summary>
    public bool Count(AliasUse use, DateTimeOffset now, out DateTime? lastHour, out int lastRegistrations, out int lastDials)
    {
        var utc = now.UtcDateTime;
        var hour = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
        var first = hour != _hour;
        (lastHour, lastRegistrations, lastDials) = (null, 0, 0);
        if (first)
        {
            (lastHour, lastRegistrations, lastDials) = (_hour, _registrations, _dials);
            (_hour, _registrations, _dials) = (hour, 0, 0);
        }

        if (use == AliasUse.Registration)
        {
            _registrations++;
        }
        else
        {
            _dials++;
        }

        return first;
    }

    /// <summary>The hour as the log line names it: <c>2026-11-24T10Z</c>, or <c>none</c>.</summary>
    public static string Name(DateTime? hour) =>
        hour?.ToString("yyyy-MM-dd'T'HH'Z'", System.Globalization.CultureInfo.InvariantCulture) ?? "none";
}
