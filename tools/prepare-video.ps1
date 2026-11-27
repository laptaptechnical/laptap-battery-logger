# tools/prepare-video.ps1 — v16 §6: make assets/video/loop.webm from the owner's video (one-time, dev PC only).
# Never run by build.ps1 or release.ps1. The full runtime is kept ("Shrink into the exe without cutting the runtime"):
# HDR → SDR tone-mapped (bt.709), 640×360, 24 fps, two-pass VP9 in WebM with 96 kbps stereo Opus.
# Checks: duration = source ± 1 s; audio is present; size ≤ 18,000,000 B, else reduce video bitrate, up to 3 tries.
#   powershell -ExecutionPolicy Bypass -File tools\prepare-video.ps1 -Source "<path to the .mp4>"
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [ValidateSet('av1', 'vp9')][string]$Codec = 'vp9',
    [int]$Kbps = 96,
    [int]$AudioKbps = 96,
    [long]$MaxBytes = 18000000,
    [string]$Out
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Out) { $Out = Join-Path $repo 'assets\video\loop.webm' }
if (-not (Test-Path -LiteralPath $Source)) { throw "Source not found: $Source" }

$ff = Get-Command ffmpeg -ErrorAction SilentlyContinue
$fp = Get-Command ffprobe -ErrorAction SilentlyContinue
if (-not $ff -or -not $fp) {
    Write-Host 'ffmpeg is not on PATH. Install it (one time), then open a new PowerShell and run this again:'
    Write-Host '  winget install --id Gyan.FFmpeg --scope user --accept-source-agreements --accept-package-agreements'
    exit 2
}
$enc = if ($Codec -eq 'av1') { 'libaom-av1' } else { 'libvpx-vp9' }
$encoders = & $ff.Source -hide_banner -encoders 2>$null | Out-String
if ($encoders -notmatch [regex]::Escape($enc)) { throw "this ffmpeg has no $enc encoder (use the Gyan 'full' build)" }
$filters = & $ff.Source -hide_banner -filters 2>$null | Out-String
if ($filters -notmatch '\bzscale\b') { throw 'this ffmpeg has no zscale (zimg) filter (use the Gyan full build)' }

function Duration([string]$f) {
    $d = & $fp.Source -v error -show_entries format=duration -of default=nw=1:nk=1 "$f" 2>$null
    return [double]::Parse(($d | Select-Object -First 1).Trim(), [Globalization.CultureInfo]::InvariantCulture)
}
$srcDur = Duration $Source
$sourceAudio = & $fp.Source -v error -select_streams a:0 -show_entries stream=codec_name -of default=nw=1:nk=1 "$Source" 2>$null
if (-not $sourceAudio) { throw 'Source has no audio stream to preserve.' }
Write-Host ("source: {0} ({1:N1} s)" -f (Split-Path -Leaf $Source), $srcDur)

$vf = 'zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,scale=640:360:flags=lanczos,fps=24,format=yuv420p'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ('laptap-video-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$tmp = Join-Path $work 'loop.webm'
try {
    $rate = $Kbps
    for ($try = 1; $try -le 3; $try++) {
        Write-Host "encode try $try at $rate kbps ($enc, two-pass)"
        $common = @('-hide_banner', '-y', '-i', $Source, '-map', '0:v:0', '-map', '0:a:0',
            '-vf', $vf, '-c:v', $enc, '-b:v', "${rate}k", '-g', '240')
        if ($Codec -eq 'av1') { $common += @('-cpu-used', '6', '-row-mt', '1', '-tiles', '2x1') } else { $common += @('-row-mt', '1', '-deadline', 'good', '-cpu-used', '6') }
        $videoOnly = $common + @('-an')
        $withAudio = $common + @('-c:a', 'libopus', '-b:a', "${AudioKbps}k", '-ac', '2')
        Push-Location $work
        try {
            & $ff.Source @videoOnly -pass 1 -passlogfile (Join-Path $work 'pass') -f webm NUL
            if ($LASTEXITCODE -ne 0) { throw 'ffmpeg pass 1 failed' }
            & $ff.Source @withAudio -pass 2 -passlogfile (Join-Path $work 'pass') -f webm $tmp
            if ($LASTEXITCODE -ne 0) { throw 'ffmpeg pass 2 failed' }
        } finally { Pop-Location }
        $size = (Get-Item -LiteralPath $tmp).Length
        $dur = Duration $tmp
        if ([Math]::Abs($dur - $srcDur) -gt 1.0) { throw ("duration {0:N2} s differs from the source {1:N2} s by more than 1 s (the runtime must not be cut)" -f $dur, $srcDur) }
        if ($size -le $MaxBytes) { break }
        Write-Host "  $size bytes > $MaxBytes; retrying 10% lower"
        if ($try -eq 3) { throw "still $size bytes after 3 tries (gate $MaxBytes)" }
        $rate = [int][Math]::Floor($rate * 0.9)
    }
    $res = (& $fp.Source -v error -select_streams v:0 -show_entries stream=width,height,codec_name -of csv=p=0 "$tmp" | Select-Object -First 1)
    $audioRes = (& $fp.Source -v error -select_streams a:0 -show_entries stream=codec_name,channels -of csv=p=0 "$tmp" | Select-Object -First 1)
    if (-not $audioRes) { throw 'Encoded video has no audio stream.' }
    Move-Item -LiteralPath $tmp -Destination $Out -Force
    Write-Host ''
    Write-Host "OK  $Out"
    Write-Host ("  size     {0} bytes (gate {1})" -f (Get-Item -LiteralPath $Out).Length, $MaxBytes)
    Write-Host ("  sha256   {0}" -f (Get-FileHash -LiteralPath $Out -Algorithm SHA256).Hash.ToLowerInvariant())
    Write-Host ("  duration {0:N2} s (source {1:N2} s)" -f (Duration $Out), $srcDur)
    Write-Host ("  video    {0}" -f $res)
    Write-Host ("  audio    {0}" -f $audioRes)
} finally { Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction SilentlyContinue }
