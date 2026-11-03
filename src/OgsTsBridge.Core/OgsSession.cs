#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Ogs.Telemetry;
using Ogs.Telemetry.Wire;
using Schema = Ogs.Telemetry.Schema;

namespace OgsTsBridge
{

/// <summary>Handle to a declared channel; pass to SetChannel on the hot path.</summary>
public readonly struct OgsChannel
{
    internal OgsChannel(int id, string name) { Id = id; Name = name; }
    public int Id { get; }
    public string Name { get; }
}

/// <summary>
/// Latency-first OGS-TS writer: declaration phase builds the DiscoveryFrame
/// once; every tick reuses cached schema-writer graphs, rewrites the
/// values in place, encodes them (canonical OGS encoding) and hands it to
/// the transport immediately — one datagram per input packet, no timers, no
/// queues, no cross-thread hops. The caller owns the thread (the legacy
/// receive thread) and the clock (a monotonic seconds value).
///
/// Output cadence equals input cadence: motion frames are sent on every
/// Tick up to <c>maxHz</c> (default 1000 — effectively pass-through; the
/// cap only protects against a misbehaving source flooding the group).
/// Discovery rides the same thread, re-sent every 2 s per
/// implementation-udp.md.
///
/// Datagram budget (implementation-udp.md § Datagram budget): every
/// datagram is at most 1,400 bytes. Discovery is cached as pages
/// (DiscoveryPager) — all of them at Start, on a redeclare and on an
/// identity or environment change, otherwise spread across each re-send
/// interval, so no tick pays for the whole set; a session-text change
/// re-sends only the page carrying the values. A tick with more channel
/// values than one datagram holds goes out as back-to-back parts with the
/// pose in part 0 (reused part graphs: no per-tick allocation once warm).
/// Changed-only sessions refresh every value within each interval,
/// spread across ticks. The generation increments on a redeclare and on a
/// type-label or location change once discovery has gone out.
/// </summary>
public sealed class OgsSession : IDisposable
{
    enum Dyn { None, Kinematics, BodyDynamics }

    /// <summary>Channel budget per session.</summary>
    public const int MaxChannels = 512;
    const int MaxDrivePoints = 32;

    readonly TelemetrySender? _sender;
    readonly Action<FrameType, byte[]>? _sink;   // test capture path
    readonly string _gameName;
    readonly double _minMotionInterval;
    readonly double _discoveryInterval;
    readonly long _sessionStartTicks = DateTimeOffset.UtcNow.UtcTicks;

    // declaration state (locked at Start)
    readonly List<Schema.ChannelDescriptor> _descriptors = new();
    readonly List<Schema.DrivePointDescriptor> _pointDescriptors = new();
    readonly List<Schema.NamedPoint> _namedPoints = new();
    string _objectName = "player";
    Schema.ObjectType _objectType = Schema.ObjectType.Vehicle;
    string _typeLabel = "";
    string _location = "local";
    byte[] _typeLabelBytes = Array.Empty<byte>(), _locationBytes = Array.Empty<byte>();
    float _gravity = 9.81f; // the g the sources scale accelerometer values with
    bool _locked;

    // discovery environment (sent with each discovery)
    float _airDensity, _temperature, _pressure; // declared once SetEnvironment is called
    bool _environmentSet, _environmentPending;

    // per-tick state (receive thread only)
    Vector3 _position; bool _hasPosition;
    Quaternion _orientation = Quaternion.Identity;
    Dyn _dyn;
    Vector3 _velocity, _acceleration, _specificForce, _angularVelocity;

    readonly double[] _pushed = new double[MaxChannels];
    readonly bool[] _boolValue = new bool[MaxChannels];
    readonly bool[] _boolSet = new bool[MaxChannels];    // reset each tick; defaults false
    readonly bool[] _isBool = new bool[MaxChannels];
    int _channelCount;

    // session-scoped text channels: carried in discovery `values`, never in
    // motion frames; a change (detected on the raw bytes, so unchanged text
    // costs no allocation) re-sends discovery on the next Tick
    readonly bool[] _isSession = new bool[MaxChannels];
    readonly Schema.TextValue?[] _sessionValue = new Schema.TextValue?[MaxChannels];
    readonly byte[]?[] _sessionBytes = new byte[]?[MaxChannels];
    readonly List<Schema.TextValue> _sessionValues = new(); // session text (discovery `texts`)

    // changed-only channel mode: a channel rides the motion frame when its
    // value differs from the last one sent, when it is marked always-sent,
    // or when the bounded refresh reaches it (every value once per
    // discovery interval, spread across ticks); a channel never set (its
    // source variable unavailable) is never sent
    bool _changedOnly;
    readonly bool[] _valueSet = new bool[MaxChannels];
    readonly bool[] _alwaysSend = new bool[MaxChannels];
    readonly bool[] _everSent = new bool[MaxChannels];
    readonly float[] _sentNumber = new float[MaxChannels];
    readonly bool[] _sentBool = new bool[MaxChannels];
    readonly List<Schema.ChannelValue> _changedValues = new(); // reused list
    int _refreshCursor;                        // next channel id of the refresh cycle
    double _refreshCycleStart = double.NaN;    // when the current cycle began
    readonly bool[] _addedThisTick = new bool[MaxChannels];

