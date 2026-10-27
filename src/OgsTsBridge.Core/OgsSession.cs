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
    float _gravity = 9.81f; // the g the sources scale accelerometer values with
    bool _locked;

    // discovery environment (sent with each discovery)
    float _airDensity = 1.225f, _temperature = 20f, _pressure = 101325f;
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

    // changed-only channel mode: a channel rides the motion frame when its
    // value differs from the last one sent, when it is marked always-sent,
    // or on the full refresh that follows each discovery send; a channel
    // never set (its source variable unavailable) is never sent
    bool _changedOnly;
    readonly bool[] _valueSet = new bool[MaxChannels];
    readonly bool[] _alwaysSend = new bool[MaxChannels];
    readonly bool[] _everSent = new bool[MaxChannels];
    readonly float[] _sentNumber = new float[MaxChannels];
    readonly bool[] _sentBool = new bool[MaxChannels];
    bool _refreshDue = true;
    readonly List<Schema.ChannelValue> _changedValues = new(); // reused list

    // drive point state cache (one reused drive point each) + per-tick dirty list
    readonly List<Schema.DrivePoint> _points = new();
    readonly bool[] _pointDirty = new bool[MaxDrivePoints];
    readonly List<Schema.DrivePoint> _drivePoints = new(); // reused list

    // cached wire graphs
    Schema.DiscoveryFrame? _discoveryFrame;
    Schema.MotionFrame? _motionFrame;
    List<Schema.ChannelValue>? _motionValues;

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

    public void DeclareObject(string name, Schema.ObjectType type, string typeLabel = "")
    {
        LockCheck("DeclareObject");
        _objectName = name; _objectType = type; _typeLabel = typeLabel;
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
        });
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
        SendDiscovery(nowSeconds);
        SendDiscovery(nowSeconds);
    }

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
        _channelCount = 0;
        _discoveryFrame = null;
        _motionFrame = null;
        _motionValues = null;
        _locked = false;
    }

    /// <summary>Locks the new declarations and sends discovery at once
    /// (twice, as in Start) — the schema's re-send on descriptor change.</summary>
    public void CommitRedeclare(double nowSeconds)
    {
        _locked = true;
        if (double.IsNaN(_anchor)) _anchor = nowSeconds;
        SendDiscovery(nowSeconds);
        SendDiscovery(nowSeconds);
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

    public void SetWheel(int index, float slip, float compression,
                         Schema.ContactMedium contact)
    {
        var w = _points[index].InitWheel(); // the existing state: no allocation
        w.Slip = slip;
        w.Compression = compression;
        w.Contact = contact;
        _pointDirty[index] = true;
    }

    /// <summary>Wheel state with rotational speed (rpm, C-3).</summary>
    public void SetWheel(int index, float rpm, float slip, float compression,
                         Schema.ContactMedium contact)
    {
        var w = _points[index].InitWheel();
        w.Rpm = rpm;
        w.Slip = slip;
        w.Compression = compression;
        w.Contact = contact;
        _pointDirty[index] = true;
    }

    /// <summary>Road-wheel steer angle, rad, + = right (C-4/C-6) — selects
    /// the wheel's extended state (its other extended fields read 0).</summary>
    public void SetWheelSteer(int index, float steerAngle)
    {
        _points[index].InitWheel().InitExtended().SteerAngle = steerAngle;
        _pointDirty[index] = true;
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
        if (!_environmentSet) { _environmentSet = true; _environmentPending = true; }
    }

    /// <summary>Encodes and sends one motion frame if the cadence allows,
    /// and re-sends discovery on the standard 2 s interval. Call once per
    /// received legacy packet with the current monotonic clock.</summary>
    public void Tick(double nowSeconds)
    {
        if (double.IsNaN(_anchor)) _anchor = nowSeconds;
        _locked = true;

        if (_environmentPending || nowSeconds - _lastDiscovery >= _discoveryInterval)
            SendDiscovery(nowSeconds);

        if (nowSeconds - _lastMotion < _minMotionInterval)
            return;

        Send(FrameType.Motion, BuildMotion(nowSeconds));
        _lastMotion = nowSeconds;
    }

    /// <summary>Re-sends discovery when the interval elapsed without any
    /// input packet driving Tick — keeps the standard's 2 s re-send alive
    /// during quiet input (menus, paused sessions). No motion is sent.</summary>
    public void DiscoveryKeepAlive(double nowSeconds)
    {
        if (double.IsNaN(_anchor)) return; // not started yet
        if (nowSeconds - _lastDiscovery >= _discoveryInterval)
            SendDiscovery(nowSeconds);
    }

    void SendDiscovery(double nowSeconds)
    {
        _discoveryFrame ??= new Schema.DiscoveryFrame
        {
            SchemaVersion = 1,
            GameName = _gameName,
            SessionStartUnixUs = _sessionStartTicks / 10, // .NET ticks (100 ns) → µs
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
                    Location = "local",
                    ReferencePoint = { Name = "origin" },
                    NamedPoints = _namedPoints,
                    DrivePoints = _pointDescriptors,
                    Channels = _descriptors,
                },
            },
        };
        var env = _discoveryFrame.Environment;
        env.AirDensity = _airDensity; env.Temperature = _temperature; env.Pressure = _pressure; // Pa
        Send(FrameType.Discovery, _discoveryFrame);
        _lastDiscovery = nowSeconds;
        _environmentPending = false;
        _refreshDue = true;
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
    Schema.MotionFrame BuildMotion(double nowSeconds)
    {
        if (_motionFrame is null)
        {
            var values = new List<Schema.ChannelValue>(_channelCount);
            for (int i = 0; i < _channelCount; i++)
                values.Add(new Schema.ChannelValue { Id = (ushort)i });
            _motionValues = values;
            _motionFrame = new Schema.MotionFrame
            {
                Objects = new List<Schema.MotionObject>
                {
                    new()
                    {
                        Name = _objectName,
                        Channels = _changedOnly ? _changedValues : values,
                        DrivePoints = _drivePoints,
                    },
                },
            };
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

        if (_motionValues is not null)
        {
            if (_changedOnly) WriteChangedValues();
            else
                for (int i = 0; i < _channelCount; i++)
                {
                    if (_isBool[i])
                        _motionValues[i].Boolean = _boolSet[i] && _boolValue[i];
                    else
                        _motionValues[i].Number = (float)_pushed[i];
                    _boolSet[i] = false; // one-shot: absent booleans read false next tick
                }
        }

        _motionFrame.Timestamp = nowSeconds - _anchor;
        return _motionFrame;
    }

    // Changed-only mode: booleans hold their last set value (absence on the
    // wire already means unchanged), numbers compare at wire precision.
    void WriteChangedValues()
    {
        _changedValues.Clear();
        bool refresh = _refreshDue;
        _refreshDue = false;
        for (int i = 0; i < _channelCount; i++)
        {
            if (!_valueSet[i]) continue;
            var cv = _motionValues![i];
            if (_isBool[i])
            {
                bool b = _boolValue[i];
                if (!refresh && !_alwaysSend[i] && _everSent[i] && _sentBool[i] == b) continue;
                cv.Boolean = b;
                _sentBool[i] = b;
            }
            else
            {
                float f = (float)_pushed[i];
                if (!refresh && !_alwaysSend[i] && _everSent[i] &&
                    _sentNumber[i].Equals(f)) continue;
                cv.Number = f;
                _sentNumber[i] = f;
            }
            _everSent[i] = true;
            _changedValues.Add(cv);
        }
    }

    public void Dispose()
    {
        _descriptors.Clear();
        _points.Clear();
        _drivePoints.Clear();
        _motionFrame = null;
        _motionValues = null;
        _discoveryFrame = null;
        _sender?.Dispose();
    }
}
}
