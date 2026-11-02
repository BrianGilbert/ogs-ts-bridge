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
        // per-wheel data belongs in WheelState drive points, not flat
        // channels; the session pages discovery and splits large ticks
        // to keep every datagram within the 1,400-byte budget
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

When one layout is published under several map names (AC EVO writes
the acpmf page as `Local\acevo_pmf_physics`), list the others in
`SharedMemoryAlternateNames`; the forwarder tries them in order.

Pages too large to copy whole, or data split across maps, implement
`ISharedMemoryGather` (`Rf2Source`): the forwarder maps the primary page
whole, opens each of `AuxiliaryMapNames` when it appears (retrying every
~0.25 s), and calls `Gather` to copy only what `Translate` needs into the
snapshot buffer — the rF2 source copies the player's 1.9 kB vehicle slot
out of a ~240 kB telemetry array, with the slot found from the scoring
map. `Gather` returns 0 for "nothing new" (unchanged version, torn
copy), and must not allocate.

## Polled sources

Games that answer requests instead of pushing (a local HTTP or TCP
server) or expose only their process memory implement `IPolledSource`
and return `SourceTransport.Poll`:

```csharp
public SourceTransport Transport => SourceTransport.Poll;
public int PollIntervalMs => 20;
public int PollBufferSize => 1 << 17;
public string PollTarget => "War Thunder on 127.0.0.1:8111"; // shown while waiting
public bool Connect() => _http.Connect();   // cold; false = not up yet
public int Read(byte[] buffer) => …;        // > 0 sample, 0 nothing new, −1 gone
public void Disconnect() => _http.Close();
```

The poll forwarder retries `Connect` every ~0.5 s (keeping discovery
alive meanwhile), then calls `Read` every `PollIntervalMs` and hands each
sample to `Translate`; a −1 disconnects and reconnects. `Read` is the
packet path: keep one connection and reuse the buffers (`LocalHttp`
does this for HTTP: keep-alive, prebuilt requests, Content-Length and
chunked bodies).

## UDP extras: multicast groups and heartbeats

A game that sends to a multicast group sets `MulticastGroup` (Project
Motor Racing: `224.0.0.150`); the input socket joins it after binding. A
game that streams only while it hears from the receiver implements
`IUdpHeartbeat` (GT7): the forwarder sends `HeartbeatPayload` from the
input port to `HeartbeatTarget` at start and every
`HeartbeatIntervalSeconds`, so replies come back to the port it listens
on.

## Text formats and undocumented frames

`JsonChannels` maps a JSON document (a UDP datagram or an HTTP reply) to
channels without allocating once its keys are known: nested objects and
arrays flatten to dotted paths, known keys get standard names and unit
factors (`JsonKey`, or a resolver for keys that carry their unit), every
other key is published raw as `<prefix>.<path>`, and a key not seen
before makes `Read` return true so the source redeclares. `Watch` gives
the source the values it maps by hand (pose, dynamics).

When a format does not document its frame, take an `axes` option and an
`AxisMap` ("z,y,-x": the game component feeding each OGS axis). It maps
vectors, angular velocities and quaternions consistently, handedness
changes included, so a live check fixes the frame without code.

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
`Declare`: motion frames then carry only changed values (plus a paced
refresh that re-sends every value within each discovery interval), and
`session.AlwaysSend(channel)` keeps a channel like `session.paused` in
every frame. Whatever the channel count, the session keeps every datagram
within the 1,400-byte budget: discovery is paged and a large tick goes
out in parts. Text that changes rarely (track, vehicle, driver) rides
session text channels (`AddSessionText`): a change re-sends only the
discovery page carrying it. A vehicle swap that changes the descriptors
goes through `BeginRedeclare`/`CommitRedeclare`, which starts a new
generation; `SetTypeLabel` and `SetLocation` do too.

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

Games with no loader-mod route, still to cover:

- **Aerofly FS 2 / 4, GTA 5, Farming Simulator 19/22/25** — they emit
  telemetry only through a game-side add-on (an Aerofly external DLL, a
  GTA 5 ScriptHookV script, an FS Lua mod). The existing ones are closed
  binaries that cannot be redistributed; each needs an add-on of our own
  (a separate project) before a source makes sense.
- **X-Wing Alliance** — the X-Wing Alliance Upgrade's CockpitLook JSON
  UDP (port 1138); waits on an XWAU install to confirm the key set and
  the units and signs of its attitude and inertia values.
- **FSX: Steam Edition** — on the `msfs` watch list, unverified: FSX is
  32-bit and the bundled client is the MSFS SimConnect.dll; if it cannot
  talk to FSX, a small x86 SimConnect relay is the fallback.

Engine titles the loader mods cover (bepinex-ogs-ts for Unity,
ue4ss-ogs-ts / uevr-ogs-ts for Unreal) publish motion without a Bridge
source; a native source is still worth it where the game's own output
carries more than motion:

- **KartKraft** (UE4) — FlatBuffers motion UDP.
- **Fernbus Simulator** (UE4) — local REST API (`/Vehicles/Current`, port 37337).
- **G-Rebels, Kayak VR: Mirage, Whitewater VR** (UE) — likely one format
  ("Motion Platform Linker", UDP 33001); confirm, then one source.
- **Distance, New Star GP** (Unity, UDP 12345) — check for a shared
  format first.
- **Unity outputs**: 1976 Back to Midway, GravitreX Arcade, HyperStorm,
  Vector 36, Fly Dangerous, Overload, VTOL VR (mod loader), Helicopter
  Simulator 2021, Touring Karts, VRace Hover Bike, Warplanes WW1,
  Subnautica.
- **Unreal outputs**: Ace Combat 7, MotoGP 18, Project Wingman, Redout,
  Iron Rebellion (UDP 6969).

Not planned: Star Wars: Squadrons (process-memory reads in an online
title).

Port priority: UDP formats first (event-driven, testable with forged
packets), then shared memory, then the rest.
