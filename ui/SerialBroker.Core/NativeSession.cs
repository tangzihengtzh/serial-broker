using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SerialBroker.Core;

public sealed record ClientInfo(ulong Id, string Name, bool Writer)
{
    public override string ToString() => $"{(Writer ? "写入" : "在线")} · {Name}  #{Id}";
}
public sealed record Snapshot(bool Running, int Policy, ulong Writer, ulong Rx, ulong Tx, ClientInfo[] Clients)
{
    public static Snapshot Empty { get; } = new(false, 2, 0, 0, 0, []);
}
public sealed record MonitorEntry(DateTimeOffset Time, string Direction, string Text, byte[]? Bytes = null)
{
    public string Render(bool hex) => Bytes == null ? Text.TrimEnd() : hex
        ? Convert.ToHexString(Bytes).Chunk(2).Select(x => new string(x)).Aggregate(new StringBuilder(), (a, x) => a.Append(x).Append(' ')).ToString()
        : string.Concat(Bytes.Select(x => x switch { 13 => "\\r", 10 => "\\n", 9 => "\\t", 92 => "\\\\", >= 32 and < 127 => ((char)x).ToString(), _ => $"\\x{x:X2}" }));
}

public sealed class NativeSession : IDisposable
{
    private const string Library = "SerialBroker.Native";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Notice(IntPtr context, IntPtr text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Data(IntPtr context, int tx, IntPtr bytes, int count);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sb_create(Notice notice, Data data, IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int sb_start(IntPtr instance, string port, uint baud, ushort tcp, int enabled, int dtr, int rts, string logs, string? testPipe, byte[] error, int capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void sb_stop(IntPtr instance);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void sb_destroy(IntPtr instance);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void sb_policy(IntPtr instance, int policy);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void sb_disconnect(IntPtr instance, ulong id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sb_snapshot(IntPtr instance, byte[] data, int capacity);
    private readonly object _gate = new();
    private readonly Notice _notice;
    private readonly Data _data;
    private readonly ConcurrentQueue<MonitorEntry> _entries = new();
    private int _queuedBytes, _queuedItems, _dropped;
    private IntPtr _handle;
    private Snapshot _last = Snapshot.Empty;

    public NativeSession()
    {
        // 回调只复制/排队，不同步切回UI，避免stop等待线程时与UI相互等待。
        _notice = (_, text) => Enqueue(new(DateTimeOffset.Now, "事件", Marshal.PtrToStringUTF8(text) ?? ""));
        _data = (_, tx, pointer, size) => { var bytes = new byte[size]; Marshal.Copy(pointer, bytes, 0, size); Enqueue(new(DateTimeOffset.Now, tx == 0 ? "RX" : "TX", "", bytes)); };
        _handle = sb_create(_notice, _data, IntPtr.Zero);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("无法创建原生串口实例");
    }
    private void Enqueue(MonitorEntry entry)
    {
        int size = entry.Bytes?.Length ?? Encoding.UTF8.GetByteCount(entry.Text);
        int count = Interlocked.Increment(ref _queuedItems);
        int total = Interlocked.Add(ref _queuedBytes, size);
        if (count > 2048 || total > 512 * 1024)
        {
            // 使用锁外队列，限制仅影响监视显示，不影响Broker转发及日志。
            Interlocked.Decrement(ref _queuedItems);
            Interlocked.Add(ref _queuedBytes, -size);
            Interlocked.Increment(ref _dropped);
            return;
        }
        _entries.Enqueue(entry);
    }
    public IReadOnlyList<MonitorEntry> Drain(int maximum = 200)
    {
        var result = new List<MonitorEntry>();
        int dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0) result.Add(new(DateTimeOffset.Now, "事件", $"显示队列已略过 {dropped} 条；转发及磁盘日志独立运行。"));
        while (result.Count < maximum && _entries.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _queuedItems);
            Interlocked.Add(ref _queuedBytes, -(item.Bytes?.Length ?? Encoding.UTF8.GetByteCount(item.Text)));
            result.Add(item);
        }
        return result;
    }
    public Task StartAsync(ConnectionProfile profile) => StartAsync(profile, null);
    internal Task StartAsync(ConnectionProfile profile, string? testPipe) => Task.Run(() =>
    {
        profile.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            var error = new byte[4096];
            if (sb_start(_handle, profile.Port, profile.Baud, (ushort)profile.TelnetPort, profile.TelnetEnabled ? 1 : 0, profile.Dtr ? 1 : 0,
                         profile.Rts ? 1 : 0, profile.LogRoot, testPipe, error, error.Length) == 0)
                throw new InvalidOperationException(Encoding.UTF8.GetString(error).TrimEnd('\0'));
        }
    });
    public Task StopAsync() => Task.Run(() => { lock (_gate) { if (_handle != IntPtr.Zero) sb_stop(_handle); } });
    public Snapshot GetSnapshot()
    {
        if (!Monitor.TryEnter(_gate)) return _last;
        try
        {
            if (_handle == IntPtr.Zero) return Snapshot.Empty;
            var buffer = new byte[65536];
            int size = sb_snapshot(_handle, buffer, buffer.Length);
            if (size > buffer.Length && size <= 2 * 1024 * 1024) { buffer = new byte[size]; size = sb_snapshot(_handle, buffer, buffer.Length); }
            if (size <= 0 || size > buffer.Length) throw new InvalidOperationException("原生状态快照无效");
            return _last = JsonSerializer.Deserialize<Snapshot>(buffer.AsSpan(0, size - 1)) ?? Snapshot.Empty;
        }
        finally { Monitor.Exit(_gate); }
    }
    public void SetPolicy(int policy) { lock (_gate) { if (_handle != IntPtr.Zero) sb_policy(_handle, policy); } }
    public void Disconnect(ulong id) { lock (_gate) { if (_handle != IntPtr.Zero) sb_disconnect(_handle, id); } }
    public void Dispose()
    {
        lock (_gate) { if (_handle == IntPtr.Zero) return; sb_destroy(_handle); _handle = IntPtr.Zero; }
        GC.KeepAlive(_notice); GC.KeepAlive(_data);
    }
}
