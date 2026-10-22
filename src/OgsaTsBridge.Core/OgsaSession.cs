#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Ogsa.Telemetry;
using Ogsa.Telemetry.Wire;
using Schema = Ogsa.Telemetry.Schema;

namespace OgsaTsBridge
{

/// <summary>Handle to a declared channel; pass to SetChannel on the hot path.</summary>
public readonly struct OgsaChannel
{
    internal OgsaChannel(int id, string name) { Id = id; Name = name; }
    public int Id { get; }
    public string Name { get; }
}

/// <summary>
/// Latency-first OGSA-TS writer: declaration phase builds the DiscoveryFrame
/// once; every tick reuses cached schema-writer graphs, rewrites the
/// values in place, encodes them (canonical OGSA encoding) and hands it to
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
public sealed class OgsaSession : IDisposable
{
    enum Dyn { None, Kinematics, BodyDynamics }

    readonly TelemetrySender? _sender;
    readonly Action<FrameType, byte[]>? _sink;   // test capture path
    readonly string _gameName;
    readonly double _minMotionInterval;
    readonly double _discoveryInterval;
    readonly long _sessionStartTicks = DateTimeOffset.UtcNow.UtcTicks;

    // declaration state (locked at Start)
    readonly List<Schema.ChannelDescriptor> _descriptors = new();
    readonly List<Schema.DrivePointDescriptor> _wheelDescriptors = new();
    string _objectName = "player";
    Schema.ObjectType _objectType = Schema.ObjectType.Vehicle;
    string _typeLabel = "";
    bool _locked;

    // per-tick state (receive thread only)
    Vector3 _position; bool _hasPosition;
    Quaternion _orientation = Quaternion.Identity;
    Dyn _dyn;
    Vector3 _velocity, _acceleration, _specificForce, _angularVelocity;

    readonly double[] _pushed = new double[64];
    readonly bool[] _boolValue = new bool[64];
    readonly bool[] _boolSet = new bool[64];    // reset each tick; defaults false
    readonly bool[] _isBool = new bool[64];
    int _channelCount;

    // wheel state cache (one reused drive point per wheel) + per-tick dirty list
    readonly List<Schema.DrivePoint> _wheels = new();
    readonly bool[] _wheelDirty = new bool[16];
    readonly List<Schema.DrivePoint> _drivePoints = new(); // reused list

    // cached wire graphs
    Schema.DiscoveryFrame? _discoveryFrame;
    Schema.MotionFrame? _motionFrame;
    List<Schema.ChannelValue>? _motionValues;

    double _anchor = double.NaN;       // host clock value at Start
    double _lastDiscovery = double.NegativeInfinity;
    double _lastMotion = double.NegativeInfinity;

    public OgsaSession(TelemetrySender sender, string gameName,
                       double maxHz = 1000.0, double discoveryIntervalSeconds = 2.0)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _gameName = gameName;
        _minMotionInterval = maxHz > 0 ? 1.0 / maxHz : 0.0;
        _discoveryInterval = discoveryIntervalSeconds;
    }

    /// <summary>Test/in-process constructor: datagrams are handed to
    /// <paramref name="sink"/> instead of a socket.</summary>
    public OgsaSession(Action<FrameType, byte[]> sink, string gameName,
                       double maxHz = 1000.0, double discoveryIntervalSeconds = 2.0)
        : this(new TelemetrySender(), gameName, maxHz, discoveryIntervalSeconds)
        => _sink = sink;

    // ---- declaration phase (before Start) ----

    public void DeclareObject(string name, Schema.ObjectType type, string typeLabel = "")
    {
        LockCheck("DeclareObject");
        _objectName = name; _objectType = type; _typeLabel = typeLabel;
    }

