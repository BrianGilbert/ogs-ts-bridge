# sync-core.ps1 — vendor the engine-agnostic C# core from ogs-ts-dotnet
# (envelope, sender, unit conversions, canonical encoder + generated schema
# writers, conformance vectors; the receiver and publisher stay upstream —
# this app builds its own latency-first frames). The tests decode what the
# app sends, so they also get the receive side's Capnp.Net readers — test
# project only; the app ships no Cap'n Proto runtime — and the standard's
# conformance corpus, which CorpusVectorTests checks the core against.
# Each upstream's MIT LICENSE travels with the folders vendored from it.
# Fetches both repos from GitHub; -Ref (ogs-ts-dotnet) and -StandardRef
# (OGS-Telemetry-Standard) take a branch, tag or full commit SHA.
# Run from the repo root: pwsh ./sync-core.ps1 [-Ref <ref>] [-StandardRef <ref>]
param([string]$Ref = 'main', [string]$StandardRef = 'main')
$ErrorActionPreference = 'Stop'
$org = 'https://github.com/Open-Game-Standards'
$changed = 0
$fetched = @()

function Get-Upstream([string]$name, [string]$ref) {
    $dir = Join-Path ([IO.Path]::GetTempPath()) "$name-$([guid]::NewGuid().ToString('N'))"
    $script:fetched += $dir
    git init -q $dir
    git -C $dir fetch -q --depth 1 "$org/$name.git" $ref
    if ($LASTEXITCODE -ne 0) { throw "could not fetch '$ref' from $org/$name" }
    git -C $dir checkout -q FETCH_HEAD
    Write-Host "$name $ref @ $(git -C $dir rev-parse HEAD)"
    return $dir
}

# Copies $from into $dst; files in $dst matching $prune that are no longer
# in the list are removed.
function Sync-Files([string[]]$from, [string]$dst, [string]$prune = '*.cs') {
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
    $names = $from | ForEach-Object { Split-Path $_ -Leaf }
    foreach ($f in Get-ChildItem "$dst/$prune") {
        if ($names -notcontains $f.Name) {
            Remove-Item $f.FullName
            Write-Host "removed: $dst/$($f.Name)"
            $script:changed = 1
        }
    }
}

try {
    $dotnet = Get-Upstream 'ogs-ts-dotnet' $Ref
    $standard = Get-Upstream 'OGS-Telemetry-Standard' $StandardRef
    $src = "$dotnet/src/Ogs.Telemetry"
    $recv = "$dotnet/src/Ogs.Telemetry.Receiving"

    Sync-Files @(
        "$src/Envelope.cs", "$src/TelemetrySender.cs", "$src/Unit.cs",
        "$src/Conversions.cs", "$src/Wire.cs", "$src/ConformanceVectors.cs",
        "$src/Schema/ogs_telemetry.writers.cs",
        "$dotnet/LICENSE") "OgsTelemetry"
    Sync-Files @(
        "$recv/MessageDecoder.cs",
        "$recv/Schema/ogs_telemetry.capnp.cs",
        "$dotnet/LICENSE") "tests/OgsTsBridge.Tests/Vendored"
    Sync-Files @("$standard/LICENSE") "tests/OgsTsBridge.Tests/Vendored/conformance" 'LICENSE'
    Sync-Files @(Get-ChildItem "$standard/conformance/vectors/*.hex" | ForEach-Object FullName) `
        "tests/OgsTsBridge.Tests/Vendored/conformance/vectors" '*.hex'
} finally {
    foreach ($d in $fetched) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
}

if ($changed -eq 0) { Write-Host "core up to date" }
exit 0
