# OGSA-TS Bridge

Translate telemetry from games that don't support the
[OGSA Telemetry Standard](https://github.com/Open-Game-Standards-Alliance/OGSA-Telemetry-Standard)
and retransmit it as **OGSA-TS UDP** (OMT1 / Cap'n Proto, multicast) — so
motion rigs, dashboards, and haptics built against the standard can consume
any game, natively.

Design priority #1: **lowest possible latency** on the forward path. Every
legacy packet is parsed, translated, and on the wire within the same
syscall-to-syscall span — no timers, no queues, no cross-thread hops.
Typical measured cost (parse → encode → send) is **tens of microseconds**,
invisible next to a game physics tick.

```
game (legacy telemetry)                     any OGSA-TS receiver
   │ UDP, its own format                        ▲
   ▼                                            │ UDP multicast 239.255.17.17:40123
┌────────────────────────────────────────────────┴──────────────┐
│ ogsa-ts-bridge                                                │
│  receive thread ─ zero-copy parse ─ cached-graph encode ─ send │
│  (process watcher picks the source, cold path, 2 s cadence)   │
└───────────────────────────────────────────────────────────────┘
```

Community & support: [Discord](https://discord.gg/tseF4Q6U99)

## Quick start

```bash
dotnet build
dotnet run --project src/OgsaTsBridge -- run
```

With no further arguments the app runs in **auto mode**: it watches for
known game executables, matching plain process names, and activates the matching source
when a game launches, stopping it when the game exits. No telemetry
probing, no Steam dependency; non-Steam games work identically.

```
11:15:55 ForzaHorizon5 running - forwarding Forza (Sled format) (0.0.0.0:5300 -> 239.255.17.17:40123)
in    60/s  out    60/s  rej 0  |  fwd us p50      61  p95     94  p99    120  max    210
11:16:11 ForzaHorizon5 exited - source stopped, watching again
```

The status line's `fwd us` percentiles measure this app's own processing
(receive-syscall return → send-syscall return) — the only latency it adds
to the chain.

Pin a source manually with `--source`:

```bash
dotnet run --project src/OgsaTsBridge -- run --source beamng
ogsa-ts-bridge list          # source catalogue, watched processes, options
ogsa-ts-bridge list beamng   # one source's options (--set key=value)
```

| Option | Default | Meaning |
|---|---|---|
| `--source <id>` | auto | pin one source, skip process watching |
| `--in-port <n>` | source default | UDP port the game sends to |
| `--out <ip:port>` | `239.255.17.17:40123` | OGSA-TS endpoint (multicast or unicast) |
| `--ttl <n>` | 1 | multicast scope (1 = local subnet) |
| `--max-hz <n>` | 1000 | motion frame cap; below it, output cadence = input cadence |
| `--priority <p>` | above | forwarder thread priority (`normal`/`above`/`high`) |
| `--no-stats` | off | silence the 1 Hz status line |

## Sources

| Source | Game(s) | Input | Object payload |
|---|---|---|---|
| `beamng` | BeamNG.drive | 88-byte UDP (port 4444), telemetry output ON in Options → Gameplay | pose + world kinematics |
| `forza-sled` | Forza Horizon 4/5, Forza Motorsport 7+ | 232-byte "Sled" UDP (port 5300), Settings → Data Out → this machine, packet type Sled | pose + body dynamics + 4 wheel drive points + channels |

More sources welcome — see
[Adding a source](#adding-a-source).

Channel mapping follows the standard's [Common Channels Annex] where a
concept is ratified (`engine.rpm`, `vehicle.speed`, …); wheel-level data
rides schema `WheelState` drive points rather than flat channels, keeping
discovery well under one Ethernet MTU.

## Latency design notes

The whole point of this app is to not be visible in the latency budget:

- **One thread owns the path.** A dedicated, above-normal-priority thread
  blocks in `ReceiveFrom`; the kernel wakes it the instant a datagram
  lands. From there it parses, translates, encodes, and `SendTo`s on the
  same thread — zero hand-offs.
- **Pass-through cadence.** Every accepted input packet produces exactly
  one motion frame (capped by `--max-hz` as a guardrail). No rate
  quantisation, no batching delay. The loopback tests assert 1:1
  input:output.
- **Zero-allocation parse.** `MemoryMarshal.Read<T>` over the receive
  buffer — no per-packet marshalling, no struct copies to the heap.
- **Cached wire graphs.** The Cap'n Proto object graph is built once at
  declaration and rewritten in place each tick (the upstream
  `TelemetryPublisher` technique); the only per-frame allocation is one
  small message buffer.
- **Windows traps handled.** `SIO_UDP_CONNRESET` disabled (ICMP
  port-unreachable replies must not stall receives), `SIO_LOOPBACK_FAST_PATH`
  on loopback binds, 1 MiB receive buffer so bursts queue in the kernel
  instead of dropping (a drop costs a full source interval; buffering
  costs microseconds).
- **Discovery double-send + keepalive.** The sender's first multicast
  datagrams can be dropped by stack warm-up (observed on Windows);
  `Start` sends discovery twice, and a 500 ms receive timeout drives the
  standard's 2 s discovery re-send even when the game stream pauses
  (e.g. Forza menus), so late-joining receivers never wait longer than
  the standard's cadence.

Receiver-side liveness follows the standard: motion frames are the
heartbeat; ~1 s of silence (5× a 60 Hz tick) marks the session stale.

## Axis conventions & verification

OGSA-TS world frame is left-handed (+X right, +Y up, +Z forward); body
rotations: +pitch nose-up, +yaw nose-right, +roll right-side-down
(CONVENTIONS C-1/C-4). `OgsaMath.EulerToQuaternion` implements exactly
this and is locked by unit tests against the C-4 definitions.

Source mappings that are **derived from the format docs, not yet verified against the live games**:

- `beamng` — world vectors mapped `(x, y, z) → (−y, z, x)` (RH Z-up →
  LH Y-up); Euler assumed radians with direct sign mapping.
- `forza-sled` — local vectors match OGSA body axes 1:1 per the Sled
  docs; Euler assumed radians (`--set euler=deg` if a title disagrees);
  Forza's local acceleration is treated as accelerometer-style specific
  force.

Per-axis escape hatches exist for live verification
(`--set flipYaw/flipPitch/flipRoll=true` flips an Euler axis *and* its
angular rate consistently). To verify with a real game:

1. Drive slowly in a straight line — surge should be forward (+z).
2. Brake gently — nose should dip (pitch), decelerate (−z surge).
3. Bank left — roll right-side-down sign check.
4. Sit stationary — dashboards should show ~1 g up (specific force
   includes gravity reaction); if a game reports gravity-free linear
   acceleration instead, note it in this file.

## Layout

| Path | Contents |
|---|---|
| `src/OgsaTsBridge` | The app: `Program` (CLI, auto supervisor), `Forwarder` (hot path), `OgsaSession` (frame writer), `GameProcessWatcher`, `UdpInputSocket`, `LatencyMeter`, `OgsaMath`, `Sources/*` |
| `OgsaTelemetry/` | Vendored engine-agnostic core from [ogsa-ts-dotnet] (envelope, UDP sender, units, generated schema) — `sync-core.ps1` refreshes it |
| `tests/OgsaTsBridge.Tests` | xunit: Euler-convention locks, wire-level translation decode checks, and real-UDP loopback tests that forge legacy packets and assert the OGSA-TS output (values *and* 1:1 cadence) |

## Adding a source

1. Create `src/OgsaTsBridge/Sources/YourGameSource.cs` implementing
   `ILegacySource`: process names to watch, the input port, a
   `[StructLayout]` packet struct mirroring the game's format, options
   (`Options` + `Configure`), `Declare` (object, wheels, channels — keep
   discovery under ~1.4 kB), and `Translate` (zero-allocation parse →
   `SetPose` / `SetKinematics` / `SetBodyDynamics` / `SetWheel` /
   `SetChannel` → exactly one `session.Tick(now)`).
2. Register it in `SourceRegistry.All()`.
3. Add a forged-packet test (see `Fixtures` + `TranslationTests`) and a
   loopback test if the input path is new.
4. Document the game-side setup and any unverified axis assumptions in
   this README.


## Development

```bash
dotnet build
dotnet test
pwsh ./sync-core.ps1   # re-vendor the core after ogsa-ts-dotnet changes
```

## License

Source-available under the [PolyForm Shield License 1.0.0](LICENSE): you
can use, study, modify and share the code, and contributions are welcome,
but you can't use it to build a product that competes with OGSA-TS Bridge,
including a free one. The OGSA-TS core vendored from [ogsa-ts-dotnet] stays
MIT licensed, as do the standard and its SDKs.

Wire compatibility follows the standard's evolution rules
(unknown fields/frame types are skipped by receivers).

[Common Channels Annex]: https://github.com/Open-Game-Standards-Alliance/OGSA-Telemetry-Standard/blob/main/CHANNELS.md
[ogsa-ts-dotnet]: https://github.com/Open-Game-Standards-Alliance/ogsa-ts-dotnet
