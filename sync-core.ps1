# sync-core.ps1 — vendor the engine-agnostic C# core from ogsa-ts-dotnet
# (envelope, sender, unit conversions, canonical encoder + generated schema
# writers, conformance vectors; the receiver and publisher stay upstream —
# this app builds its own latency-first frames). The tests decode what the
# app sends, so they also get the receive side's Capnp.Net readers — test
# project only; the app ships no Cap'n Proto runtime.
# Run from the repo root after pulling upstream changes.
$ErrorActionPreference = 'Stop'
$src = "../ogsa-ts-dotnet/src/Ogsa.Telemetry"
$recv = "../ogsa-ts-dotnet/src/Ogsa.Telemetry.Receiving"
if (-not (Test-Path "$src/Envelope.cs")) {
    throw "source not found: $src (is ogsa-ts-dotnet checked out as a sibling?)"
}
$changed = 0

function Sync-Files([string[]]$from, [string]$dst) {
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    foreach ($f in $from) {
        $name = Split-Path $f -Leaf
        if (-not (Test-Path "$dst/$name") -or
            (Get-FileHash $f).Hash -ne (Get-FileHash "$dst/$name").Hash) {
            Copy-Item $f "$dst/$name"
            Write-Host "updated: $dst/$name"
            $script:changed = 1
        }
    }
    # vendored files no longer in the list are removed
    $names = $from | ForEach-Object { Split-Path $_ -Leaf }
    foreach ($f in Get-ChildItem "$dst/*.cs") {
        if ($names -notcontains $f.Name) {
            Remove-Item $f.FullName
            Write-Host "removed: $dst/$($f.Name)"
            $script:changed = 1
        }
    }
}

Sync-Files @(
    "$src/Envelope.cs", "$src/TelemetrySender.cs", "$src/Unit.cs",
    "$src/Conversions.cs", "$src/Wire.cs", "$src/ConformanceVectors.cs",
    "$src/Schema/ogsa_telemetry.writers.cs") "OgsaTelemetry"
Sync-Files @(
    "$recv/MessageDecoder.cs",
    "$recv/Schema/ogsa_telemetry.capnp.cs") "tests/OgsaTsBridge.Tests/Vendored"

if ($changed -eq 0) { Write-Host "core up to date" }
exit 0