    // drive point state cache (one reused drive point each) + per-tick dirty list
    readonly List<Schema.DrivePoint> _points = new();
    readonly bool[] _pointDirty = new bool[MaxDrivePoints];
    readonly List<Schema.DrivePoint> _drivePoints = new(); // reused list

    // cached wire graphs
    Schema.DiscoveryFrame? _discoveryFrame;    // the complete set (unpaged view)
    List<Schema.DiscoveryFrame>? _pages;       // what is sent: one page or many
    readonly List<int> _valuePages = new();    // pages carrying session values
    bool _sendAllPages = true, _sendValuePages;
    int _nextPage;                             // next page of the current re-send cycle
    uint _generation;
    bool _discoverySent;
    Schema.MotionFrame? _motionFrame;          // part 0
    readonly List<Schema.MotionFrame> _parts = new();              // part 0, then reused later parts
    readonly List<List<Schema.ChannelValue>> _partValues = new();  // each part's value list
    int _partCount = 1, _part0Capacity, _partCapacity;
    List<Schema.ChannelValue>? _motionValues;
    readonly Schema.ChannelValue?[] _motionById = new Schema.ChannelValue?[MaxChannels];

    double _anchor = double.NaN;       // host clock value at Start
    double _lastDiscovery = double.NegativeInfinity;
    double _lastMotion = double.NegativeInfinity;

    public OgsSession(TelemetrySender sender, string gameName,
                       double maxHz = 1000.0, double discoveryIntervalSeconds = 2.0)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _gameName = gameName;
        _minMotionInterval = maxHz > 0 ? 1.0 / maxHz : 0.0;
        _discoveryInterval = discoveryIntervalSeconds;
    }

    /// <summary>Test/in-process constructor: datagrams are handed to
    /// <paramref name="sink"/> instead of a socket.</summary>
    public OgsSession(Action<FrameType, byte[]> sink, string gameName,
                       double maxHz = 1000.0, double discoveryIntervalSeconds = 2.0)
        : this(new TelemetrySender(), gameName, maxHz, discoveryIntervalSeconds)
        => _sink = sink;

    // ---- declaration phase (before Start) ----

    string _instanceLabel = "";
    string _host = LocalHostName();

    /// <summary>Names this Bridge instance in receivers' session pickers
    /// (implementation-udp.md § Senders and sessions): a user-set label
    /// such as "Rig 2", and the host name (null = this machine's, the
    /// default).</summary>
    public void SetInstance(string instanceLabel, string? host = null)
    {
        LockCheck("SetInstance");
        _instanceLabel = instanceLabel ?? "";
        _host = host ?? LocalHostName();
    }

    static string LocalHostName()
    {
        try { return System.Net.Dns.GetHostName() ?? ""; }
        catch (Exception) { return ""; }
    }

    public void DeclareObject(string name, Schema.ObjectType type, string typeLabel = "")
    {
        LockCheck("DeclareObject");
        _objectName = name; _objectType = type;
        // a label set at runtime (SetTypeLabel) survives a redeclare: the
        // vehicle did not change, only the declarations did
        if (_typeLabelBytes.Length == 0) _typeLabel = typeLabel;
    }

    /// <summary>Declares the gravity magnitude (m/s², along world −Y) the
    /// source's specific force reacts to, so receivers that remove the
    /// gravity reaction use the same value (standard CONVENTIONS, Gravity).
    /// Defaults to 9.81, the factor the g-scaled sources use.</summary>
    public void DeclareGravity(float gravity)
    {
        LockCheck("DeclareGravity");
        _gravity = gravity;
    }

    /// <summary>Motion frames carry only channels whose value changed since
    /// they were last sent (the schema's "absence means unchanged"), plus a
    /// full refresh after every discovery send so late joiners converge
    /// within the discovery interval. For sources with large channel sets.</summary>
    public void SendChangedChannelsOnly()
    {
        LockCheck("SendChangedChannelsOnly");
        _changedOnly = true;
    }

    /// <summary>In changed-only mode, carries this channel in every motion
    /// frame regardless (e.g. <c>session.paused</c>, CONVENTIONS C-11).</summary>
    public void AlwaysSend(OgsChannel channel)
    {
        LockCheck("AlwaysSend");
        _alwaysSend[channel.Id] = true;
    }

    public OgsChannel AddChannel(string name, string unit, string description = "",
                                  double min = double.NaN, double max = double.NaN)
    {
        LockCheck("AddChannel");
        var h = NewChannel(boolean: false);
        var descriptor = new Schema.ChannelDescriptor
        {
            Id = (ushort)h.Id,
            Name = name,
            Unit = unit,
            Description = description,
            Type = Schema.ChannelType.Number,
        };
        if (!double.IsNaN(min) && !double.IsNaN(max))
        {
            var range = descriptor.InitRange();
            range.Min = min; range.Max = max;
        }
        _descriptors.Add(descriptor);
        return h;
    }

