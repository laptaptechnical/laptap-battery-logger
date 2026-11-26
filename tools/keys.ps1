# tools/keys.ps1 — the owner's signing keys (plan §5.3). Run on the OWNER's PC only.
#   new          first time: make the primary key (DPAPI, this Windows user) + the backup key (passphrase file you save
#                on the office-safe USB). Prints the C# for src/UpdateKeys.cs.
#   rotate       planned change of the primary: makes the next key. The next release is signed with the current key
#                and carries {next, backup}; after that release the next key takes over (release.ps1 does it).
#   recover      primary LOST (Windows profile reset, PC died): unlock the backup, make a new primary. The next release
#                is signed with the BACKUP and carries {backup, new primary}.
#   stolen       primary STOLEN: as recover, and the next release jumps its release number by 500.
#   roll-backup  make a fresh backup key (after a recover). The next release carries {primary, new backup}.
#   public       print the C# for the current keys again.
#   test-new     tests only: a throwaway plain key file (-Out) for the local end-to-end test. Never for a release.
# Private keys never go into the repo. Public keys are safe to share.
param(
    [Parameter(Mandatory = $true)][ValidateSet('new', 'rotate', 'recover', 'stolen', 'roll-backup', 'public', 'test-new')][string]$Action,
    [string]$Out,
    # Tests only (honoured only when $env:LAPTAP_KEYS_TEST is '1'): no prompts, no file dialogs.
    [string]$TestPassphrase,
    [string]$TestBackupFile
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Add-Type -AssemblyName System.Security
if (-not ('KeyTools' -as [type])) { Add-Type -Path (Join-Path $here 'KeyTools.cs') -ReferencedAssemblies System.Core }

$dir = Join-Path $env:APPDATA 'Laptap\BatteryLogger'
$primaryFile = Join-Path $dir 'signing.key'
$nextFile = Join-Path $dir 'signing.next.key'
$backupPub = Join-Path $dir 'backup.pub'
$entropy = [Text.Encoding]::ASCII.GetBytes('laptap-battery-logger-signing')
$testMode = $env:LAPTAP_KEYS_TEST -eq '1'
if (-not $testMode -and ($TestPassphrase -or $TestBackupFile)) { throw '-TestPassphrase / -TestBackupFile are for tests only.' }
# Every action that writes needs the folder first (it doesn't exist on a fresh PC).
if ($Action -ne 'test-new' -and $Action -ne 'public') { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

function Save-Dpapi([byte[]]$priv, [string]$path) {
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    $p = [Security.Cryptography.ProtectedData]::Protect($priv, $entropy, 'CurrentUser')
    [IO.File]::WriteAllBytes($path, $p)
}
function Read-Dpapi([string]$path) {
    [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($path), $entropy, 'CurrentUser')
}
function Read-Pass([string]$prompt, [switch]$Twice) {
    if ($testMode) { if ($TestPassphrase.Length -lt 12) { throw 'Use a passphrase of at least 12 characters.' }; return $TestPassphrase }
    $a = Read-Host $prompt -AsSecureString
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($a))
    if ($Twice) {
        $b = Read-Host 'Type it again' -AsSecureString
        $plain2 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($b))
        if ($plain -ne $plain2) { throw 'The two passphrases differ. Nothing was saved.' }
    }
    if ($plain.Length -lt 12) { throw 'Use a passphrase of at least 12 characters.' }
    return $plain
}
function Pick-SaveFile([string]$name) {
    if ($testMode) { return $TestBackupFile }
    Add-Type -AssemblyName System.Windows.Forms
    $d = New-Object Windows.Forms.SaveFileDialog
    $d.Title = 'Save the BACKUP key (choose the office-safe USB stick)'; $d.FileName = $name; $d.Filter = 'Backup key (*.lbk)|*.lbk'
    if ($d.ShowDialog() -ne 'OK') { throw 'No place chosen. Nothing was saved.' }
    return $d.FileName
}
function Pick-OpenFile() {
    if ($testMode) { return $TestBackupFile }
    Add-Type -AssemblyName System.Windows.Forms
    $d = New-Object Windows.Forms.OpenFileDialog
    $d.Title = 'Open the BACKUP key file'; $d.Filter = 'Backup key (*.lbk)|*.lbk|All files|*.*'
    if ($d.ShowDialog() -ne 'OK') { throw 'No file chosen.' }
    return $d.FileName
}
# A new backup key, all in memory: nothing is written here.
function New-Backup() {
    $pass = Read-Pass 'Passphrase for the BACKUP key (write it on paper for the office safe)' -Twice
    $file = Pick-SaveFile 'laptap-battery-logger-backup.lbk'
    $backup = [KeyTools]::NewKey()
    return @{ Key = $backup; File = $file; Bytes = [KeyTools]::ProtectBackup($backup, $pass); Pub = [KeyTools]::PublicBlob($backup) }
}
# Write a set of files all-or-nothing: if any write fails, the ones written in this call are removed again.
function Write-All($items) {
    $done = @()
    try {
        foreach ($it in $items) {
            New-Item -ItemType Directory -Force -Path (Split-Path $it.Path) | Out-Null
            [IO.File]::WriteAllBytes($it.Path, $it.Bytes)
            $done += $it.Path
        }
    } catch {
        foreach ($p in $done) { try { Remove-Item -LiteralPath $p -Force } catch {} }
        throw "Nothing was saved: $($_.Exception.Message)"
    }
}
function Show-Keys([byte[][]]$keys, [string]$why) {
    Write-Host ''
    Write-Host "Send these lines to the Manager ($why). They are public keys and safe to share:"
    Write-Host ''
    Write-Host ([KeyTools]::CSharpKeys($keys))
}
function PubFromBlob([byte[]]$pub) { $pub }   # public blob: X at 8, Y at 40, same layout KeyTools reads

