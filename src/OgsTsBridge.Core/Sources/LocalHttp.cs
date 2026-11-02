#nullable enable
using System;
using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Minimal HTTP/1.1 GET client for a game's local telemetry server: one
/// keep-alive TCP connection, prebuilt requests, one receive buffer, so a
/// poll costs a send and a receive and no allocation. Handles
/// Content-Length, chunked and read-to-close bodies; a server that closes
/// the connection is reconnected on the next request (cold).
/// </summary>
internal sealed class LocalHttp : IDisposable
{
    readonly IPEndPoint _endpoint;
    readonly byte[] _rx;
    Socket? _socket;

    public LocalHttp(IPEndPoint endpoint, int bufferBytes = 1 << 17)
    {
        _endpoint = endpoint;
        _rx = new byte[bufferBytes];
    }

    public static byte[] Request(string path, IPEndPoint host) => Encoding.ASCII.GetBytes(
        $"GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: keep-alive\r\nAccept: application/json\r\n\r\n");

    public bool Connect()
    {
        Close();
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            ReceiveTimeout = 1000,
            SendTimeout = 1000,
        };
        try { s.Connect(_endpoint); }
        catch (SocketException) { s.Dispose(); return false; }
        _socket = s;
        return true;
    }

    public void Close()
    {
        _socket?.Dispose();
        _socket = null;
    }

    public void Dispose() => Close();

    /// <summary>Sends <paramref name="request"/> and copies the response
    /// body into <paramref name="body"/>; returns its length, or −1 when
    /// the server is gone (connection refused, reset, timed out).</summary>
    public int Get(byte[] request, Span<byte> body)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (_socket is null && !Connect()) return -1;
            int n = TryGet(request, body, out bool reconnect);
            if (n >= 0 && !reconnect) return n;
            Close();
            if (n >= 0) return n; // body complete, server closed after it
        }
        return -1;
    }

    int TryGet(byte[] request, Span<byte> body, out bool closeAfter)
    {
        closeAfter = false;
        var s = _socket!;
        try { s.Send(request); }
        catch (SocketException) { return -1; }

        int have = 0, headerEnd = -1;
        while (headerEnd < 0)
        {
            int got = Receive(s, have);
            if (got <= 0) return -1;
            have += got;
            headerEnd = _rx.AsSpan(0, have).IndexOf("\r\n\r\n"u8);
        }
        var headers = _rx.AsSpan(0, headerEnd);
        int bodyStart = headerEnd + 4;
        closeAfter = Contains(headers, "connection: close"u8);
        bool chunked = Contains(headers, "transfer-encoding: chunked"u8);
        long length = ContentLength(headers);

        if (length >= 0)
        {
            while (have - bodyStart < length)
            {
                int got = Receive(s, have);
                if (got <= 0) return -1;
                have += got;
            }
            return Copy(_rx.AsSpan(bodyStart, (int)length), body);
        }
        if (chunked)
        {
            // read until the terminating 0-size chunk, then de-chunk in place
            while (_rx.AsSpan(bodyStart, have - bodyStart).IndexOf("\r\n0\r\n\r\n"u8) < 0
                   && !_rx.AsSpan(bodyStart, have - bodyStart).StartsWith("0\r\n\r\n"u8))
            {
                int got = Receive(s, have);
                if (got <= 0) return -1;
                have += got;
            }
            return Dechunk(_rx.AsSpan(bodyStart, have - bodyStart), body);
        }
        // no length: the body runs to the close
        closeAfter = true;
        while (true)
        {
            int got = Receive(s, have);
            if (got < 0) return -1;
            if (got == 0) break;
            have += got;
        }
        return Copy(_rx.AsSpan(bodyStart, have - bodyStart), body);
    }

    int Receive(Socket s, int at)
    {
        if (at >= _rx.Length) return -1; // response larger than the buffer
        try { return s.Receive(_rx, at, _rx.Length - at, SocketFlags.None); }
        catch (SocketException) { return -1; }
    }

    static int Copy(ReadOnlySpan<byte> from, Span<byte> to)
    {
        int n = Math.Min(from.Length, to.Length);
        from[..n].CopyTo(to);
        return n;
    }

    static int Dechunk(ReadOnlySpan<byte> chunked, Span<byte> to)
    {
        int written = 0;
        while (true)
        {
            int eol = chunked.IndexOf("\r\n"u8);
            if (eol < 0) return written;
            if (!Utf8Parser.TryParse(chunked[..eol], out int size, out _, 'x') || size == 0) return written;
            chunked = chunked[(eol + 2)..];
            if (size > chunked.Length) size = chunked.Length;
            int n = Math.Min(size, to.Length - written);
            chunked[..n].CopyTo(to[written..]);
            written += n;
            chunked = chunked[Math.Min(chunked.Length, size + 2)..];
        }
    }

    static bool Contains(ReadOnlySpan<byte> headers, ReadOnlySpan<byte> lowerNeedle)
    {
        for (int i = 0; i + lowerNeedle.Length <= headers.Length; i++)
        {
            int j = 0;
            while (j < lowerNeedle.Length && Lower(headers[i + j]) == lowerNeedle[j]) j++;
            if (j == lowerNeedle.Length) return true;
        }
        return false;
    }

    static long ContentLength(ReadOnlySpan<byte> headers)
    {
        var key = "content-length:"u8;
        for (int i = 0; i + key.Length <= headers.Length; i++)
        {
            int j = 0;
            while (j < key.Length && Lower(headers[i + j]) == key[j]) j++;
            if (j != key.Length) continue;
            var rest = headers[(i + key.Length)..].TrimStart((byte)' ');
            return Utf8Parser.TryParse(rest, out long n, out _) ? n : -1;
        }
        return -1;
    }

    static byte Lower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
}
}
