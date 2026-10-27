#nullable enable
using System;
using System.Net;
using System.Net.Sockets;

namespace OgsTsBridge
{
/// <summary>Bind failure on the game-input port, with the diagnostics a
/// user needs to resolve it (reserved port ranges, exclusive owners).</summary>
public sealed class InputPortException : Exception
{
    public InputPortException(IPEndPoint local, SocketException cause)
        : base(BuildMessage(local, cause), cause)
    {
    }

    static string BuildMessage(IPEndPoint local, SocketException cause)
    {
        string reserved = "";
        if (cause.SocketErrorCode == SocketError.AccessDenied)
        {
            var owner = FindExclusiveOwner(local.Port);
            var named = owner is null
                ? $"       Get-NetUDPEndpoint -LocalPort {local.Port}"
                : $"       it is held by: {owner} (Get-NetUDPEndpoint -LocalPort {local.Port})";
            // 10013: either a Windows reserved/excluded range (Hyper-V,
            // WSL, WinNAT — the ranges move on reboot) or an exclusive
            // owner without SO_REUSEADDR
            reserved = $@"
This is Windows refusing the bind, most commonly:
  1. Another process holds the port exclusively — close it (or its game
     plugin) and this app will pick the game up on its own:
{named}
  2. The port sits in a reserved range (Hyper-V/WSL/WinNAT — reshuffled on reboot):
       netsh interface ipv4 show excludedportrange protocol=udp
     Fix: choose a free port with --in-port {local.Port + 1} and point the
     game's telemetry output at it, or as admin stop the reservation source:
       net stop winnat   (re-start with 'net start winnat' afterwards)
";
        }
        return $"cannot bind the game-input socket {local}: {cause.Message}{reserved}";
    }

    /// <summary>Names the exclusive owner of the port, if any (netstat -ano
    /// parse + process lookup). Best-effort: null when nothing found.</summary>
    static string? FindExclusiveOwner(int port)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netstat", "-ano -p UDP")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var lines = p.StandardOutput.ReadToEnd().Split('\n');
            foreach (var line in lines)
            {
                // UDP    0.0.0.0:20777    *:*    1234
                var parts = System.Text.RegularExpressions.Regex.Split(line.Trim(), @"\s+");
                if (parts.Length >= 4 && parts[0].Equals("UDP", StringComparison.OrdinalIgnoreCase))
                {
                    var ep = parts[1];
                    var colon = ep.LastIndexOf(':');
                    if (colon > 0 && ep[(colon + 1)..] == port.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        && int.TryParse(parts[^1], out var pid))
                    {
                        try
                        {
                            var proc = System.Diagnostics.Process.GetProcessById(pid);
                            return $"{proc.ProcessName} (PID {pid})";
                        }
                        catch { return $"PID {pid}"; }
                    }
                }
            }
        }
        catch { /* diagnostics only */ }
        return null;
    }
}
}
