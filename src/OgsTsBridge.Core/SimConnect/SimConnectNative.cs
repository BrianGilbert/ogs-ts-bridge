#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OgsTsBridge.SimConnect
{
/// <summary>
/// The SimConnect client C API (SimConnect.dll, x64), bound at runtime
/// through <see cref="NativeLibrary"/> so the path is chosen by
/// <see cref="SimConnectLocator"/>. Only the calls the Bridge needs.
/// Every call returns an HRESULT; 0 = S_OK.
/// </summary>
public sealed unsafe class SimConnectNative
{
    public const uint ObjectIdUser = 0;
    public const uint Unused = 0xFFFFFFFF;

    // SIMCONNECT_DATATYPE
    public const uint DataTypeFloat64 = 4;
    public const uint DataTypeString256 = 9;
    public const uint DataTypeXyz = 16;

    // SIMCONNECT_PERIOD
    public const uint PeriodNever = 0;
    public const uint PeriodSimFrame = 3;
    public const uint PeriodSecond = 4;

    // SIMCONNECT_DATA_REQUEST_FLAG
    public const uint FlagChanged = 1;

    // SIMCONNECT_RECV_ID
    public const uint RecvException = 1;
    public const uint RecvOpen = 2;
    public const uint RecvQuit = 3;
    public const uint RecvEvent = 4;
    public const uint RecvSimObjectData = 8;

    // SIMCONNECT_RECV header: dwSize, dwVersion, dwID
    public const int RecvIdOffset = 8;
    // SIMCONNECT_RECV_EXCEPTION: dwException, dwSendID, dwIndex
    public const int ExceptionCodeOffset = 12, ExceptionSendIdOffset = 16;
    // SIMCONNECT_RECV_EVENT: uGroupID, uEventID, dwData
    public const int EventIdOffset = 16, EventDataOffset = 20;
    // SIMCONNECT_RECV_SIMOBJECT_DATA: dwRequestID, dwObjectID, dwDefineID,
    // dwFlags, dwentrynumber, dwoutof, dwDefineCount, then the data
    public const int DataRequestIdOffset = 12, DataDefineCountOffset = 36, DataOffset = 40;

    readonly IntPtr _lib;
    readonly delegate* unmanaged[Stdcall]<IntPtr*, byte*, IntPtr, uint, IntPtr, uint, int> _open;
    readonly delegate* unmanaged[Stdcall]<IntPtr, int> _close;
    readonly delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, byte*, uint, float, uint, int> _addToDataDefinition;
    readonly delegate* unmanaged[Stdcall]<IntPtr, uint, int> _clearDataDefinition;
    readonly delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, uint, uint, uint, int> _requestDataOnSimObject;
    readonly delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, int> _subscribeToSystemEvent;
    readonly delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, int> _getNextDispatch;
    readonly delegate* unmanaged[Stdcall]<IntPtr, uint*, int> _getLastSentPacketId;

    public string LibraryPath { get; }

    SimConnectNative(IntPtr lib, string path)
    {
        _lib = lib;
        LibraryPath = path;
        _open = (delegate* unmanaged[Stdcall]<IntPtr*, byte*, IntPtr, uint, IntPtr, uint, int>)Export("SimConnect_Open");
        _close = (delegate* unmanaged[Stdcall]<IntPtr, int>)Export("SimConnect_Close");
        _addToDataDefinition = (delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, byte*, uint, float, uint, int>)Export("SimConnect_AddToDataDefinition");
        _clearDataDefinition = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Export("SimConnect_ClearDataDefinition");
        _requestDataOnSimObject = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, uint, uint, uint, int>)Export("SimConnect_RequestDataOnSimObject");
        _subscribeToSystemEvent = (delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, int>)Export("SimConnect_SubscribeToSystemEvent");
        _getNextDispatch = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, int>)Export("SimConnect_GetNextDispatch");
        _getLastSentPacketId = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Export("SimConnect_GetLastSentPacketID");
    }

    IntPtr Export(string name) => NativeLibrary.GetExport(_lib, name);

    /// <summary>Loads the library at <paramref name="path"/>; throws when
    /// it is missing or lacks the client API.</summary>
    public static SimConnectNative Load(string path) => new(NativeLibrary.Load(path), path);

    static byte[] Ansi(string s) => Encoding.ASCII.GetBytes(s + "\0");

    public int Open(out IntPtr handle, string clientName, IntPtr eventHandle)
    {
        var name = Ansi(clientName);
        IntPtr h = IntPtr.Zero;
        int hr;
        fixed (byte* n = name) hr = _open(&h, n, IntPtr.Zero, 0, eventHandle, 0);
        handle = h;
        return hr;
    }

    public int Close(IntPtr handle) => _close(handle);

    public int AddToDataDefinition(IntPtr handle, uint defineId, string datum, string units, uint dataType)
    {
        var d = Ansi(datum);
        var u = Ansi(units);
        fixed (byte* dp = d)
        fixed (byte* up = u)
            return _addToDataDefinition(handle, defineId, dp, dataType == DataTypeFloat64 || dataType == DataTypeXyz ? up : null,
                                        dataType, 0f, Unused);
    }

    public int ClearDataDefinition(IntPtr handle, uint defineId) => _clearDataDefinition(handle, defineId);

    public int RequestDataOnSimObject(IntPtr handle, uint requestId, uint defineId, uint period, uint flags) =>
        _requestDataOnSimObject(handle, requestId, defineId, ObjectIdUser, period, flags, 0, 0, 0);

    public int SubscribeToSystemEvent(IntPtr handle, uint eventId, string name)
    {
        var n = Ansi(name);
        fixed (byte* p = n) return _subscribeToSystemEvent(handle, eventId, p);
    }

    /// <summary>Next queued message, or false when the queue is empty.</summary>
    public bool GetNextDispatch(IntPtr handle, out IntPtr data, out uint size)
    {
        IntPtr d = IntPtr.Zero;
        uint s = 0;
        int hr = _getNextDispatch(handle, &d, &s);
        data = d; size = s;
        return hr == 0 && d != IntPtr.Zero;
    }

    public uint GetLastSentPacketId(IntPtr handle)
    {
        uint id = 0;
        _getLastSentPacketId(handle, &id);
        return id;
    }
}
}