    public OgsaChannel AddChannel(string name, string unit, string description = "",
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

    public OgsaChannel AddBooleanChannel(string name, string description = "")
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

    /// <summary>Declares a wheel drive point. Offsets are metres in the
    /// object-local frame (+x right, +y up, +z forward from the CG).</summary>
    public int AddWheel(string name, Vector3 offset, float radius,
                        bool driven, bool steered,
                        float travelMin = -0.05f, float travelMax = 0.15f)
    {
        LockCheck("AddWheel");
        int id = _wheelDescriptors.Count;
        var descriptor = new Schema.DrivePointDescriptor
        {
            Id = (ushort)id,
            Name = name,
            Offset = { X = offset.X, Y = offset.Y, Z = offset.Z },
        };
        var spec = descriptor.InitWheel();
        spec.Radius = radius; spec.Driven = driven; spec.Steered = steered;
        var suspension = spec.InitSuspension();
        suspension.Travel.Min = travelMin; suspension.Travel.Max = travelMax;
        suspension.Stiffness = 65000f; suspension.Damping = 5000f;
        _wheelDescriptors.Add(descriptor);

        var point = new Schema.DrivePoint { Id = (ushort)id };
        point.InitWheel().Contact = Schema.ContactMedium.Asphalt;
        _wheels.Add(point);
        return id;
    }

    OgsaChannel NewChannel(bool boolean)
    {
        if (_channelCount >= _pushed.Length)
            throw new NotSupportedException("channel budget exceeded for this build");
        int id = _channelCount++;
        _isBool[id] = boolean;
        _boolValue[id] = false;
        return new OgsaChannel(id, _descriptors.Count.ToString());
    }

    void LockCheck(string what)
    {
        if (_locked)
            throw new InvalidOperationException(
                $"{what} after Start — declarations are locked once discovery " +
                "has been sent (receivers cache descriptors by id)");
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

    public void SetChannel(OgsaChannel channel, double value) => _pushed[channel.Id] = value;

    public void SetChannel(OgsaChannel channel, bool value)
    {
        _boolValue[channel.Id] = value;
        _boolSet[channel.Id] = true;
    }

    public void SetWheel(int index, float slip, float compression,
                         Schema.ContactMedium contact)
    {
        var w = _wheels[index].InitWheel(); // the existing state: no allocation
        w.Slip = slip;
        w.Compression = compression;
        w.Contact = contact;
        _wheelDirty[index] = true;
    }

    /// <summary>Encodes and sends one motion frame if the cadence allows,
    /// and re-sends discovery on the standard 2 s interval. Call once per
    /// received legacy packet with the current monotonic clock.</summary>
    public void Tick(double nowSeconds)
    {
        if (double.IsNaN(_anchor)) _anchor = nowSeconds;
        _locked = true;

        if (nowSeconds - _lastDiscovery >= _discoveryInterval)
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
                AirDensity = 1.225f, Temperature = 20f, Pressure = 101325f, // Pa
                Gravity = 9.81f, Medium = Schema.ContactMedium.Asphalt,
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
                    DrivePoints = _wheelDescriptors,
                    Channels = _descriptors,
                },
            },
        };
        Send(FrameType.Discovery, _discoveryFrame);
        _lastDiscovery = nowSeconds;
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
                    new() { Name = _objectName, Channels = values, DrivePoints = _drivePoints },
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

        // per-wheel state: only wheels touched since the last tick ride the
        // frame (an empty list encodes as null)
        _drivePoints.Clear();
        for (int i = 0; i < _wheels.Count; i++)
        {
            if (!_wheelDirty[i]) continue;
            _drivePoints.Add(_wheels[i]);
            _wheelDirty[i] = false;
        }

        if (_motionValues is not null)
            for (int i = 0; i < _channelCount; i++)
            {
                if (_isBool[i])
                    _motionValues[i].Boolean = _boolSet[i] && _boolValue[i];
                else
                    _motionValues[i].Number = (float)_pushed[i];
                _boolSet[i] = false; // one-shot: absent booleans read false next tick
            }

        _motionFrame.Timestamp = nowSeconds - _anchor;
        return _motionFrame;
    }

    public void Dispose()
    {
        _descriptors.Clear();
        _wheels.Clear();
        _drivePoints.Clear();
        _motionFrame = null;
        _motionValues = null;
        _discoveryFrame = null;
        _sender?.Dispose();
    }
}
}