    public OgsChannel AddBooleanChannel(string name, string description = "")
    {
        LockCheck("AddBooleanChannel");
        var h = NewChannel(boolean: true);
        _descriptors.Add(new Schema.ChannelDescriptor
        {
            Id = (ushort)h.Id,
            Name = name,
            Unit = "",
            Description = description,
            Type = Schema.ChannelType.Boolean,
        });
        return h;
    }

    /// <summary>Declares a session-scoped text channel (track, vehicle,
    /// compound names): its value rides discovery `values` and is never
    /// sent in motion frames. Set it with <see cref="SetSessionText(OgsChannel, ReadOnlySpan{byte})"/>.</summary>
    public OgsChannel AddSessionText(string name, string description = "")
    {
        LockCheck("AddSessionText");
        var h = NewChannel(boolean: false);
        _isSession[h.Id] = true;
        _descriptors.Add(new Schema.ChannelDescriptor
        {
            Id = (ushort)h.Id,
            Name = name,
            Unit = "",
            Description = description,
            Type = Schema.ChannelType.Text,
        });
        var value = new Schema.TextValue { Id = (ushort)h.Id, Text = "" };
        _sessionValue[h.Id] = value;
        _sessionValues.Add(value);
        return h;
    }

    /// <summary>Declares a named point (meters, object-local frame — the
    /// same frame as the drive-point offsets).</summary>
    public void AddNamedPoint(string name, Vector3 position)
    {
        LockCheck("AddNamedPoint");
        var point = new Schema.NamedPoint { Name = name };
        point.Position.X = position.X; point.Position.Y = position.Y; point.Position.Z = position.Z;
        _namedPoints.Add(point);
    }

    /// <summary>Declares a wheel drive point. Offsets are metres in the
    /// object-local frame (+x right, +y up, +z forward from the CG).
    /// <paramref name="suspension"/> false declares no suspension spec
    /// (for sources that do not know the geometry).</summary>
    public int AddWheel(string name, Vector3 offset, float radius,
                        bool driven, bool steered,
                        float travelMin = -0.05f, float travelMax = 0.15f,
                        bool suspension = true)
    {
        LockCheck("AddWheel");
        var descriptor = NewPointDescriptor(name, offset);
        var spec = descriptor.InitWheel();
        spec.Radius = radius; spec.Driven = driven; spec.Steered = steered;
        if (suspension)
        {
            var s = spec.InitSuspension();
            s.Travel.Min = travelMin; s.Travel.Max = travelMax;
            s.Stiffness = 65000f; s.Damping = 5000f;
        }

        var point = new Schema.DrivePoint { Id = descriptor.Id };
        point.InitWheel().Contact = Schema.ContactMedium.Asphalt;
        _points.Add(point);
        return descriptor.Id;
    }

    /// <summary>Declares a propeller (or rotor) drive point;
    /// <paramref name="diameter"/> 0 = unknown.</summary>
    public int AddPropeller(string name, Vector3 offset, float diameter = 0f, ushort blades = 0)
    {
        LockCheck("AddPropeller");
        var descriptor = NewPointDescriptor(name, offset);
        var spec = descriptor.InitPropeller();
        spec.Diameter = diameter; spec.Blades = blades;

        var point = new Schema.DrivePoint { Id = descriptor.Id };
        point.InitPropeller();
        _points.Add(point);
        return descriptor.Id;
    }

    /// <summary>Declares a jet drive point.</summary>
    public int AddJet(string name, Vector3 offset)
    {
        LockCheck("AddJet");
        var descriptor = NewPointDescriptor(name, offset);
        descriptor.SetJet();

        var point = new Schema.DrivePoint { Id = descriptor.Id };
        point.InitJet();
        _points.Add(point);
        return descriptor.Id;
    }

    Schema.DrivePointDescriptor NewPointDescriptor(string name, Vector3 offset)
    {
        if (_pointDescriptors.Count >= MaxDrivePoints)
            throw new NotSupportedException("drive point budget exceeded for this build");
        var descriptor = new Schema.DrivePointDescriptor
        {
            Id = (ushort)_pointDescriptors.Count,
            Name = name,
            Offset = { X = offset.X, Y = offset.Y, Z = offset.Z },
        };
        _pointDescriptors.Add(descriptor);
        return descriptor;
    }

    OgsChannel NewChannel(bool boolean)
    {
        if (_channelCount >= MaxChannels)
            throw new NotSupportedException("channel budget exceeded for this build");
        int id = _channelCount++;
        _isBool[id] = boolean;
        _boolValue[id] = false;
        return new OgsChannel(id, _descriptors.Count.ToString());
    }

    void LockCheck(string what)
    {
        if (_locked)
            throw new InvalidOperationException(
                $"{what} after Start — declarations are locked once discovery " +
                "has been sent (receivers cache descriptors by id); use " +
                "BeginRedeclare to change them");
    }

    /// <summary>Locks declarations and sends the initial DiscoveryFrame —
    /// twice, back-to-back: the sender's first multicast datagram is
    /// dropped by some stacks' loopback/interface warm-up (observed on
    /// Windows), and discovery is idempotent by design, so the redundant
    /// frame costs ~70 bytes and heals the loss for every late joiner.</summary>
    public void Start(double nowSeconds)
    {
        _locked = true;
        if (double.IsNaN(_anchor)) _anchor = nowSeconds;
        SendAllPages(nowSeconds);
        SendAllPages(nowSeconds);
    }