switch ($Action) {
    'new' {
        if (Test-Path $primaryFile) { throw "Keys already exist in $dir. Use 'rotate', 'recover' or 'public'." }
        # Everything is made in memory first; only then are the three files written (all-or-nothing).
        $b = New-Backup
        $primary = [KeyTools]::NewKey()
        $primaryBytes = [Security.Cryptography.ProtectedData]::Protect($primary, $entropy, 'CurrentUser')
        Write-All @(@{ Path = $b.File; Bytes = $b.Bytes }, @{ Path = $backupPub; Bytes = $b.Pub }, @{ Path = $primaryFile; Bytes = $primaryBytes })
        Write-Host "Backup key saved to $($b.File)"
        Write-Host "Primary key saved for this Windows user in $primaryFile"
        Show-Keys @($primary, $b.Key) 'first keys'
    }
    'rotate' {
        if (-not (Test-Path $primaryFile)) { throw 'No primary key on this PC. Use recover.' }
        if (-not (Test-Path $backupPub)) { throw 'backup.pub is missing; run roll-backup first.' }
        $next = [KeyTools]::NewKey()
        Save-Dpapi $next $nextFile
        Show-Keys @($next, [IO.File]::ReadAllBytes($backupPub)) 'planned rotation: the next release carries {new primary, backup}'
        Write-Host 'Publish the next release as usual. It is signed with the current key; after it, the new key takes over.'
    }
    { $_ -eq 'recover' -or $_ -eq 'stolen' } {
        $file = Pick-OpenFile
        $pass = Read-Pass 'Passphrase of the BACKUP key'
        $backup = [KeyTools]::UnprotectBackup([IO.File]::ReadAllBytes($file), $pass)
        if (Test-Path $primaryFile) { Move-Item $primaryFile ($primaryFile + '.replaced-' + (Get-Date -Format yyyyMMddHHmm)) }
        $primary = [KeyTools]::NewKey()
        Save-Dpapi $primary $primaryFile
        [IO.File]::WriteAllBytes($backupPub, [KeyTools]::PublicBlob($backup))
        Set-Content -LiteralPath (Join-Path $dir 'signing.use-backup') -Value $file
        if ($Action -eq 'stolen') { Set-Content -LiteralPath (Join-Path $dir 'signing.bump') -Value '500' }
        Show-Keys @($backup, $primary) 'recovery: the next release is signed with the BACKUP and carries {backup, new primary}'
        if ($Action -eq 'stolen') { Write-Host 'The next release number jumps by 500, so anything the thief signed below it is superseded.' }
    }
    'roll-backup' {
        if (-not (Test-Path $primaryFile)) { throw 'No primary key on this PC.' }
        $primary = Read-Dpapi $primaryFile
        $b = New-Backup
        Write-All @(@{ Path = $b.File; Bytes = $b.Bytes }, @{ Path = $backupPub; Bytes = $b.Pub })
        Write-Host "Backup key saved to $($b.File)"
        Show-Keys @($primary, $b.Key) 'fresh backup: the next release carries {primary, new backup}'
    }
    'public' {
        if (-not (Test-Path $primaryFile) -or -not (Test-Path $backupPub)) { throw "No keys in $dir." }
        Show-Keys @((Read-Dpapi $primaryFile), [IO.File]::ReadAllBytes($backupPub)) 'current keys'
    }
    'test-new' {
        if (-not $Out) { throw '-Out <file> is needed' }
        $k = [KeyTools]::NewKey()
        [IO.File]::WriteAllBytes($Out, [KeyTools]::ToTestKeyFile($k))
        # X:Y for LAPTAP_BL_TEST_PUBKEYS (a TESTHOOKS build only)
        Write-Output ([KeyTools]::Hex([KeyTools]::PublicX($k)) + ':' + [KeyTools]::Hex([KeyTools]::PublicY($k)))
    }
}
