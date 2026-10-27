#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace OgsTsBridge
{
/// <summary>
/// Low-latency UDP receive socket for legacy game input.
///
/// Latency-relevant details:
/// - Blocking ReceiveFrom on the caller's thread: the kernel wakes the
///   thread the moment a datagram lands — no polling interval, no async
///   state machine, no thread-pool hop.
/// - SIO_UDP_CONNRESET disabled on Windows: without it, an ICMP
///   port-unreachable reply to the game's own send socket (or any stray
///   one on this socket) surfaces as WSAECONNRESET and can stall receives.
/// - SIO_LOOPBACK_FAST_PATH when bound to loopback (games sending to
///   127.0.0.1) — skips much of the loopback path on Windows.
/// - A 1 MiB receive buffer absorbs bursts during a GC or scheduler hiccup
///   so packets queue in the kernel instead of being dropped (a drop costs
///   a full source interval of latency, buffering costs microseconds).
/// </summary>
public sealed class UdpInputSocket : IDisposable
{
    const int SIO_UDP_CONNRESET = -1744830452;   // 0x9800000C
    const int SIO_LOOPBACK_FAST_PATH = -1744830448; // 0x98000010

    readonly Socket _socket;

    public UdpInputSocket(IPEndPoint local, int receiveBufferBytes = 1 << 20)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try { _socket.ReceiveBufferSize = receiveBufferBytes; }
        catch (SocketException) { /* some stacks cap it — not fatal */ }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                _socket.IOControl(SIO_UDP_CONNRESET, BitConverter.GetBytes(0), null);
            }
            catch (PlatformNotSupportedException) { /* older builds — ignore */ }
            if (IPAddress.IsLoopback(local.Address))
            {
                try
                {
                    _socket.IOControl(SIO_LOOPBACK_FAST_PATH,
                        BitConverter.GetBytes(1), null);
                }
                catch (PlatformNotSupportedException) { /* needs same option on peer; best effort */ }
            }
        }

        try
        {
            _socket.Bind(local);
        }
        catch (SocketException ex)
        {
            throw new InputPortException(local, ex);
        }

        // ReuseAddress lets several sockets co-own the port and Windows
        // silently routes unicast to one of them — a stale instance (or
        // another telemetry tool) can starve us with no error.
        // Surface any pre-existing owner instead of failing silently.
        try
        {
            var dup = System.Net.NetworkInformation.IPGlobalProperties
                .GetIPGlobalProperties().GetActiveUdpListeners()
                .Count(ep => ep.Port == local.Port);
            if (dup > 1) // us + someone else
                Console.Error.WriteLine(
                    $"warning: {dup - 1} other listener(s) already on UDP port {local.Port} — " +
                    $"they may be receiving the game's packets. Close them or use --in-port.");
        }
        catch { /* best-effort diagnostics */ }
    }

    /// <summary>UDP receive timeout: the forwarder wakes this often to
    /// keep the standard's 2 s discovery re-send alive during quiet input
    /// (game menus, paused sessions). Zero disables. The timeout costs
    /// nothing on the packet path — it only bounds how long a blocked
    /// receive sleeps when nothing arrives.</summary>
    public int ReceiveTimeoutMs
    {
        set => _socket.ReceiveTimeout = value;
    }

    /// <summary>Blocks until a datagram arrives (or the receive timeout
    /// expires — see ReceiveTimeoutMs). Returns its length, or 0 on
    /// timeout.</summary>
    public int Receive(byte[] buffer, ref EndPoint from)
    {
        try { return _socket.ReceiveFrom(buffer, ref from); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        _closed = true;
        try { _socket.Close(); } catch (SocketException) { }
        _socket.Dispose();
 }

    volatile bool _closed;
    /// <summary>Set by Dispose — lets the forwarder treat interrupted
    /// receives as shutdown instead of a fault.</summary>
    public bool Closed => _closed;
}
}
