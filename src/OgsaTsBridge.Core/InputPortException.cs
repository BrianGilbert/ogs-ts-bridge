#nullable enable
using System;
using System.Net;
using System.Net.Sockets;

namespace OgsaTsBridge
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
            // 10013: either a Windows reserved/excluded range (Hyper-V,
            // WSL, WinNAT — the ranges move on reboot) or an exclusive
            // owner without SO_REUSEADDR
            reserved = $@"
This is Windows refusing the bind, most commonly:
  1. The port sits in a reserved range (Hyper-V/WSL/WinNAT — reshuffled on reboot):
       netsh interface ipv4 show excludedportrange protocol=udp
     Fix: choose a free port with --in-port {local.Port + 1} and point the
     game's telemetry output at it, or as admin stop the reservation source:
       net stop winnat   (re-start with 'net start winnat' afterwards)
  2. Another process holds the port exclusively:
       Get-NetUDPEndpoint -LocalPort {local.Port}
       Get-Process -Id (Get-NetUDPEndpoint -LocalPort {local.Port}).OwningProcess
";
        }
        return $"cannot bind the game-input socket {local}: {cause.Message}{reserved}";
    }
}
}
