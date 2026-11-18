using Panda.Bridge.Protocol;

namespace Panda.Relay;

/// <summary>What the hub does with one 4504 dial refusal's log line.</summary>
internal enum NotOnlineLine
{
    /// <summary>The first of its (relayId, kind) this hour: logged with the previous hour's count.</summary>
    First,

    /// <summary>Seen this hour already: counted, not logged.</summary>
    Repeat,

    /// <summary>The hour's table is full: logged plainly, every time (never silent).</summary>
    Untracked,
}

/// <summary>
/// 2026-11-18 (A: "Log VPS bị nhiễu do lỗi 4504"): which 4504 refusals the hub logs. A bridge redials an offline
/// Chrome once a minute for as long as it hosts it, so the first 4504 of each (relayId, kind) in a UTC clock hour is
/// logged, with how many that pair had in the previous hour; the rest of the hour is counted, not logged. Memory:
/// at most <paramref name="maxTracked"/> pairs this hour plus as many kept from the hour before (2 × maxTracked);
/// a pair that does not fit is logged every time. Logging only: the refusal itself, and every other refusal, is
/// unchanged. Not thread-safe: the hub calls it under its gate.
/// </summary>
internal sealed class NotOnlineLog(int maxTracked)
{
    private Dictionary<(string RelayId, RelayStreamKind Kind), int> _thisHour = [];
    private Dictionary<(string RelayId, RelayStreamKind Kind), int> _lastHour = [];
    private DateTime _hour;

    /// <summary>
    /// Counts one 4504. <paramref name="previousHour"/> = that pair's count in the clock hour before (0 when none);
    /// meaningful for <see cref="NotOnlineLine.First"/> only.
    /// </summary>
    public NotOnlineLine Count(string relayId, RelayStreamKind kind, DateTimeOffset now, out int previousHour)
    {
        var utc = now.UtcDateTime;
        var hour = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
        if (hour != _hour)
        {
            // only the hour right before carries its counts; after a gap there is nothing "previous" to report
            _lastHour = hour == _hour.AddHours(1) ? _thisHour : [];
            _thisHour = [];
            _hour = hour;
        }

        var key = (relayId, kind);
        previousHour = 0;
        if (_thisHour.TryGetValue(key, out var seen))
        {
            _thisHour[key] = seen + 1;
            return NotOnlineLine.Repeat;
        }

        if (_thisHour.Count >= maxTracked)
        {
            return NotOnlineLine.Untracked;
        }

        _thisHour[key] = 1;
        previousHour = _lastHour.GetValueOrDefault(key);
        return NotOnlineLine.First;
    }
}
