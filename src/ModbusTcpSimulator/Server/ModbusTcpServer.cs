using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ModbusTcpSimulator.Protocol;

namespace ModbusTcpSimulator.Server;

/// <summary>
/// Modbus TCP server. Each frame is a 7 byte MBAP header followed by the PDU:
/// transaction id (2), protocol id (2, always 0), length (2, unit id + PDU), unit id (1).
/// Handles many clients at once; requests from one client are answered in order.
/// </summary>
public sealed class ModbusTcpServer : IAsyncDisposable
{
    private const int MbapHeaderLength = 7;

    private readonly TcpListener _listener;
    private readonly ModbusRequestHandler _handler;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private Task? _acceptLoop;
    private int _nextClientId;
    private long _requestCount;

    public ModbusTcpServer(IPAddress address, int port, ModbusRequestHandler handler, Action<string>? log = null)
    {
        _listener = new TcpListener(address, port);
        _handler = handler;
        _log = log;
    }

    /// <summary>When true, every request and response is logged as hex.</summary>
    public bool Verbose { get; set; }

    /// <summary>The bound port. Useful when started with port 0.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int ClientCount => _clients.Count;

    public long RequestCount => Interlocked.Read(ref _requestCount);

    public IEnumerable<string> ClientEndpoints =>
        _clients.Values.Select(c => c.Client.RemoteEndPoint?.ToString() ?? "?");

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                _log?.Invoke($"Accept failed: {ex.Message}");
                continue;
            }

            client.NoDelay = true;
            var id = Interlocked.Increment(ref _nextClientId);
            _clients[id] = client;
            _ = HandleClientAsync(id, client, ct);
        }
    }

    private async Task HandleClientAsync(int id, TcpClient client, CancellationToken ct)
    {
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
        _log?.Invoke($"Client connected: {endpoint}");

        var header = new byte[MbapHeaderLength];
        var pdu = new byte[Limits.MaxPduLength];

        try
        {
            await using var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, ct);

                var transactionId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0));
                var protocolId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                var unitId = header[6];

                // Length covers the unit id plus a PDU of at least one byte.
                if (protocolId != 0 || length < 2 || length > Limits.MaxPduLength + 1)
                {
                    _log?.Invoke($"{endpoint}: invalid MBAP header (protocol {protocolId}, length {length}), closing");
                    break;
                }

                var pduLength = length - 1;
                await stream.ReadExactlyAsync(pdu.AsMemory(0, pduLength), ct);
                Interlocked.Increment(ref _requestCount);

                var response = _handler.Handle(pdu.AsSpan(0, pduLength));

                if (Verbose)
                {
                    _log?.Invoke($"{endpoint} unit {unitId} tx {transactionId}: {Convert.ToHexString(pdu, 0, pduLength)} -> {Convert.ToHexString(response)}");
                }

                var frame = new byte[MbapHeaderLength + response.Length];
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), transactionId);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(response.Length + 1));
                frame[6] = unitId;
                response.CopyTo(frame, MbapHeaderLength);

                await stream.WriteAsync(frame, ct);
            }
        }
        catch (EndOfStreamException) { /* client closed */ }
        catch (OperationCanceledException) { /* server stopping */ }
        catch (IOException) { /* connection reset */ }
        catch (Exception ex)
        {
            _log?.Invoke($"{endpoint}: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Dispose();
            _log?.Invoke($"Client disconnected: {endpoint}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { /* already stopping */ }
        }
        _cts.Dispose();
    }
}
