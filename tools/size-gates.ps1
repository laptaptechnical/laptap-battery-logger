# tools/size-gates.ps1 — v16 §6 (I1, I2): the build's size gates, in one place (build.ps1 calls it; tests call it too).
#   video ≤ 4,500,000 B (the packaged loop.webm / loop.mp4, if any)
#   exe   ≤ 4,900,000 B (under MaxExeBytes = 5,242,880, which every v15.x laptop enforces)
# Throws on the first failing gate; prints the sizes otherwise.
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Video,
    [long]$MaxVideo = 4500000,
    [long]$MaxExe = 4900000
)
$ErrorActionPreference = 'Stop'
if ($Video) {
    $v = (Get-Item -LiteralPath $Video).Length
    if ($v -gt $MaxVideo) { throw "video is $v bytes (gate: <= $MaxVideo)" }
    Write-Host "video $(Split-Path -Leaf $Video) = $v bytes (gate <= $MaxVideo)"
} else { Write-Host 'no packaged video (the player shows the logo still + Open file)' }
$e = (Get-Item -LiteralPath $Exe).Length
if ($e -gt $MaxExe) { throw "exe is $e bytes (gate: <= $MaxExe)" }
Write-Host "exe $(Split-Path -Leaf $Exe) = $e bytes (gate <= $MaxExe)"
