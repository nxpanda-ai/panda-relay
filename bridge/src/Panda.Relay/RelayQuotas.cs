using System.Net;

namespace Panda.Relay;

/// <summary>
/// S6 / S1 counters. Per relayId per UTC day: bytes piped (both directions) and streams opened — only token-admitted
/// streams move them. Per IP per clock hour: registrations, token-refused dials and /v1/stream noise (C6) (4530, never
/// a Chrome's quota).
/// Not thread-safe: <see cref="RelayHub"/> calls it under its lock. Only the current day and hour are kept (a new
/// window drops the old one whole), checks never add an entry, and past <see cref="RelayOptions.MaxTrackedIps"/>
/// IP buckets in one hour every new bucket shares one overflow counter — memory stays bounded and O(1) per call.
/// plan-relay-prefix: one IPv6 /48 owns at most <see cref="RelayOptions.MaxBucketsPerPrefix"/> /64 buckets an hour; its
/// further /64s share one bucket of that /48 (counted in the table), so a client holding one /48 cannot fill the table;
/// once something else filled it, a /48 without a shared bucket goes to the overflow like any new bucket.
/// </summary>
internal sealed class RelayQuotas(RelayOptions options)
{
    private readonly Dictionary<string, Day> _days = new(StringComparer.Ordinal);
    private readonly Dictionary<IPAddress, Hour> _hours = [];
    private readonly Dictionary<IPAddress, Hour> _prefixHours = []; // plan-relay-prefix D1: a /48's shared bucket, own keys
    private readonly Dictionary<IPAddress, int> _perPrefix = []; // D2: /64 buckets each /48 owns this hour
    private readonly HashSet<(IPAddress Ip, string RelayId)> _seenNotOnline = []; // C6: 4504 dedup, this hour only
    private DateTime _day;
    private DateTime _hour;
    private Hour? _overflow;

    internal int TrackedIps => Tracked + (_overflow is null ? 0 : 1);

    /// <summary>The table's entries: IP buckets and /48 shared buckets (plan-relay-prefix D3).</summary>
    private int Tracked => _hours.Count + _prefixHours.Count;

    internal int TrackedChromes => _days.Count;

    /// <summary>True when the relayId may not open a stream now; <paramref name="retryAfter"/> = seconds to the next day.</summary>
    public bool Blocked(string relayId, DateTimeOffset now, out int retryAfter)
    {
        retryAfter = DayWindow(now);
        _days.TryGetValue(relayId, out var day); // no entry = nothing spent yet (a limit of 0 still blocks)
        return (day?.Bytes ?? 0) > options.BytesPerDay || (day?.Streams ?? 0) >= options.StreamsPerDay;
    }

    public void CountStream(string relayId, DateTimeOffset now)
    {
        DayWindow(now);
        DayOf(relayId).Streams++;
    }

    /// <summary>Adds piped bytes; true when this crossed the day's byte quota (every stream of the relayId closes).</summary>
    public bool AddBytes(string relayId, long bytes, DateTimeOffset now, out int retryAfter)
    {
        retryAfter = DayWindow(now);
        var day = DayOf(relayId);
        day.Bytes += bytes;
        return day.Bytes > options.BytesPerDay;
    }

    /// <summary>Counts one /v1/browser connection; false (with the seconds left in the hour) when the IP is over its limit.</summary>
    public bool TryCountRegistration(IPAddress ip, DateTimeOffset now, out int retryAfter)
    {
        retryAfter = HourWindow(now);
        var hour = HourOf(ip);
        if (hour.Registrations >= options.RegistrationsPerIpPerHour)
        {
            return false;
        }

        hour.Registrations++;
        return true;
    }

    public bool DialerLimited(IPAddress ip, DateTimeOffset now, out int retryAfter)
    {
        retryAfter = HourWindow(now);
        var hour = Resolve(ip, create: false);
        return hour is not null && (hour.RefusedDials >= options.RefusedDialsPerIpPerHour || hour.Noise >= options.NoisePerIpPerHour);
    }

    /// <summary>plan-relay-deploy C6: one unit of noise (an unknown / late / wrong-owner accept, a malformed request).</summary>
    public void CountNoise(IPAddress ip, DateTimeOffset now)
    {
        HourWindow(now);
        HourOf(ip).Noise++;
    }