    /// <summary>The descriptor-set generation discovery and motion carry.</summary>
    public uint Generation => _generation;

    /// <summary>Discovery pages of the current set (1 = unpaged).</summary>
    public int DiscoveryPageCount => Pages().Count;

    // ---- redeclaration (descriptor change: vehicle swap) ----

    /// <summary>Clears every declaration (object, channels, drive points,
    /// named points) and unlocks them, keeping the session anchor and the
    /// changed-only mode. Declare again, then call
    /// <see cref="CommitRedeclare"/>. Cold path — allocates.</summary>
    public void BeginRedeclare()
    {
        _descriptors.Clear();
        _pointDescriptors.Clear();
        _namedPoints.Clear();
        _points.Clear();
        _drivePoints.Clear();
        _changedValues.Clear();
        Array.Clear(_pointDirty);
        Array.Clear(_pushed);
        Array.Clear(_boolValue);
        Array.Clear(_boolSet);
        Array.Clear(_isBool);
        Array.Clear(_alwaysSend);
        Array.Clear(_everSent);
        Array.Clear(_valueSet);
        Array.Clear(_isSession);
        Array.Clear(_sessionValue);
        Array.Clear(_sessionBytes);
        Array.Clear(_motionById);
        _sessionValues.Clear();
        _channelCount = 0;
        _discoveryFrame = null;
        _pages = null;
        _motionFrame = null;
        _motionValues = null;
        _parts.Clear();
        _partValues.Clear();
        _partCount = 1;
        _refreshCursor = 0;
        _locked = false;
    }

    /// <summary>Locks the new declarations and sends discovery at once
    /// (twice, as in Start) — the schema's re-send on descriptor change.</summary>
    public void CommitRedeclare(double nowSeconds)
    {
        _locked = true;
        if (double.IsNaN(_anchor)) _anchor = nowSeconds;
        if (_discoverySent) _generation++; // ids may be reassigned: a new generation
        SendAllPages(nowSeconds);
        SendAllPages(nowSeconds);
    }

    // ---- hot path (receive thread only) ----

    public void SetPose(Vector3? position, Quaternion orientation)
    {
        _hasPosition = position.HasValue;
        if (position.HasValue) _position = position.Value;
        _orientation = orientation;
    }

    /// <summary>World-space velocity/acceleration in m/s, m/s² (Kinematics
    /// union member). Mutually exclusive with SetBodyDynamics per packet —
    /// whichever was set last wins.</summary>
    public void SetKinematics(Vector3 velocity, Vector3 acceleration)
    {
        _dyn = Dyn.Kinematics;
        _velocity = velocity; _acceleration = acceleration;
    }

    /// <summary>Accelerometer-style specific force (m/s², gravity reaction
    /// included) and angular velocity (rad/s) in body axes (BodyDynamics
    /// union member).</summary>
    public void SetBodyDynamics(Vector3 specificForce, Vector3 angularVelocity)
    {
        _dyn = Dyn.BodyDynamics;
        _specificForce = specificForce; _angularVelocity = angularVelocity;
    }

    /// <summary>Sends <c>noDynamics</c> until the next SetKinematics or
    /// SetBodyDynamics (e.g. while paused, CONVENTIONS C-11).</summary>
    public void ClearDynamics() => _dyn = Dyn.None;

    public void SetChannel(OgsChannel channel, double value)
    {
        _pushed[channel.Id] = value;
        _valueSet[channel.Id] = true;
    }

    public void SetChannel(OgsChannel channel, bool value)
    {
        _boolValue[channel.Id] = value;
        _boolSet[channel.Id] = true;
        _valueSet[channel.Id] = true;
    }

    // Every WheelState measurement is its own optionality union: a setter
    // selects only the values it is given; anything never set stays "not
    // measured". NaN also leaves (or returns) a field to "not measured".
    static void Measure(Schema.WheelState.RpmGroup g, float v) { if (float.IsNaN(v)) g.SetNoRpm(); else g.Value = v; }
    static void Measure(Schema.WheelState.SlipGroup g, float v) { if (float.IsNaN(v)) g.SetNoSlip(); else g.Value = v; }
    static void Measure(Schema.WheelState.CompressionGroup g, float v) { if (float.IsNaN(v)) g.SetNoCompression(); else g.Value = v; }
    static void Measure(Schema.WheelState.SteerAngleGroup g, float v) { if (float.IsNaN(v)) g.SetNoSteerAngle(); else g.Value = v; }
    static void Measure(Schema.WheelState.LoadForceGroup g, float v) { if (float.IsNaN(v)) g.SetNoLoadForce(); else g.Value = v; }
    static void Measure(Schema.WheelState.DamperVelocityGroup g, float v) { if (float.IsNaN(v)) g.SetNoDamperVelocity(); else g.Value = v; }

