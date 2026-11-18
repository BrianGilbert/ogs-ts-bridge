#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OgsTsBridge.Sources
{
/// <summary>
/// Read-only access to another process's memory through pointer chains,
/// shared by the sources whose game has no telemetry output (Elite,
/// Squadrons). The process is opened with PROCESS_VM_READ and
/// PROCESS_QUERY_LIMITED_INFORMATION only.
///
/// A chain resolves as: p = *(base + Offset_0), then p = *(p + Offset_i)
/// for the middle offsets, and the value is the float at p + last offset
/// (base = the main module).
/// </summary>
internal sealed class ProcessMemory
{
    IntPtr _process;
    long _base;

    public bool IsOpen => _process != IntPtr.Zero;

    /// <summary>Opens the first running process matching one of the names;
    /// false when none is up (or access is denied).</summary>
    public bool Open(params string[] processNames)
    {
        Close();
        foreach (var name in processNames)
        {
            var procs = Process.GetProcessesByName(name);
            try
            {
                if (procs.Length == 0) continue;
                var p = procs[0];
                _base = p.MainModule?.BaseAddress.ToInt64() ?? 0;
                if (_base == 0) continue;
                _process = OpenProcess(ProcessVmRead | ProcessQueryLimitedInformation, false, p.Id);
                if (_process != IntPtr.Zero) return true;
            }
            catch (System.ComponentModel.Win32Exception) { }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        return false;
    }

    public void Close()
    {
        if (_process != IntPtr.Zero) CloseHandle(_process);
        _process = IntPtr.Zero;
    }

    /// <summary>False once the process has exited (or was never opened).</summary>
    public bool IsAlive() =>
        _process != IntPtr.Zero && GetExitCodeProcess(_process, out uint code) && code == StillActive;

    /// <summary>The float at the end of the chain, or NaN when any link
    /// does not resolve right now.</summary>
    public unsafe float Resolve(long[] offsets)
    {
        long p;
        if (!ReadProcessMemory(_process, new IntPtr(_base + offsets[0]), &p, 8, out _)) return float.NaN;
        for (int i = 1; i < offsets.Length - 1; i++)
        {
            if (p == 0) return float.NaN;
            if (!ReadProcessMemory(_process, new IntPtr(p + offsets[i]), &p, 8, out _)) return float.NaN;
        }
        if (p == 0) return float.NaN;
        float v;
        return ReadProcessMemory(_process, new IntPtr(p + offsets[^1]), &v, 4, out _) ? v : float.NaN;
    }

    const uint ProcessVmRead = 0x0010, ProcessQueryLimitedInformation = 0x1000, StillActive = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr process, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern unsafe bool ReadProcessMemory(IntPtr process, IntPtr address, void* buffer, nint size, out nint read);
}
}