    /// <summary>
    /// plan-relay-deploy C6: a 4504 costs one unit per (IP, relayId) per hour, so retrying offline targets stays cheap;
    /// past <see cref="RelayOptions.MaxTrackedNoise"/> remembered pairs every new one counts (strict, memory bound).
    /// </summary>
    public void CountNotOnline(IPAddress ip, string relayId, DateTimeOffset now)
    {
        HourWindow(now);
        var key = (ip, relayId);
        if (_seenNotOnline.Contains(key))
        {
            return;
        }

        if (_seenNotOnline.Count < options.MaxTrackedNoise)
        {
            _seenNotOnline.Add(key);
        }

        HourOf(ip).Noise++;
    }

    public void CountRefusedDial(IPAddress ip, DateTimeOffset now)
    {
        HourWindow(now);
        HourOf(ip).RefusedDials++;
    }

    /// <summary>Moves to <paramref name="now"/>'s UTC day (dropping the last one); returns the seconds left in it.</summary>
    private int DayWindow(DateTimeOffset now)
    {
        var date = now.UtcDateTime.Date;
        if (date != _day)
        {
            _days.Clear();
            _day = date;
        }

        return Seconds(now, date.AddDays(1));
    }

    /// <summary>Moves to <paramref name="now"/>'s clock hour (dropping the last one); returns the seconds left in it.</summary>
    private int HourWindow(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        var start = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
        if (start != _hour)
        {
            _hours.Clear();
            _prefixHours.Clear();
            _perPrefix.Clear();
            _seenNotOnline.Clear();
            _overflow = null;
            _hour = start;
        }

        return Seconds(now, start.AddHours(1));
    }

    private Day DayOf(string relayId)
    {
        if (!_days.TryGetValue(relayId, out var day))
        {
            _days[relayId] = day = new Day();
        }

        return day;
    }

    private Hour HourOf(IPAddress ip) => Resolve(ip, create: true)!;

    /// <summary>
    /// plan-relay-prefix AC3 / D2: the bucket a count of <paramref name="ip"/> (already <see cref="Bucket"/>ed) uses. A
    /// check (<paramref name="create"/> false) sees the same bucket and never adds an entry nor spends a /48's slot.
    /// Order: (1) its /48's shared bucket, even on a full table; (2) a full table → the overflow; (3) a /48 that owns its
    /// cap of /64s → its new shared bucket; (4) a new bucket of its own. IPv4 takes (2) and (4) only.
    /// </summary>
    private Hour? Resolve(IPAddress ip, bool create)
    {
        if (_hours.TryGetValue(ip, out var hour))
        {
            return hour;
        }

        var prefix = Prefix48(ip);
        if (prefix is not null && _prefixHours.TryGetValue(prefix, out var shared))
        {
            return shared;
        }

        if (Tracked >= options.MaxTrackedIps)
        {
            return create ? _overflow ??= new Hour() : _overflow;
        }

        if (!create)
        {
            return null;
        }

        if (prefix is not null)
        {
            var owned = _perPrefix.GetValueOrDefault(prefix);
            if (owned >= options.MaxBucketsPerPrefix)
            {
                return _prefixHours[prefix] = new Hour();
            }

            _perPrefix[prefix] = owned + 1;
        }

        return _hours[ip] = new Hour();
    }

    /// <summary>The /48 of an IPv6 bucket (bytes 6..15 cleared); null for IPv4.</summary>
    private static IPAddress? Prefix48(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 || ip.IsIPv4MappedToIPv6)
        {
            return null;
        }

        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 6, 10);
        return new IPAddress(bytes);
    }

    /// <summary>
    /// The per-IP key: an IPv4-mapped address counts as its IPv4 address, and IPv6 counts per /64 (one client usually
    /// holds a whole /64, so per-address limits would be free to dodge).
    /// </summary>
    public static IPAddress Bucket(IPAddress? ip)
    {
        if (ip is null)
        {
            return IPAddress.None;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            return ip.MapToIPv4();
        }

        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return ip;
        }

        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes);
    }

    /// <summary>Whole seconds until <paramref name="end"/>, in 1..86400 (S6: the reason is a decimal integer in that range).</summary>
    private static int Seconds(DateTimeOffset now, DateTime end) =>
        (int)Math.Clamp(Math.Ceiling((end - now.UtcDateTime).TotalSeconds), 1, 86400);

    private sealed class Day
    {
        public long Bytes { get; set; }
        public int Streams { get; set; }
    }

    private sealed class Hour
    {
        public int Registrations { get; set; }
        public int RefusedDials { get; set; }
        public int Noise { get; set; }
    }
}