    /// <summary>Wheel slip ratio and suspension compression (m), and contact.</summary>
    public void SetWheel(int index, float slip, float compression,
                         Schema.ContactMedium contact)
    {
        var w = _points[index].InitWheel(); // the existing state: no allocation
        Measure(w.Slip, slip);
        Measure(w.Compression, compression);
        w.Contact = contact;
        _pointDirty[index] = true;
    }

    /// <summary>Wheel state with rotational speed (rpm, C-3).</summary>
    public void SetWheel(int index, float rpm, float slip, float compression,
                         Schema.ContactMedium contact)
    {
        var w = _points[index].InitWheel();
        Measure(w.Rpm, rpm);
        Measure(w.Slip, slip);
        Measure(w.Compression, compression);
        w.Contact = contact;
        _pointDirty[index] = true;
    }

    /// <summary>Road-wheel steer angle, rad, + = right (C-4/C-6).</summary>
    public void SetWheelSteer(int index, float steerAngle)
    {
        Measure(_points[index].InitWheel().SteerAngle, steerAngle);
        _pointDirty[index] = true;
    }

    /// <summary>Steer angle (rad), suspension load force (N) and damper
    /// velocity (m/s); NaN for any the source does not measure.</summary>
    public void SetWheelDynamics(int index, float steerAngle, float loadForce, float damperVelocity)
    {
        var w = _points[index].InitWheel();
        Measure(w.SteerAngle, steerAngle);
        Measure(w.LoadForce, loadForce);
        Measure(w.DamperVelocity, damperVelocity);
        _pointDirty[index] = true;
    }

    /// <summary>Tyre pressure (Pa), carcass temperature (°C) and wear (0..1
    /// remaining) — reported together (TyreState).</summary>
    public void SetWheelTyre(int index, float pressure, float temperature, float wear)
    {
        var t = _points[index].InitWheel().InitTyre();
        t.Pressure = pressure;
        t.Temperature = temperature;
        t.Wear = wear;
        _pointDirty[index] = true;
    }

    /// <summary>Wheel drive torque (Nm) and brake pressure (Pa); NaN
    /// leaves a quantity the source does not measure unset (`noTorque`,
    /// `noBrakePressure`), never 0.</summary>
    public void SetWheelLoads(int index, float torque, float brakePressure)
    {
        var w = _points[index].InitWheel();
        var t = w.Torque;
        if (float.IsNaN(torque)) t.SetNoTorque(); else t.Value = torque;
        var b = w.BrakePressure;
        if (float.IsNaN(brakePressure)) b.SetNoBrakePressure(); else b.Value = brakePressure;
        _pointDirty[index] = true;
    }

    /// <summary>Updates a session text channel from UTF-8 bytes (a NUL ends
    /// the text, as in C string fields). Unchanged bytes cost a compare;
    /// a change decodes once and re-sends discovery on the next Tick.</summary>
    public void SetSessionText(OgsChannel channel, ReadOnlySpan<byte> utf8)
    {
        if (UpdateText(ref _sessionBytes[channel.Id], utf8, out var text))
        {
            // pages share the value instances: re-send only the values page
            _sessionValue[channel.Id]!.Text = text;
            _sendValuePages = true;
        }
    }

    public void SetSessionText(OgsChannel channel, string text)
    {
        if (_sessionValue[channel.Id]!.Text == text) return;
        _sessionValue[channel.Id]!.Text = text;
        _sendValuePages = true;
    }

    /// <summary>Updates the object's display location (track/place name)
    /// from UTF-8 bytes; re-sends discovery when it changes.</summary>
    public void SetLocation(ReadOnlySpan<byte> utf8)
    {
        if (!UpdateText(ref _locationBytes, utf8, out var text)) return;
        _location = text.Length == 0 ? "local" : text;
        if (_discoveryFrame is not null) _discoveryFrame.Objects![0].Location = _location;
        IdentityChanged();
    }

    /// <summary>Updates the object's type label (vehicle/aircraft name —
    /// receivers may select per-vehicle profiles on it) from UTF-8 bytes;
    /// re-sends discovery when it changes.</summary>
    public void SetTypeLabel(ReadOnlySpan<byte> utf8)
    {
        if (!UpdateText(ref _typeLabelBytes, utf8, out var text)) return;
        _typeLabel = text;
        if (_discoveryFrame is not null) _discoveryFrame.Objects![0].TypeLabel = _typeLabel;
        IdentityChanged();
    }

    // object identity changed: a new generation once receivers hold tables,
    // the pages rebuilt and every one re-sent at the next tick (cold path)
    void IdentityChanged()
    {
        if (_discoverySent) _generation++;
        _pages = null;
        _sendAllPages = true;
    }

    static bool UpdateText(ref byte[]? cache, ReadOnlySpan<byte> utf8, out string text)
    {
        int nul = utf8.IndexOf((byte)0);
        if (nul >= 0) utf8 = utf8.Slice(0, nul);
        if (cache is not null && utf8.SequenceEqual(cache)) { text = ""; return false; }
        cache = utf8.ToArray();
        text = System.Text.Encoding.UTF8.GetString(utf8).Trim();
        return true;
    }

    public void SetPropeller(int index, float rpm)
    {
        _points[index].InitPropeller().Rpm = rpm;
        _pointDirty[index] = true;
    }

