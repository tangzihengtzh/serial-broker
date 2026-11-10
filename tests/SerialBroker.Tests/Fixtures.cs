using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SerialBroker.Gui;

namespace SerialBroker.Tests;

internal sealed class StartupFailureFixture(IStartupRegistration inner) : IStartupRegistration
{
    public bool FailRead { get; set; }
    public bool FailWrite { get; set; }
    public bool IsEnabled => FailRead ? throw new UnauthorizedAccessException("Test registry read denied") : inner.IsEnabled;
    public void SetEnabled(bool enabled)
    {
        if (FailWrite) throw new UnauthorizedAccessException("Test registry write denied");
        inner.SetEnabled(enabled);
    }
}

// 所有测试设备都是当前进程创建的命名管道；没有COM驱动调用或真实开发板。
internal sealed class Device : IAsyncDisposable
{
    private readonly NamedPipeServerStream _server;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _receiver;
    private readonly List<byte> _received = [];
    private readonly object _gate = new();
    private bool _disposed;
    public string Name { get; } = "SerialBroker.Test." + Guid.NewGuid().ToString("N");
    public string Path => @"\\.\pipe\" + Name;
    public Device()
    {
        _server = new(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _receiver = Receive();
    }
    private async Task Receive()
    {
        try
        {
            await _server.WaitForConnectionAsync(_cancel.Token);
            var buffer = new byte[4096];
            while (!_cancel.IsCancellationRequested)
            {
                int n = await _server.ReadAsync(buffer, _cancel.Token); if (n == 0) break;
                lock (_gate) _received.AddRange(buffer.AsSpan(0, n).ToArray());
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }
    public async Task Send(string text) { await _server.WriteAsync(Encoding.UTF8.GetBytes(text)); await _server.FlushAsync(); }
    public string Received { get { lock (_gate) return Encoding.UTF8.GetString(_received.ToArray()); } }
    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; _cancel.Cancel(); _server.Dispose(); await _receiver; _cancel.Dispose(); }
}

internal sealed class Peer(string name) : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
    public async Task Connect()
    {
        await _pipe.ConnectAsync(3000);
        await Send(1, Encoding.UTF8.GetBytes("functional-test"));
    }
    public async Task Send(ushort type, byte[]? body = null)
    {
        body ??= [];
        byte[] header = new byte[8]; header[0] = 0x53; header[1] = 0x42;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)body.Length);
        await _pipe.WriteAsync(header); await _pipe.WriteAsync(body); await _pipe.FlushAsync();
    }
    public async Task<byte[]> Until(ushort expected)
    {
        using var timeout = new CancellationTokenSource(5000);
        for (int i = 0; i < 100; i++)
        {
            byte[] header = new byte[8]; await _pipe.ReadExactlyAsync(header, timeout.Token);
            if (header[0] != 0x53 || header[1] != 0x42) throw new InvalidDataException("Bad SB frame");
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (size > 65536) throw new InvalidDataException("Oversized frame");
            byte[] body = new byte[size]; await _pipe.ReadExactlyAsync(body, timeout.Token);
            if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)) == expected) return body;
        }
        throw new InvalidDataException("Expected frame not received");
    }
    public async Task Transmit(string text)
    {
        byte[] body = new byte[4 + Encoding.UTF8.GetByteCount(text)];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 1); Encoding.UTF8.GetBytes(text, body.AsSpan(4));
        await Send(3, body); await Until(8);
    }
    public ValueTask DisposeAsync() => _pipe.DisposeAsync();
}

internal static class NetworkFixture
{
    private static readonly HashSet<int> Ports = [];
    public static int FreePort()
    {
        while (true)
        {
            var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
            int port = ((IPEndPoint)server.LocalEndpoint).Port; server.Stop();
            if (Ports.Add(port)) return port;
        }
    }
    public static async Task<string> ReadUntil(TcpClient client, string marker)
    {
        using var timeout = new CancellationTokenSource(5000);
        var bytes = new List<byte>(); var buffer = new byte[4096];
        while (true)
        {
            int size = await client.GetStream().ReadAsync(buffer, timeout.Token);
            if (size == 0) throw new IOException("TCP client closed");
            bytes.AddRange(buffer.AsSpan(0, size).ToArray());
            string text = Encoding.UTF8.GetString(bytes.ToArray());
            if (text.Contains(marker, StringComparison.Ordinal)) return text;
        }
    }
}
