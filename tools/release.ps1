# tools/release.ps1 <version> — make a signed release (plan §5.5). Owner-run, on the owner's PC.
#  1. refuses a dirty tree   2. writes VERSION (version, release + 1)   3. builds + tests (build.ps1)
#  4. writes + signs update.json   5. checks it with the new exe's --verify-release   6. commits "Release vX" + tag vX
#  7. asks "Push to GitHub now? (Y/N)"   8. opens the folder with the three files to upload.
# It never creates the GitHub release itself (that's the owner's drag-and-drop, HOW-TO-RELEASE.md).
# Test mode (the local end-to-end test only): -NoGit -KeyFile <test key> [-TestHooks] [-Release n] -OutDir <dir>:
# no git, VERSION untouched, signed with a throwaway test key.
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Version,
    [string]$KeyFile,
    [switch]$TestHooks,
    [switch]$NoGit,
    [int]$Release = 0,
    [string]$OutDir,
    [switch]$NoVideo     # v16 (M7): allow a release with no packaged video (never for the fleet)
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location -LiteralPath $repo
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must look like 15.0.1' }
# U3: the test switches can never make a real (committed, tagged) release.
if (($TestHooks -or $KeyFile -or $Release -gt 0 -or $OutDir) -and -not $NoGit) { throw '-TestHooks, -KeyFile, -Release and -OutDir are for the local test only (with -NoGit).' }
if (-not ('KeyTools' -as [type])) { Add-Type -Path (Join-Path $repo 'tools\KeyTools.cs') -ReferencedAssemblies System.Core }
Add-Type -AssemblyName System.Security
$keyDir = Join-Path $env:APPDATA 'Laptap\BatteryLogger'
# A failed run puts VERSION back, so the next run isn't refused for "uncommitted changes".
$restoreVersion = $false
trap { if ($restoreVersion) { git checkout -q -- VERSION 2>$null }; break }

# ---- 1-2. clean tree, VERSION ----
$vf = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $repo 'VERSION')) { if ($line -match '^\s*(\w+)\s*=\s*(\S+)\s*$') { $vf[$Matches[1]] = $Matches[2] } }
$bump = 1
if (-not $NoGit) {
    if (git status --porcelain) { throw 'The working tree has uncommitted changes. Commit or discard them first.' }
    $bf = Join-Path $keyDir 'signing.bump'
    if (Test-Path $bf) { $bump += [int](Get-Content $bf) }
}
$rel = if ($Release -gt 0) { $Release } else { [int]$vf['release'] + $bump }
if (-not $NoGit) {
    [IO.File]::WriteAllText((Join-Path $repo 'VERSION'), "version=$Version`nrelease=$rel`n", (New-Object Text.UTF8Encoding($false)))
    $restoreVersion = $true
}
$dir = if ($OutDir) { $OutDir } else { Join-Path $repo "release\v$Version" }
if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
New-Item -ItemType Directory -Force -Path $dir | Out-Null

# ---- v16 (M7): the packaged video is embedded by build.ps1; say which, and refuse a release without one ----
$video = $null
foreach ($v in @('assets\video\loop.webm', 'assets\video\loop.mp4')) { $p = Join-Path $repo $v; if (Test-Path -LiteralPath $p) { $video = $p; break } }
if (-not $video -and -not $NoVideo) { throw 'No packaged video (assets\video\loop.webm). Make it with tools\prepare-video.ps1, or pass -NoVideo.' }
if ($video) {
    $dur = 'n/a (ffprobe not found)'
    $fp = Get-Command ffprobe -ErrorAction SilentlyContinue
    if ($fp) { $d = & $fp.Source -v error -show_entries format=duration -of default=nw=1:nk=1 "$video" 2>$null; if ($d) { $dur = ($d | Select-Object -First 1).Trim() + ' s' } }
    Write-Host ('video: ' + (Split-Path -Leaf $video) + ', ' + (Get-Item -LiteralPath $video).Length + ' bytes, sha256 ' + (Get-FileHash -LiteralPath $video -Algorithm SHA256).Hash.ToLowerInvariant() + ', ' + $dur)
} else { Write-Host 'video: none (-NoVideo)' }

