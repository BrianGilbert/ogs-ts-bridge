#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Ogsa.Telemetry;

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
/// once; every tick reuses cached Cap'n Proto object graphs, rewrites the
/// values in place, serializes into a fresh small message and hands it to
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
    readonly List<CapnpGen.ChannelDescriptor> _descriptors = new();
    readonly List<CapnpGen.DrivePointDescriptor> _wheelDescriptors = new();
    string _objectName = "player";
    CapnpGen.ObjectType _objectType = CapnpGen.ObjectType.vehicle;
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

    // wheel state cache + per-tick dirty list
    readonly List<CapnpGen.WheelState> _wheels = new();
    readonly bool[] _wheelDirty = new bool[16];
    readonly List<CapnpGen.DrivePoint> _drivePoints = new(); // reused list

    // cached wire graphs
    CapnpGen.DiscoveryFrame? _discoveryFrame;
    CapnpGen.MotionFrame? _motionFrame;
    List<CapnpGen.ChannelValue>? _motionValues;

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

    public void DeclareObject(string name, CapnpGen.ObjectType type, string typeLabel = "")
    {
        LockCheck("DeclareObject");
        _objectName = name; _objectType = type; _typeLabel = typeLabel;
    }

    public OgsaChannel AddChannel(string name, string unit, string description = "",
                                  double min = double.NaN, double max = double.NaN)
    {
        LockCheck("AddChannel");
        var h = NewChannel(boolean: false);
        _descriptors.Add(new CapnpGen.ChannelDescriptor
        {
            Id = (ushort)h.Id,
            Name = name,
            Unit = unit,
            Description = description,
            Range = double.IsNaN(min) || double.IsNaN(max)
                ? null!
                : new CapnpGen.Range { Min = min, Max = max },
        });
        return h;
    }

    public OgsaChannel AddBooleanChannel(string name, string description = "")
    {
        LockCheck("AddBooleanChannel");
        var h = NewChannel(boolean: true);
        _descriptors.Add(new CapnpGen.ChannelDescriptor
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
        _wheelDescriptors.Add(new CapnpGen.DrivePointDescriptor
        {
            Id = (ushort)id,
            Name = name,
            Offset = new CapnpGen.Vector3 { X = offset.X, Y = offset.Y, Z = offset.Z },
            Wheel = new CapnpGen.WheelSpec
            {
                Radius = radius, Driven = driven, Steered = steered,
                Suspension = new CapnpGen.SuspensionSpec
                {
                    Travel = new CapnpGen.Range { Min = travelMin, Max = travelMax },
                    Stiffness = 65000f, Damping = 5000f,
                },
            },
        });
        _wheels.Add(new CapnpGen.WheelState { Contact = CapnpGen.ContactMedium.asphalt });
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
                         CapnpGen.ContactMedium contact)
    {
        var w = _wheels[index];
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

        var mb = Capnp.MessageBuilder.Create(512);
        BuildMotion(nowSeconds).serialize(mb.BuildRoot<CapnpGen.MotionFrame.WRITER>());
        if (_sink is not null) _sink(FrameType.Motion, Envelope.EncodeDatagram(FrameType.Motion, mb.Frame.Segments));
        else _sender!.Send(FrameType.Motion, mb);
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
        _discoveryFrame ??= new CapnpGen.DiscoveryFrame
        {
            SchemaVersion = 1,
            GameName = _gameName,
            SessionStartUnixUs = _sessionStartTicks / 10, // .NET ticks (100 ns) → µs
            Environment = new CapnpGen.Environment
            {
                AirDensity = 1.225f, Temperature = 20f, Pressure = 1.01325f,
                Gravity = 9.81f, Medium = CapnpGen.ContactMedium.asphalt,
            },
            Objects = new List<CapnpGen.ObjectDescriptor>
            {
                new()
                {
                    Name = _objectName,
                    Type = _objectType,
                    TypeLabel = _typeLabel,
                    Primary = true,
                    Location = "local",
                    ReferencePoint = new CapnpGen.NamedPoint
                        { Name = "origin", Position = new CapnpGen.Vector3() },
                    DrivePoints = _wheelDescriptors,
                    Channels = _descriptors,
                },
            },
        };
        var mb = Capnp.MessageBuilder.Create(512);
        _discoveryFrame.serialize(mb.BuildRoot<CapnpGen.DiscoveryFrame.WRITER>());
        if (_sink is not null) _sink(FrameType.Discovery, Envelope.EncodeDatagram(FrameType.Discovery, mb.Frame.Segments));
        else _sender!.Send(FrameType.Discovery, mb);
        _lastDiscovery = nowSeconds;
    }

    // Reuses the cached object graph; union discriminants rewrite on
    // assignment so a mutated graph serializes exactly like a fresh one
    // (same technique as the upstream TelemetryPublisher).
    CapnpGen.MotionFrame BuildMotion(double nowSeconds)
    {
        if (_motionFrame is null)
        {
            var values = new List<CapnpGen.ChannelValue>(_channelCount);
            for (int i = 0; i < _channelCount; i++)
                values.Add(new CapnpGen.ChannelValue { Id = (ushort)i });
            _motionValues = values;

            var o = new CapnpGen.MotionObject
            {
                Name = _objectName,
                Position = new CapnpGen.Vector3(),
                Orientation = new CapnpGen.Quaternion(),
                Kinematics = new CapnpGen.Kinematics
                {
                    Velocity = new CapnpGen.Vector3(),
                    Acceleration = new CapnpGen.Vector3(),
                },
            };
            if (values.Count > 0) o.Channels = values;
            _motionFrame = new CapnpGen.MotionFrame
            {
                Objects = new List<CapnpGen.MotionObject> { o },
            };
        }

        var obj = _motionFrame.Objects[0];
        if (_hasPosition)
        {
            obj.Position!.X = _position.X;
            obj.Position.Y = _position.Y;
            obj.Position.Z = _position.Z;
        }
        else obj.Position = null!;
        obj.Orientation!.X = _orientation.X;
        obj.Orientation.Y = _orientation.Y;
        obj.Orientation.Z = _orientation.Z;
        obj.Orientation.W = _orientation.W;

        switch (_dyn)
        {
            case Dyn.Kinematics:
                obj.Kinematics ??= new CapnpGen.Kinematics
                {
                    Velocity = new CapnpGen.Vector3(),
                    Acceleration = new CapnpGen.Vector3(),
                };
                obj.Kinematics.Velocity.X = _velocity.X;
                obj.Kinematics.Velocity.Y = _velocity.Y;
                obj.Kinematics.Velocity.Z = _velocity.Z;
                obj.Kinematics.Acceleration.X = _acceleration.X;
                obj.Kinematics.Acceleration.Y = _acceleration.Y;
                obj.Kinematics.Acceleration.Z = _acceleration.Z;
                break;
            case Dyn.BodyDynamics:
                obj.BodyDynamics ??= new CapnpGen.BodyDynamics
                {
                    SpecificForce = new CapnpGen.Vector3(),
                    AngularVelocity = new CapnpGen.Vector3(),
                };
                obj.BodyDynamics.SpecificForce.X = _specificForce.X;
                obj.BodyDynamics.SpecificForce.Y = _specificForce.Y;
                obj.BodyDynamics.SpecificForce.Z = _specificForce.Z;
                obj.BodyDynamics.AngularVelocity.X = _angularVelocity.X;
                obj.BodyDynamics.AngularVelocity.Y = _angularVelocity.Y;
                obj.BodyDynamics.AngularVelocity.Z = _angularVelocity.Z;
                break;
            default:
                obj.which = CapnpGen.MotionObject.WHICH.NoDynamics; // union default — dynamics absent
                break;
        }

        // per-wheel state: only wheels touched since the last tick ride the frame
        _drivePoints.Clear();
        for (int i = 0; i < _wheels.Count; i++)
        {
            if (!_wheelDirty[i]) continue;
            _drivePoints.Add(new CapnpGen.DrivePoint
            {
                Id = (ushort)i,
                Wheel = _wheels[i],
            });
            _wheelDirty[i] = false;
        }
        obj.DrivePoints = _drivePoints.Count > 0 ? _drivePoints : null!;

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
