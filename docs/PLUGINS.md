# Writing a source (plugin) for OGS-TS Bridge

A **source** is an adapter that reads one legacy telemetry *format* and
re-emits it as OGS-TS. This guide covers the contract, a full UDP
example, the shared-memory variant, axis conventions, options, testing,
and registration.

## The golden rule: a source is a format, not a game

Games reuse telemetry engines. pCARS2, Automobilista 2, and pCARS3 emit
the same SMS UDP stream; Assetto Corsa and ACC write the same `acpmf`
shared-memory pages; Dirt Rally 2 and DiRT 4 share the EGO layout (they
differ only in byte order); F1 2020–2024 share the Codemasters packet
family (header drift aside).

So implement **one source per format** and list every executable known
to emit it in `ProcessNames`. When a new game is confirmed to speak a
covered format, it's a one-line watch-list addition — no new code:

```csharp
public IReadOnlyList<string> ProcessNames { get; } = new[]
{
    "pCARS2AVX", "AMS2AVX", "pCARS3",   // all emit the SMS UDP stack
};
```

Auto mode activates the source when any of those executables appears and
stops it when the game exits (plain process names, no Steam dependency, no telemetry probing).

## The contract

`ILegacySource` (src/OgsTsBridge.Core/Sources/ILegacySource.cs):

| Member | What it does |
|---|---|
| `Id` / `Title` / `Description` | Catalogue entry + CLI/GUI labels |
| `DefaultPort` | UDP port the game is configured to send to |
| `ProcessNames` | Executables that indicate this format's games |
| `Games` | One `LegacyGame(display name, process)` per covered game — must cover every `ProcessNames` entry; this is what the GUI picker shows and how the active game is named (contract-tested) |
| `Options` | `--set key=value` knobs surfaced by `list <id>` |
| `Configure(options)` | Apply options once, before anything else |
| `Declare(session)` | Register the object, wheels, channels — becomes the DiscoveryFrame |
| `AcceptsDatagram(length)` | Cheap length/magic check on the receive thread |
| `Translate(datagram, session, now)` | Parse one packet, write the session, tick exactly once |
| `Transport` | `Udp` (default), `SharedMemory` + the map name/size/poll interval, or `SimConnect` (implement `ISimConnectSource`) |

**Latency rules for `Translate`** (it runs on the forwarder thread,
between the receive and send syscalls):

- no allocations — parse with `MemoryMarshal.Read<T>` or offset readers;
- no locks, no events, no cross-thread calls;
- finish with exactly one `session.Tick(now)` per forwarded packet;
- return without ticking for packets that carry nothing (menu gate,
  unchanged shared-memory page).

## A UDP source, end to end

`SmsSource` is the smallest real example (pCARS2/AMS2/pCARS3). The shape:

```csharp
public sealed class SmsSource : ILegacySource
{
    OgsChannel _rpm, _speed;                    // handles wired in Declare

    public string Id => "sms";
    public IReadOnlyList<string> ProcessNames { get; } = new[]
        { "pCARS2AVX", "AMS2AVX", "pCARS3" };
    public int DefaultPort => 5606;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var o = new SourceOptions(options);
        o.EnsureKnown("flipYaw", "flipPitch", "flipRoll", "accUnit");
        // pull values with o.GetBool/GetDouble/GetString defaults
    }

    public void Declare(OgsSession session)
    {
        session.DeclareObject("car", Ogs.Telemetry.Schema.ObjectType.Vehicle, "SMS-engine car");
        _rpm   = session.AddChannel("engine.rpm", "rpm", "Current engine rpm");
        _speed = session.AddChannel("vehicle.speed", "m/s", "Speed over ground");
        // keep discovery under ~1.4 kB — per-wheel data belongs in
        // WheelState drive points, not flat channels
    }

    public bool AcceptsDatagram(int length) => length >= 116;

    public void Translate(ReadOnlySpan<byte> d, OgsSession session, double now)
    {
        // static offset readers — spans cannot be captured in local functions
        session.SetPose(null, OgsMath.EulerToQuaternion(
            F(d, 56), F(d, 52), F(d, 60)));                  // yaw, pitch, roll
        session.SetBodyDynamics(
            new Vector3(F(d, 100), F(d, 104), F(d, 108)) * 9.81f,
            new Vector3(F(d, 88),  F(d, 92),  F(d, 96)));
        session.SetChannel(_rpm, U(d, 44));
        session.SetChannel(_speed, F(d, 40));
        session.Tick(now);                                   // exactly once
    }

    static float F(ReadOnlySpan<byte> d, int off) =>
        MemoryMarshal.Read<float>(d.Slice(off, 4));
}
```

Dynamics choice — send what the packet measures natively:

- world velocity + acceleration → `SetKinematics(v, a)` (WRC, EGO);
- local acceleration (accelerometer-style) + angular rates →
  `SetBodyDynamics(f, ω)` (SMS, F1, AC, LFS);
- exactly one per packet; never both.

Pose comes from `OgsMath.EulerToQuaternion(yaw, pitch, roll)` or, when
the packet gives world direction vectors, from
`OgsMath.EulerFromForwardRight(fwd, right)` + the quaternion (EGO, WRC).

### Where field layouts come from

