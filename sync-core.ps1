# sync-core.ps1 — vendor the engine-agnostic C# core from ogsa-ts-dotnet
# (envelope, sender, unit conversions, canonical encoder + generated schema
# writers, conformance vectors; the receiver and publisher stay upstream —
# this app builds its own latency-first frames). The tests decode what the
# app sends, so they also get the receive side's Capnp.Net readers — test
# project only; the app ships no Cap'n Proto runtime.
# Upstream's MIT LICENSE travels with each vendored folder.
# Fetches upstream from GitHub; -Ref takes a branch, tag or full commit SHA.
# Run from the repo root: pwsh ./sync-core.ps1 [-Ref <ref>]
param([string]$Ref = 'main')
$ErrorActionPreference = 'Stop'
$repo = 'https://github.com/Open-Game-Standards-Alliance/ogsa-ts-dotnet.git'
$upstream = Join-Path ([IO.Path]::GetTempPath()) "ogsa-ts-dotnet-$([guid]::NewGuid().ToString('N'))"
try {
    git init -q $upstream
    git -C $upstream fetch -q --depth 1 $repo $Ref
    if ($LASTEXITCODE -ne 0) { throw "could not fetch '$Ref' from $repo" }
    git -C $upstream checkout -q FETCH_HEAD
    $commit = git -C $upstream rev-parse HEAD
    Write-Host "ogsa-ts-dotnet $Ref @ $commit"
    $src = "$upstream/src/Ogsa.Telemetry"
    $recv = "$upstream/src/Ogsa.Telemetry.Receiving"
    $changed = 0

    function Sync-Files([string[]]$from, [string]$dst) {
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        foreach ($f in $from) {
            $name = Split-Path $f -Leaf
            # compare ignoring CR: GitHub serves LF, checkouts may be CRLF
            if (-not (Test-Path "$dst/$name") -or
                ([IO.File]::ReadAllText($f) -replace "`r", '') -ne
                ([IO.File]::ReadAllText("$dst/$name") -replace "`r", '')) {
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
        "$src/Schema/ogsa_telemetry.writers.cs",
        "$upstream/LICENSE") "OgsaTelemetry"
    Sync-Files @(
        "$recv/MessageDecoder.cs",
        "$recv/Schema/ogsa_telemetry.capnp.cs",
        "$upstream/LICENSE") "tests/OgsaTsBridge.Tests/Vendored"
} finally {
    Remove-Item -Recurse -Force $upstream -ErrorAction SilentlyContinue
}
if ($changed -eq 0) { Write-Host "core up to date" }
exit 0