    public void SetJet(int index, float throttle)
    {
        _points[index].InitJet().Throttle = throttle;
        _pointDirty[index] = true;
    }

    /// <summary>Updates the discovery environment (kg/m³, °C, Pa). The
    /// first update replaces the defaults with a discovery re-send on the
    /// next Tick; later ones ride the regular re-send.</summary>
    public void SetEnvironment(float airDensity, float temperature, float pressure)
    {
        _airDensity = airDensity; _temperature = temperature; _pressure = pressure;
        // a known atmosphere from here on: mark it before refreshing the pages
        if (!_environmentSet) { _environmentSet = true; _environmentPending = true; }
        if (_pages is not null)
            foreach (var page in _pages) FillEnvironment(page.Environment);
    }

    /// <summary>Encodes and sends one motion frame if the cadence allows,
    /// and re-sends discovery on the standard 2 s interval. Call once per
    /// received legacy packet with the current monotonic clock.</summary>
    public void Tick(double nowSeconds)
    {
        if (double.IsNaN(_anchor)) _anchor = nowSeconds;
        _locked = true;

        PaceDiscovery(nowSeconds);

        if (nowSeconds - _lastMotion < _minMotionInterval)
            return;

        int parts = BuildMotion(nowSeconds);
        for (int i = 0; i < parts; i++) Send(FrameType.Motion, _parts[i]);
        _lastMotion = nowSeconds;
    }

    /// <summary>Re-sends discovery when the interval elapsed without any
    /// input packet driving Tick — keeps the standard's 2 s re-send alive
    /// during quiet input (menus, paused sessions). No motion is sent.</summary>
    public void DiscoveryKeepAlive(double nowSeconds)
    {
        if (double.IsNaN(_anchor)) return; // not started yet
        PaceDiscovery(nowSeconds);
    }

    // Pacing (implementation-udp.md § Send cadence): every page back-to-back
    // after a change; the values page alone after a session-text change;
    // otherwise each re-send cycle spreads the pages across the interval.
    void PaceDiscovery(double nowSeconds)
    {
        if (_sendAllPages || _environmentPending) { SendAllPages(nowSeconds); return; }
        var pages = Pages();
        if (_sendValuePages)
        {
            foreach (int i in _valuePages) Send(FrameType.Discovery, pages[i]);
            _sendValuePages = false;
        }
        if (nowSeconds - _lastDiscovery >= _discoveryInterval)
        {
            _lastDiscovery = nowSeconds;
            _nextPage = 0;
        }
        double spacing = _discoveryInterval / pages.Count;
        while (_nextPage < pages.Count && nowSeconds - _lastDiscovery >= _nextPage * spacing)
            Send(FrameType.Discovery, pages[_nextPage++]);
    }

    void SendAllPages(double nowSeconds)
    {
        var pages = Pages();
        foreach (var page in pages) Send(FrameType.Discovery, page);
        _lastDiscovery = nowSeconds;
        _nextPage = pages.Count;
        _sendAllPages = _sendValuePages = _environmentPending = false;
        _discoverySent = true;
        // late joiners get every value within the interval
        _refreshCursor = 0;
        _refreshCycleStart = nowSeconds;
    }

    List<Schema.DiscoveryFrame> Pages()
    {
        if (_pages is not null) return _pages;
        var complete = CompleteDiscovery();
        _pages = DiscoveryPager.Paginate(() =>
        {
            var page = NewDiscovery();
            FillEnvironment(page.Environment);
            return page;
        }, complete.Objects!, _generation);
        _valuePages.Clear();
        for (int i = 0; i < _pages.Count; i++)
            foreach (var o in _pages[i].Objects!)
                if (o.Values is { Count: > 0 } || o.Texts is { Count: > 0 }) { _valuePages.Add(i); break; }
        return _pages;
    }

    void FillEnvironment(Schema.Environment env)
    {
        env.Gravity = _gravity; env.Medium = Schema.ContactMedium.Asphalt;
        var atmosphere = env.Atmosphere;
        if (_environmentSet)
        {
            var air = atmosphere.InitKnown();
            air.AirDensity = _airDensity; air.Temperature = _temperature; air.Pressure = _pressure; // Pa
        }
        else atmosphere.SetNoAtmosphere(); // the source has no atmosphere model
    }

    Schema.DiscoveryFrame NewDiscovery() => new()
    {
        SchemaVersion = 1,
        GameName = _gameName,
        SessionStartUnixUs = _sessionStartTicks / 10, // .NET ticks (100 ns) → µs
        InstanceLabel = _instanceLabel,
        Host = _host,
    };

    Schema.DiscoveryFrame CompleteDiscovery()
    {
        _discoveryFrame ??= new Schema.DiscoveryFrame
        {
            SchemaVersion = 1,
            GameName = _gameName,
            SessionStartUnixUs = _sessionStartTicks / 10, // .NET ticks (100 ns) → µs
            InstanceLabel = _instanceLabel,
            Host = _host,
            Environment =
            {
                Gravity = _gravity, Medium = Schema.ContactMedium.Asphalt,
            },
            Objects = new List<Schema.ObjectDescriptor>
            {
                new()
                {
                    Name = _objectName,
                    Type = _objectType,
                    TypeLabel = _typeLabel,
                    Primary = true,
                    Location = _location,
                    ReferencePoint = { Name = "origin" },
                    NamedPoints = _namedPoints,
                    DrivePoints = _pointDescriptors,
                    Channels = _descriptors,
                    Texts = _sessionValues,
                },
            },
        };
        FillEnvironment(_discoveryFrame.Environment);
        return _discoveryFrame;
    }

