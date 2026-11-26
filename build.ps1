# Battery-Logger/v14/build.ps1 (dev only; staff never run this)
# 1) node tests for logic.js  2) compile the logger exe with the built-in .NET Framework csc
# 3) set the run-from-removable-media PE flags  4) compile + run HelperTests.exe  5) check dist\ and the size.
# Installs nothing. Fails on the first failing step.
param([string]$Out = 'dist')   # e.g. -Out dist-next to leave a frozen dist alone
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $csc)) { throw 'csc.exe (.NET Framework 4) not found' }

$exe = Join-Path (Join-Path $PSScriptRoot $Out) 'Laptap Battery Logger.exe'
$refs = @('/r:System.Management.dll', '/r:System.Web.Extensions.dll', '/r:System.Windows.Forms.dll', '/r:System.Core.dll')
$src = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object { $_.FullName }

function Step($name) { Write-Host ''; Write-Host "== $name" }

# ---- 1. node tests ----
Step 'node tests (logic.js)'
$node = Get-Command node -ErrorAction SilentlyContinue
if ($node) {
    & $node.Source --test (Join-Path $PSScriptRoot 'tests')
    if ($LASTEXITCODE -ne 0) { throw 'node tests failed' }
} else { Write-Warning 'node not found: logic.js tests skipped' }

# ---- 2. compile the logger ----
Step 'compile Laptap Battery Logger.exe'
New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot $Out) | Out-Null
$cargs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/win32icon:src\laptap.ico', '/win32manifest:src\app.manifest') + $refs +
        @('/resource:src\web\index.html,index.html', '/resource:src\web\logic.js,logic.js', "/out:$exe") + $src
& $csc @cargs
if ($LASTEXITCODE -ne 0) { throw 'compile failed (logger)' }

# ---- 3. PE flags: IMAGE_FILE_REMOVABLE_RUN_FROM_SWAP (0x0400) + IMAGE_FILE_NET_RUN_FROM_SWAP (0x0800) ----
Step 'set run-from-swap PE flags'
$b = [System.IO.File]::ReadAllBytes($exe)
$pe = [BitConverter]::ToInt32($b, 0x3C)
if ($b[$pe] -ne 0x50 -or $b[$pe + 1] -ne 0x45 -or $b[$pe + 2] -ne 0 -or $b[$pe + 3] -ne 0) { throw 'not a PE file' }
$ch = [BitConverter]::ToUInt16($b, $pe + 22) -bor 0x0C00
$bytes = [BitConverter]::GetBytes([uint16]$ch)
$b[$pe + 22] = $bytes[0]; $b[$pe + 23] = $bytes[1]
[System.IO.File]::WriteAllBytes($exe, $b)
$check = [BitConverter]::ToUInt16([System.IO.File]::ReadAllBytes($exe), $pe + 22)
if (($check -band 0x0C00) -ne 0x0C00) { throw 'PE flags not set' }
Write-Host ('Characteristics = 0x{0:X4}' -f $check)

# ---- 4. C# tests ----
Step 'compile + run HelperTests.exe'
$tbin = Join-Path $PSScriptRoot 'tests\bin'
New-Item -ItemType Directory -Force -Path $tbin | Out-Null
$texe = Join-Path $tbin 'HelperTests.exe'
$targs = @('/nologo', '/target:exe', '/platform:anycpu', '/main:HelperTests') + $refs +
         @('/resource:src\web\index.html,index.html', '/resource:src\web\logic.js,logic.js', "/out:$texe") + $src + @('tests\HelperTests.cs')
& $csc @targs
if ($LASTEXITCODE -ne 0) { throw 'compile failed (tests)' }
& $texe (Join-Path $PSScriptRoot 'tests\fixtures') $exe (Join-Path $PSScriptRoot 'src\web\index.html')
if ($LASTEXITCODE -ne 0) { throw 'HelperTests failed' }

# ---- 5. dist check ----
Step 'dist check'
$files = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $Out) -Force)
if ($files.Count -ne 1) { throw ($Out + '\ must hold exactly one file, found: ' + (($files | ForEach-Object Name) -join ', ')) }
$kb = [Math]::Round((Get-Item -LiteralPath $exe).Length / 1KB, 1)
Write-Host "$Out\Laptap Battery Logger.exe = $kb KB"
if ($kb -ge 400) { throw "exe is $kb KB (target < 400 KB)" }
Write-Host ''
Write-Host 'BUILD OK'