# ---- 3. build + test ----
$build = Join-Path $dir 'build'
$bargs = @{ Out = $build; Version = $Version; Release = $rel }
if ($TestHooks) { $bargs.TestHooks = $true; $bargs.SkipTests = $true }
& (Join-Path $repo 'build.ps1') @bargs
$asset = Join-Path $dir 'Laptap-Battery-Logger.exe'
Move-Item -LiteralPath (Join-Path $build 'Laptap Battery Logger.exe') -Destination $asset
Remove-Item -Recurse -Force $build

# U3: a real release is scanned again for test-hook text, whatever build.ps1 did.
if (-not $NoGit) {
    $raw = [IO.File]::ReadAllBytes($asset)
    $latin = [Text.Encoding]::GetEncoding(28591).GetString($raw)
    $utf16 = [Text.Encoding]::Unicode.GetString($raw) + [Text.Encoding]::Unicode.GetString($raw, 1, $raw.Length - 1)
    foreach ($needle in @('LAPTAP_BL_TEST_BASE', 'LAPTAP_BL_TEST_PUBKEYS', 'LAPTAP_BL_TEST_NS', '/api/start(?!-anyway)')) {   # v16: /api/start-anyway is a real route
        if ([regex]::IsMatch($latin, $needle) -or [regex]::IsMatch($utf16, $needle)) { throw "The release exe contains test-hook text ($needle). Nothing was signed." }
    }
}
# U1: the new exe must start its engine, server and page (a broken release would strand every laptop).
$st = Start-Process -FilePath $asset -ArgumentList '--self-test' -Wait -PassThru -WindowStyle Hidden
if ($st.ExitCode -ne 0) { throw "--self-test of the new exe failed (exit $($st.ExitCode)). Nothing was signed." }
Write-Host 'self-test ok'

# ---- 4. update.json + signature ----
$sha = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $asset).Length
$published = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
$json = "{ `"format`": 1, `"product`": `"laptap-battery-logger`", `"release`": $rel, `"version`": `"$Version`", `"tag`": `"v$Version`",`n" +
        "  `"published`": `"$published`",`n  `"exe`": { `"asset`": `"Laptap-Battery-Logger.exe`", `"sha256`": `"$sha`", `"size`": $size } }`n"
$bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json)
[IO.File]::WriteAllBytes((Join-Path $dir 'update.json'), $bytes)

if ($KeyFile) {
    $priv = [KeyTools]::FromTestKeyFile([IO.File]::ReadAllBytes($KeyFile))   # test keys only
} elseif (Test-Path (Join-Path $keyDir 'signing.use-backup')) {
    $bk = (Get-Content (Join-Path $keyDir 'signing.use-backup')).Trim()
    $s = Read-Host 'Passphrase of the BACKUP key (this release is signed with it)' -AsSecureString
    $pass = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s))
    $priv = [KeyTools]::UnprotectBackup([IO.File]::ReadAllBytes($bk), $pass)
} else {
    $pf = Join-Path $keyDir 'signing.key'
    if (-not (Test-Path $pf)) { throw "No signing key on this PC ($pf). Run tools\keys.ps1 new first." }
    $priv = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($pf), [Text.Encoding]::ASCII.GetBytes('laptap-battery-logger-signing'), 'CurrentUser')
}
$sig = [Convert]::ToBase64String([KeyTools]::Sign($priv, $bytes))
[IO.File]::WriteAllText((Join-Path $dir 'update.json.sig'), $sig, [Text.Encoding]::ASCII)