    void Send(FrameType type, WireStruct frame)
    {
        var datagram = CanonicalEncoder.EncodeDatagram(type, frame);
        if (_sink is not null) _sink(type, datagram);
        else _sender!.Send(datagram);
    }

    // Reuses the cached object graph; setting a value re-selects its union
    // variant, so a mutated graph encodes exactly like a fresh one (same
    // technique as the upstream TelemetryPublisher).
    // Returns the number of parts this tick fills (_parts[0..n)).
    int BuildMotion(double nowSeconds)
    {
        if (_motionFrame is null)
        {
            var values = new List<Schema.ChannelValue>(_channelCount);
            for (int i = 0; i < _channelCount; i++)
            {
                if (_isSession[i]) continue; // discovery-only
                var cv = new Schema.ChannelValue { Id = (ushort)i };
                values.Add(cv);
                _motionById[i] = cv;
            }
            _motionValues = values;
            _partValues.Add(new List<Schema.ChannelValue>(values.Count));
            _motionFrame = new Schema.MotionFrame
            {
                Objects = new List<Schema.MotionObject>
                {
                    new()
                    {
                        Name = _objectName,
                        Channels = _partValues[0],
                        DrivePoints = _drivePoints,
                    },
                },
            };
            _parts.Add(_motionFrame);
            PlanParts();
        }

        var obj = _motionFrame.Objects![0];
        // an absent position encodes as zeros — what receivers read for it anyway
        var p = _hasPosition ? _position : default;
        obj.Position.X = p.X; obj.Position.Y = p.Y; obj.Position.Z = p.Z;
        obj.Orientation.X = _orientation.X;
        obj.Orientation.Y = _orientation.Y;
        obj.Orientation.Z = _orientation.Z;
        obj.Orientation.W = _orientation.W;

        switch (_dyn)
        {
            case Dyn.Kinematics:
                var k = obj.InitKinematics();
                k.Velocity.X = _velocity.X; k.Velocity.Y = _velocity.Y; k.Velocity.Z = _velocity.Z;
                k.Acceleration.X = _acceleration.X; k.Acceleration.Y = _acceleration.Y;
                k.Acceleration.Z = _acceleration.Z;
                break;
            case Dyn.BodyDynamics:
                var bd = obj.InitBodyDynamics();
                bd.SpecificForce.X = _specificForce.X; bd.SpecificForce.Y = _specificForce.Y;
                bd.SpecificForce.Z = _specificForce.Z;
                bd.AngularVelocity.X = _angularVelocity.X; bd.AngularVelocity.Y = _angularVelocity.Y;
                bd.AngularVelocity.Z = _angularVelocity.Z;
                break;
            default:
                obj.SetNoDynamics(); // union default — dynamics absent
                break;
        }

        // per-point state: only points touched since the last tick ride the
        // frame (an empty list encodes as null)
        _drivePoints.Clear();
        for (int i = 0; i < _points.Count; i++)
        {
            if (!_pointDirty[i]) continue;
            _drivePoints.Add(_points[i]);
            _pointDirty[i] = false;
        }

        List<Schema.ChannelValue> tickValues = _motionValues!;
        if (_changedOnly) { WriteChangedValues(nowSeconds); tickValues = _changedValues; }
        else
            for (int i = 0; i < _channelCount; i++)
            {
                var cv = _motionById[i];
                if (cv is null) continue;
                if (_isBool[i])
                    cv.Boolean = _boolSet[i] && _boolValue[i];
                else
                    cv.Number = (float)_pushed[i];
                _boolSet[i] = false; // one-shot: absent booleans read false next tick
            }

        // split the tick's values into parts within the datagram budget
        int n = tickValues.Count;
        int parts = n <= _part0Capacity ? 1 : 1 + (n - _part0Capacity + _partCapacity - 1) / _partCapacity;
        EnsureParts(parts);
        int next = 0;
        for (int part = 0; part < parts; part++)
        {
            var list = _partValues[part];
            list.Clear();
            int take = Math.Min(part == 0 ? _part0Capacity : _partCapacity, n - next);
            for (int k = 0; k < take; k++) list.Add(tickValues[next++]);
            var frame = _parts[part];
            frame.Timestamp = nowSeconds - _anchor;
            frame.Generation = _generation;
            frame.Part = parts > 1 ? (ushort)part : (ushort)0;
            frame.PartCount = parts > 1 ? (ushort)parts : (ushort)0;
        }
        _partCount = parts;
        return parts;
    }

