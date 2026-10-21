# build.ps1 - build ISETOOLS.exe with the .NET Framework compiler that ships with Windows.
#   .\build.ps1            build dist\ISETOOLS.exe and run the built-in self test
#   .\build.ps1 -NoTest    build only
#   .\build.ps1 -Release   also produce dist\ISETOOLS-<version>.zip (exe + README.md)
param(
    [switch]$NoTest,
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src\ISETOOLS.cs'
$manifest = Join-Path $root 'src\app.manifest'
$dist = Join-Path $root 'dist'
$out = Join-Path $dist 'ISETOOLS.exe'

$cscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'csc.exe (.NET Framework 4.x) not found. Windows 10/11 always ships it under %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\' }

if (-not (Test-Path $src)) { throw "source not found: $src" }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Write-Host "compiler : $csc"
Write-Host "output   : $out"

# NOTE: Windows PowerShell 5.1 does not reliably quote native arguments that contain spaces, so
# "/out:C:\path with space\x.exe" reaches csc.exe split in two. Always invoke csc with RELATIVE
# paths (no spaces) from the repo root instead.
$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/warn:4',
    '/win32manifest:src\app.manifest',
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/out:dist\ISETOOLS.exe',
    'src\ISETOOLS.cs'
)

Push-Location $root
try {
    & $csc $cscArgs 2>&1 | ForEach-Object { Write-Host $_ }
    $compileExit = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($compileExit -ne 0) { throw "compile failed with exit code $compileExit" }

$size = (Get-Item $out).Length
Write-Host ("built    : {0} ({1:N0} bytes)" -f $out, $size)

if (-not $NoTest) {
    Write-Host ''
    Write-Host 'running self test ...'
    # cmd /c waits for GUI-subsystem apps and propagates their exit code; no stdio piping needed
    # (piped stdio is blocked in some sandboxes and is unnecessary here).
    Push-Location $root
    try {
        & cmd /c 'dist\ISETOOLS.exe --selftest'
        $testExit = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    if ($testExit -ne 0) { throw "self test FAILED (exit $testExit)" }
    Write-Host 'self test: PASS'
}

Copy-Item (Join-Path $root 'README.md') (Join-Path $dist 'README.md') -Force

if ($Release) {
    $ver = '1.0.0'
    $zip = Join-Path $dist ("ISETOOLS-{0}.zip" -f $ver)
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $out, (Join-Path $dist 'README.md') -DestinationPath $zip
    Write-Host ("package  : {0} ({1:N0} bytes)" -f $zip, (Get-Item $zip).Length)
}

Write-Host ''
Write-Host 'done. deliverables in dist\'