# ---- 5. verify ----
function Get-KeyPairs([string]$text) {
    [regex]::Matches($text, 'new string\[\]\s*\{\s*"([0-9a-fA-F]{64})"\s*,\s*"([0-9a-fA-F]{64})"\s*\}') | ForEach-Object { ($_.Groups[1].Value + ':' + $_.Groups[2].Value).ToLowerInvariant() }
}
if ($NoGit) {
    # Test mode: the TESTHOOKS exe trusts the test key from the environment.
    if ($KeyFile) { $env:LAPTAP_BL_TEST_PUBKEYS = [KeyTools]::Hex([KeyTools]::PublicX($priv)) + ':' + [KeyTools]::Hex([KeyTools]::PublicY($priv)) }
    $p = Start-Process -FilePath $asset -ArgumentList @('--verify-release', "`"$dir`"") -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { throw "--verify-release failed (exit $($p.ExitCode))." }
    Write-Host "verified: v$Version, release $rel"
    Write-Host "Release files in $dir"; return
}
# U2 (a): the laptops in the field must accept this signature: check it against the keys of the last released version.
$current = @(Get-KeyPairs (Get-Content -LiteralPath (Join-Path $repo 'src\UpdateKeys.cs') -Raw))
$prevTag = (git tag --list 'v*' --sort=-v:refname | Select-Object -First 1)
if ($prevTag) { $field = @(Get-KeyPairs ((git show "${prevTag}:src/UpdateKeys.cs") -join "`n")); $from = "the keys in $prevTag" }
else { $field = $current; $from = 'this build''s keys (first release)' }
if ($field.Count -eq 0) { throw "No public keys found in $from." }
$fk = Join-Path $env:TEMP ('laptap-field-keys-' + [guid]::NewGuid().ToString('N') + '.txt')
[IO.File]::WriteAllLines($fk, $field)
try {
    $p = Start-Process -FilePath $asset -ArgumentList @('--verify-release', "`"$dir`"", '--keys-file', "`"$fk`"") -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { throw "--verify-release failed against $from (exit $($p.ExitCode)). The laptops in the field would refuse this update." }
} finally { Remove-Item -LiteralPath $fk -ErrorAction SilentlyContinue }
Write-Host "verified against ${from}: v$Version, release $rel"
# U2 (b): the key that will sign the NEXT release must be in this exe, or the fleet is stranded after this one.
$nextSigner = Join-Path $keyDir 'signing.next.key'
if (-not (Test-Path $nextSigner)) { $nextSigner = Join-Path $keyDir 'signing.key' }
$np = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($nextSigner), [Text.Encoding]::ASCII.GetBytes('laptap-battery-logger-signing'), 'CurrentUser')
$npKey = ([KeyTools]::Hex([KeyTools]::PublicX($np)) + ':' + [KeyTools]::Hex([KeyTools]::PublicY($np))).ToLowerInvariant()
if ($current -notcontains $npKey) { throw "The key that will sign the next release is not in src\UpdateKeys.cs. Paste the lines tools\keys.ps1 printed, then run this again." }
Write-Host 'the next release''s signing key is in this exe'

# ---- 6-8. commit + tag locally, ask before pushing, open the folder ----
git add VERSION
git commit -m "Release v$Version"
if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
$restoreVersion = $false
git tag "v$Version"
$next = Join-Path $keyDir 'signing.next.key'
if (Test-Path $next) {   # planned rotation: the new key takes over after this release
    Move-Item (Join-Path $keyDir 'signing.key') (Join-Path $keyDir ('signing.retired-' + (Get-Date -Format yyyyMMddHHmm) + '.key'))
    Move-Item $next (Join-Path $keyDir 'signing.key')
}
foreach ($f in 'signing.use-backup', 'signing.bump') { $x = Join-Path $keyDir $f; if (Test-Path $x) { Remove-Item $x } }
$a = Read-Host 'Push to GitHub now? (Y/N)'
if ($a -match '^[Yy]') { git push origin HEAD; git push origin "refs/tags/v$Version" }   # full ref: a branch may share the tag name
else { Write-Host "Not pushed. Later: git push origin HEAD; git push origin refs/tags/v$Version" }
Start-Process explorer.exe -ArgumentList "`"$dir`""