    // Part capacities, measured once with the largest variants the frames
    // can select: body dynamics and every declared drive point in its
    // largest state ride part 0.
    void PlanParts()
    {
        var probe0 = new Schema.MotionObject
        {
            Name = _objectName,
            Channels = new List<Schema.ChannelValue> { new() { Number = 1f } },
            DrivePoints = new List<Schema.DrivePoint>(),
        };
        probe0.InitBodyDynamics().SpecificForce.X = 1f;
        foreach (var d in _pointDescriptors)
        {
            var point = new Schema.DrivePoint { Id = d.Id };
            switch (d.Which)
            {
                case Schema.DrivePointDescriptor.Variant.Propeller:
                    var prop = point.InitPropeller(); prop.Rpm = 1f; prop.InitExtended().Pitch = 1f;
                    break;
                case Schema.DrivePointDescriptor.Variant.Jet:
                    var jet = point.InitJet(); jet.Throttle = 1f; jet.Thrust = 1f;
                    break;
                default:
                    var wheel = point.InitWheel();
                    // the largest state: every measurement selected, and the tyre
                    Measure(wheel.Rpm, 1f); Measure(wheel.Slip, 1f); Measure(wheel.Compression, 1f);
                    Measure(wheel.SteerAngle, 1f); Measure(wheel.LoadForce, 1f); Measure(wheel.DamperVelocity, 1f);
                    var probeTorque = wheel.Torque; probeTorque.Value = 1f;
                    var probeBrake = wheel.BrakePressure; probeBrake.Value = 1f;
                    wheel.InitTyre().Wear = 1f;
                    break;
            }
            probe0.DrivePoints!.Add(point);
        }
        var later = new Schema.MotionObject
        {
            Name = _objectName,
            Channels = new List<Schema.ChannelValue> { new() { Number = 1f } },
        };
        var plan = MotionPartPlanner.Plan(2 * MaxChannels,
            new Schema.MotionFrame { Timestamp = 1, Objects = new List<Schema.MotionObject> { probe0 } },
            new Schema.MotionFrame { Timestamp = 1, Objects = new List<Schema.MotionObject> { later } });
        _part0Capacity = plan[0].Count;
        _partCapacity = plan.Count > 1 ? plan[1].Count : plan[0].Count;
    }

    // Later parts are allocated the first time a tick needs them, then reused.
    void EnsureParts(int parts)
    {
        while (_parts.Count < parts)
        {
            var list = new List<Schema.ChannelValue>(_partCapacity);
            _partValues.Add(list);
            _parts.Add(new Schema.MotionFrame
            {
                Objects = new List<Schema.MotionObject> { new() { Name = _objectName, Channels = list } },
            });
        }
    }

    // Changed-only mode: booleans hold their last set value (absence on the
    // wire already means unchanged), numbers compare at wire precision.
    void WriteChangedValues(double nowSeconds)
    {
        _changedValues.Clear();
        Array.Clear(_addedThisTick, 0, _channelCount);
        for (int i = 0; i < _channelCount; i++)
        {
            if (!_valueSet[i]) continue;
            var cv = _motionById[i];
            if (cv is null) continue;
            if (_isBool[i])
            {
                if (!_alwaysSend[i] && _everSent[i] && _sentBool[i] == _boolValue[i]) continue;
            }
            else if (!_alwaysSend[i] && _everSent[i] && _sentNumber[i].Equals((float)_pushed[i])) continue;
            AddValue(i, cv);
        }

        // bounded refresh: unchanged values too, round-robin in a continuous
        // cycle paced to last one discovery interval, so no value goes longer
        // than the interval without being re-sent (late joiners converge)
        // and the refresh costs no more than that guarantee needs
        if (_refreshCursor >= _channelCount || double.IsNaN(_refreshCycleStart))
        {
            _refreshCursor = 0;
            _refreshCycleStart = nowSeconds;
        }
        double tick = double.IsNegativeInfinity(_lastMotion) ? 0 : nowSeconds - _lastMotion;
        // aim to finish at 90 % of the interval: margin for tick jitter
        double left = Math.Max(_refreshCycleStart + 0.9 * _discoveryInterval - nowSeconds, tick);
        int remaining = _channelCount - _refreshCursor;
        int quota = left > 0 && tick > 0 ? Math.Max(1, (int)Math.Ceiling(remaining * tick / left)) : remaining;
        while (quota > 0 && _refreshCursor < _channelCount)
        {
            int i = _refreshCursor++;
            var cv = _motionById[i];
            if (!_valueSet[i] || cv is null || _addedThisTick[i]) continue;
            AddValue(i, cv);
            quota--;
        }
    }

    void AddValue(int i, Schema.ChannelValue cv)
    {
        if (_isBool[i])
        {
            cv.Boolean = _boolValue[i];
            _sentBool[i] = _boolValue[i];
        }
        else
        {
            float f = (float)_pushed[i];
            cv.Number = f;
            _sentNumber[i] = f;
        }
        _everSent[i] = true;
        _addedThisTick[i] = true;
        _changedValues.Add(cv);
    }

    public void Dispose()
    {
        _descriptors.Clear();
        _points.Clear();
        _drivePoints.Clear();
        _motionFrame = null;
        _motionValues = null;
        _parts.Clear();
        _partValues.Clear();
        _discoveryFrame = null;
        _pages = null;
        _sender?.Dispose();
    }
}
}
