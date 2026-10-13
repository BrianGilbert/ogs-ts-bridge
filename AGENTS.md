# OGSA-TS Bridge working conventions

- Latency is the first-class requirement. Nothing that sits on the
  packet path may allocate, lock, or hop threads: parse with
  `MemoryMarshal.Read`, write into cached frame graphs, send from the
  receive thread. If a change adds work between the receive syscall and
  the send syscall, it needs a strong reason.
- Output cadence equals input cadence (one motion frame per accepted
  packet, `--max-hz` is a guardrail, not a target).
- The vendored `OgsaTelemetry/` core comes from ogsa-ts-dotnet via
  `sync-core.ps1` — never hand-edit it; run the script after pulling
  upstream. App-specific behaviour belongs in `OgsaSession`, not the core.
- Discovery datagrams must stay under ~1.4 kB (one Ethernet MTU).
  Wheel-level data rides `WheelState` drive points, not flat channels.
- Channel names follow the standard's Common Channels Annex where
  ratified; anything else gets a game-prefixed name.
- Game detection is process-name based — never
  probe telemetry ports to guess which game is running, and never depend
  on Steam.
- The GUI (and any future UI) is snapshot-driven: read stats/latency at
  display cadence (≤ a few Hz), issue commands only through
  EngineHost/AutoSupervisor, never add a lock or allocation the
  forwarder thread would take. UI work must not add code between the
  receive and send syscalls.
- Axis/sign mappings not yet verified against the live game are
  documented in the README's verification section with a `--set flip*`
  escape hatch. Update that section when you verify.
- Tests: `dotnet test` must stay green; the loopback tests forge packets
  and assert both values and 1:1 input:output cadence over real UDP.
  Mind port collisions with anything running locally (tests use
  44544/44545 in, 48123/48124 out, group 239.255.17.99).
- Known Windows quirk: a sender's first multicast datagrams can be
  dropped by stack warm-up — that's why `Start` sends discovery twice
  and the tests tolerate it.
