# sync-core.ps1 — vendor the engine-agnostic C# core from ogsa-ts-dotnet
# (envelope, sender, unit conversions, generated schema; the receiver and
# publisher stay upstream — this app builds its own latency-first frames).
# Run from the repo root after pulling upstream changes.
$ErrorActionPreference = 'Stop'
$src = "../ogsa-ts-dotnet/src/Ogsa.Telemetry"
$dst = "OgsaTelemetry"
if (-not (Test-Path "$src/Envelope.cs")) {
    throw "source not found: $src (is ogsa-ts-dotnet checked out as a sibling?)"
}
$keep = @('Envelope.cs', 'TelemetrySender.cs', 'Unit.cs', 'Conversions.cs')
$changed = 0
foreach ($name in $keep) {
    $from = Join-Path $src $name
    if (-not (Test-Path "$dst/$name") -or
        (Get-FileHash $from).Hash -ne (Get-FileHash "$dst/$name").Hash) {
        Copy-Item $from "$dst/$name"
        Write-Host "updated: $dst/$name"
        $changed = 1
    }
}
$schema = "$src/Schema/ogsa_telemetry.capnp.cs"
if ((Get-FileHash $schema).Hash -ne (Get-FileHash "$dst/ogsa_telemetry.capnp.cs").Hash) {
    Copy-Item $schema "$dst/ogsa_telemetry.capnp.cs"
    Write-Host "updated: $dst/ogsa_telemetry.capnp.cs"
    $changed = 1
}
if ($changed -eq 0) { Write-Host "core up to date" }
exit 0