Work from the game developer's published telemetry documentation
wherever it exists, and cite it in your source's doc comment. Where a
format is undocumented, ground-truth the layout against live packets
(see `EgoSource`'s dump diagnostic for the pattern).

## Axis conventions

OGS-TS: world is left-handed (+X right, +Y up, +Z forward), rotations
+pitch nose-up, +yaw nose-right, +roll right-side-down (CONVENTIONS
C-1/C-4). Helpers in `OgsMath`:

- `EulerToQuaternion(yaw, pitch, roll)` — locked by unit tests;
- `BeamNgWorldToOgs` — BeamNG's RH/Z-up world → OGS;
- `RhuYUpWorldToOgs` — the common RH/Y-up world (EGO, WRC) → OGS,
  applied as `(z, y, x)`;
- `EulerFromForwardRight` — Euler from world-space basis vectors.

**Unverified mappings**: any axis/sign assumption you could not check
against the live game must (a) be documented in the source's doc
comment and the README verification section, and (b) ship with a
`--set flipYaw/flipPitch/flipRoll=true` escape hatch that flips the
Euler axis *and* its matching angular rate together.

## Options

Expose per-source knobs through `Options` + `Configure`:
`o.EnsureKnown(...)` first (unknown keys throw — typos must not
silently no-op), then pull typed values with defaults. Conventions
already in use: `flipYaw/Pitch/Roll`, `euler=rad|deg`, `accUnit=g|ms2`,
`gravIncluded`, `wheelbase/track/radius` (placeholder wheel geometry).

## Shared-memory sources

For engines that write a memory-mapped page (Kunos `acpmf_physics`, …):

```csharp
public SourceTransport Transport => SourceTransport.SharedMemory;
public string SharedMemoryName => "Local\\acpmf_physics";
public int SharedMemorySize => 220;      // the prefix this source reads
public int PollIntervalMs => 2;          // < the game's write cadence
```

The supervisor then runs an `MmfForwarder` instead of the UDP one: it
polls the page on a dedicated thread and hands a snapshot to `Translate`.
Gate on the page's own change token so unchanged polls emit nothing:

```csharp
int packetId = MemoryMarshal.Read<int>(d);
if (packetId == _lastPacketId) return;   // no tick — nothing new
```

UDP is push (zero added latency); shared memory adds at most
`PollIntervalMs` (2 ms ≪ AC's 333 Hz physics write cadence).

## SimConnect sources

For Microsoft Flight Simulator (and anything else speaking SimConnect),
implement `ISimConnectSource` and return `SourceTransport.SimConnect`.
The source lists its variables instead of a byte layout:

```csharp
public IReadOnlyList<SimVar> FrameVars    // every sim frame, FLOAT64 each
public IReadOnlyList<SimVar> AircraftVars // once a second, on change
                                          // (Number, Text, or Xyz)
```

The supervisor runs a `SimConnectForwarder`: it loads `SimConnect.dll`
(the copy bundled in `lib/SimConnect`, or the `simconnect` option), opens
a client with an event handle, requests `FrameVars` per sim frame and
wakes when SimConnect signals data. Each frame reaches `Translate` as
packed doubles in `FrameVars` order, so the source reads them by index
(`MemoryMarshal.Cast<byte, double>`). A variable the sim does not know
(names and units differ between versions) is dropped from the definition
and reads `NaN`: skip `NaN` values rather than sending zeros. The
console prints which variables the sim rejected.

`OnAircraft` gets the aircraft block (title, engine count, geometry) and
may redeclare the session (`BeginRedeclare` → declare → `CommitRedeclare`)
when the aircraft changes. `OnSimState` reports pause and sim-running
events; `OnIdle` runs when a wait passes without data, so a paused sim
keeps streaming per CONVENTIONS C-11.

Large channel sets call `session.SendChangedChannelsOnly()` in
`Declare`: motion frames then carry only changed values (plus a full
refresh after each discovery), and `session.AlwaysSend(channel)` keeps a
channel like `session.paused` in every frame.

## Testing

Every source ships forged-packet tests (see
`tests/OgsTsBridge.Tests/TranslationTests.cs` and `SourcesBatchTests.cs`):

1. Forge a packet with known field values (`Fixtures` pattern — build a
   `byte[]`, write floats with `BitConverter`/`MemoryMarshal.Write`).
2. Run it through a capture-sink `OgsSession`
   (`new OgsSession((type, datagram) => captured.Add(...), gameName)`).
3. Decode the datagrams with `Envelope.TryUnwrap` + `DeserializerState`
   and assert the translated values — pose quaternion, dynamics union,
   channel values by discovery id, wheel states.

Add a real-UDP loopback test when the *input path* is new (a new
transport or datagram gate), not for every format — the loopback suite
already covers the UDP and MMF pipelines.

## Registration checklist

1. Create `src/OgsTsBridge.Core/Sources/<Format>Source.cs`.
2. Add it to `SourceRegistry.All()`.
3. `dotnet test` — your forged-packet tests must be green.
4. `dotnet run --project src/OgsTsBridge -- list` — verify the entry.
5. Add a row to the README sources table with game-side setup steps.
6. Document unverified axis assumptions (README verification section).

## Formats not yet covered (roadmap)

Formats still to cover:

- **GT7** — UDP with heartbeat registration and a bespoke encoding.
- **iRacing** — irsdk memory-mapped broadcast.
- **RaceRoom** — `$R3E` shared memory.
- **ETS2** — SDK shared memory (needs the user-side telemetry DLL
  installed into the game).
- **DCS** — export-script UDP (needs a Lua file shipped into the game).
- **X-Plane 11/12** — UDP dataref rows (needs per-user dataref config).
- **War Thunder / FSX / Farming Sim 19 / Fernbus** — HTTP/local
  APIs (FSX speaks SimConnect too).
- **Elite Dangerous** — journal/status files; **GTA5 / Star Wars
  Squadrons** — process memory reads.

Port priority: UDP formats first (event-driven, testable with forged
packets), then shared memory, then the rest.
