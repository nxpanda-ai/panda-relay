using System.Buffers;
using System.Net.WebSockets;
using System.Numerics;
using Panda.Bridge.Protocol;

namespace Panda.Relay.Tests;

/// <summary>
/// plan-zero-alloc-relay D2 / seam S1: RelaySocket lends one receive buffer per socket, and a message larger than it a
/// pooled array that goes back to the pool on the next receive or on Dispose — never while a read still writes it.
/// </summary>
public class ReceiveBufferTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RelayOptions Options(int maxFrameBytes = 512 * 1024) =>
        new() { PublicHost = "relay.example", MaxFrameBytes = maxFrameBytes };

    [Fact]
    public async Task A_message_larger_than_the_own_buffer_arrives_whole_and_its_lease_ends_at_the_next_receive()
    {
        var (client, server, _) = AllocationTests.Rig.Pair();
        var pool = new RecordingPool();
        using var socket = new RelaySocket(server, Options(), TimeProvider.System, pool);
        var big = Payload(100 * 1024, seed: 1);

        await client.SendAsync(big, WebSocketMessageType.Binary, endOfMessage: true, Ct);
        var frame = await socket.ReceiveAsync();

        Assert.Equal(FrameKind.Binary, frame.Kind);
        Assert.Equal(big, frame.Bytes.ToArray());
        Assert.Equal(pool.Rents - 1, pool.Returns); // the array the frame lends is still out

        var small = Payload(10, seed: 2);
        await client.SendAsync(small, WebSocketMessageType.Binary, endOfMessage: true, Ct);
        frame = await socket.ReceiveAsync();

        Assert.Equal(small, frame.Bytes.ToArray());
        Assert.Equal(pool.Rents, pool.Returns);
        pool.AssertNoWriteAfterReturn();
    }

    [Fact]
    public async Task Dispose_during_a_multi_read_message_never_returns_an_array_a_read_still_writes()
    {
        var (client, server, _) = AllocationTests.Rig.Pair();
        var pool = new RecordingPool();
        var socket = new RelaySocket(server, Options(), TimeProvider.System, pool);
        var message = Payload(100 * 1024, seed: 3);

        await client.SendAsync(message.AsMemory(0, 40 * 1024), WebSocketMessageType.Binary, endOfMessage: false, Ct);
        var receive = socket.ReceiveAsync();
        // 16 KiB own → 32 KiB → 64 KiB (the superseded 32 KiB returned): the read now writes a rented array
        await WaitUntilAsync(() => pool.Rents == 2 && pool.Returns == 1);
        Assert.False(receive.IsCompleted);

        socket.Dispose();
        Assert.Equal(1, pool.Returns); // Dispose never returns the array being read into

        await client.SendAsync(message.AsMemory(40 * 1024), WebSocketMessageType.Binary, endOfMessage: true, Ct);
        var frame = await receive;

        Assert.Equal(FrameKind.Lost, frame.Kind); // the socket was disposed: its frame is never lent
        Assert.Equal(pool.Rents, pool.Returns);
        pool.AssertNoWriteAfterReturn();
    }

    [Fact]
    public async Task Dispose_at_the_first_rent_never_returns_an_array_a_read_still_writes()
    {
        var (client, server, _) = AllocationTests.Rig.Pair();
        var pool = new RecordingPool();
        var socket = new RelaySocket(server, Options(), TimeProvider.System, pool);
        pool.OnRent = socket.Dispose; // Dispose lands between the rent and the read into the rented array

        await client.SendAsync(Payload(100 * 1024, seed: 4), WebSocketMessageType.Binary, endOfMessage: true, Ct);
        var frame = await socket.ReceiveAsync();

        Assert.Equal(FrameKind.Lost, frame.Kind);
        Assert.True(pool.Rents > 0);
        Assert.Equal(pool.Rents, pool.Returns);
        pool.AssertNoWriteAfterReturn();
    }

    [Fact]
    public async Task A_lease_published_before_Dispose_is_returned_once_by_Dispose()
    {
        var (client, server, _) = AllocationTests.Rig.Pair();
        var pool = new RecordingPool();
        var socket = new RelaySocket(server, Options(), TimeProvider.System, pool);

        await client.SendAsync(Payload(50 * 1024, seed: 5), WebSocketMessageType.Binary, endOfMessage: true, Ct);
        Assert.Equal(FrameKind.Binary, (await socket.ReceiveAsync()).Kind);
        socket.Dispose();
        socket.Dispose();

        Assert.Equal(pool.Rents, pool.Returns);
        pool.AssertNoWriteAfterReturn();
    }

    [Fact]
    public async Task An_oversized_message_consumes_exactly_MaxFrameBytes_plus_one_before_TooLarge()
    {
        // 40 KiB + 1 is not a pool size: the pool hands out 64 KiB, so an unsliced read would take more (critic F7)
        var (client, server, _) = AllocationTests.Rig.Pair();
        var pool = new RecordingPool();
        var limit = 40 * 1024;
        using var socket = new RelaySocket(server, Options(limit), TimeProvider.System, pool);
        var size = 50 * 1024;

        await client.SendAsync(Payload(size, seed: 6), WebSocketMessageType.Binary, endOfMessage: true, Ct);
        Assert.Equal(FrameKind.TooLarge, (await socket.ReceiveAsync()).Kind);

        var rest = 0;
        var buffer = new byte[size];
        ValueWebSocketReceiveResult result;
        do
        {
            // bounded: an unsliced read would have eaten the whole message and this read would wait forever
            result = await server.ReceiveAsync(buffer.AsMemory(), Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
            rest += result.Count;
        }
        while (!result.EndOfMessage);

        Assert.Equal(size - (limit + 1), rest);
        Assert.Equal(pool.Rents, pool.Returns); // TooLarge publishes nothing
        pool.AssertNoWriteAfterReturn();
    }

    [Fact]
    public async Task A_text_message_is_lent_like_a_binary_one()
    {
        var (client, server, _) = AllocationTests.Rig.Pair();
        using var socket = new RelaySocket(server, Options(), TimeProvider.System, new RecordingPool());

        await client.SendAsync("{\"type\":\"PING\"}"u8.ToArray(), WebSocketMessageType.Text, endOfMessage: true, Ct);
        var frame = await socket.ReceiveAsync();

        Assert.Equal(FrameKind.Text, frame.Kind);
        Assert.Equal("{\"type\":\"PING\"}", System.Text.Encoding.UTF8.GetString(frame.Bytes.Span));
    }

    /// <summary>
    /// Seam S1's consumer side: distinct random payloads 1 B .. MaxFrameBytes both ways at once through the relay's pump,
    /// until the day's quota closes the stream mid-run — every message that arrives is byte-identical and in order.
    /// </summary>
    [Fact]
    public async Task Distinct_payloads_arrive_byte_identical_and_in_order_until_a_quota_close()
    {
        var max = 512 * 1024;
        var rng = new Random(20261014);
        int[] edges = [1, 16 * 1024 - 1, 16 * 1024, 16 * 1024 + 1, 32 * 1024 + 1, max];
        List<byte[]> up = [], down = [];
        for (var i = 0; i < 30; i++)
        {
            up.Add(Payload(i < edges.Length ? edges[i] : rng.Next(1, max + 1), seed: 1000 + i));
            down.Add(Payload(i < edges.Length ? edges[^(i + 1)] : rng.Next(1, max + 1), seed: 2000 + i));
        }

        var total = up.Sum(p => (long)p.Length) + down.Sum(p => (long)p.Length);
        var options = new RelayOptions { PublicHost = "relay.example", BytesPerDay = total / 2 };
        await using var rig = AllocationTests.Rig.Relay(16, options);

        var sendUp = SendAllAsync(rig.ClientA, up);
        var sendDown = SendAllAsync(rig.ClientB, down);
        var gotUp = ReceiveUntilCloseAsync(rig.ClientB);
        var gotDown = ReceiveUntilCloseAsync(rig.ClientA);
        await Task.WhenAll(sendUp, sendDown, gotUp, gotDown).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var (upArrived, upClose) = await gotUp;
        var (downArrived, downClose) = await gotDown;

        AssertPrefix(up, upArrived);
        AssertPrefix(down, downArrived);
        Assert.True(upArrived.Count + downArrived.Count > 0, "nothing arrived before the quota");
        Assert.True(upArrived.Count < up.Count || downArrived.Count < down.Count, "the quota never closed");
        Assert.Equal((WebSocketCloseStatus)RelayCloseCodes.QuotaExceeded, upClose);
        Assert.Equal((WebSocketCloseStatus)RelayCloseCodes.QuotaExceeded, downClose);
    }

    private static void AssertPrefix(List<byte[]> sent, List<byte[]> got)
    {
        Assert.True(got.Count <= sent.Count);
        for (var i = 0; i < got.Count; i++)
        {
            Assert.True(sent[i].AsSpan().SequenceEqual(got[i]), $"message {i} ({sent[i].Length} B) arrived changed ({got[i].Length} B)");
        }
    }

    private static async Task SendAllAsync(WebSocket client, List<byte[]> payloads)
    {
        try
        {
            foreach (var p in payloads)
            {
                await client.SendAsync(p, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            // the relay closed the stream
        }
    }

    private static async Task<(List<byte[]> Messages, WebSocketCloseStatus? Close)> ReceiveUntilCloseAsync(WebSocket client)
    {
        List<byte[]> messages = [];
        var buffer = new byte[64 * 1024];
        var message = new ArrayBufferWriter<byte>();
        while (true)
        {
            var result = await client.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (messages, client.CloseStatus);
            }

            message.Write(buffer.AsSpan(0, result.Count));
            if (result.EndOfMessage)
            {
                messages.Add(message.WrittenSpan.ToArray());
                message.ResetWrittenCount();
            }
        }
    }

    private static byte[] Payload(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(5, Ct);
        }
    }

    /// <summary>
    /// Hands out fresh arrays rounded up to a power of two (as ArrayPool.Shared does), records every rent and return,
    /// fills a returned array with a sentinel, and later proves nobody wrote it after its return.
    /// </summary>
    internal sealed class RecordingPool : ArrayPool<byte>
    {
        private const byte Sentinel = 0xEE;
        private readonly Lock _gate = new();
        private readonly List<byte[]> _rented = [];
        private readonly List<byte[]> _returned = [];
        private int _foreignOrDouble;
        private int _notCleared;

        public Action? OnRent { get; set; }

        public int Rents
        {
            get
            {
                lock (_gate)
                {
                    return _rented.Count;
                }
            }
        }

        public int Returns
        {
            get
            {
                lock (_gate)
                {
                    return _returned.Count;
                }
            }
        }

        public override byte[] Rent(int minimumLength)
        {
            var array = new byte[(int)BitOperations.RoundUpToPowerOf2((uint)minimumLength)];
            lock (_gate)
            {
                _rented.Add(array);
            }

            OnRent?.Invoke();
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            lock (_gate)
            {
                if (!_rented.Contains(array) || _returned.Contains(array))
                {
                    _foreignOrDouble++;
                }

                if (!clearArray)
                {
                    _notCleared++; // tunnel bytes or control secrets would stay in the shared pool (critic F12)
                }

                _returned.Add(array);
            }

            array.AsSpan().Fill(Sentinel);
        }

        public void AssertNoWriteAfterReturn()
        {
            lock (_gate)
            {
                Assert.Equal(0, _foreignOrDouble);
                Assert.Equal(0, _notCleared);
                foreach (var array in _returned)
                {
                    Assert.True(array.AsSpan().IndexOfAnyExcept(Sentinel) < 0, "an array was written after it went back to the pool");
                }
            }
        }
    }
}
