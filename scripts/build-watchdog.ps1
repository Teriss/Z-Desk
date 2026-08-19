param(
    [Parameter(Mandatory = $true)] [string] $ProjectRoot,
    [Parameter(Mandatory = $true)] [string] $Output
)

$ErrorActionPreference = 'Stop'
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio Build Tools with MSVC x64 are required.' }
$install = & $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($install)) { throw 'MSVC x64 build tools were not found.' }
$vcvars = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat was not found: $vcvars" }

$source = Join-Path $ProjectRoot 'native\ZDeskWatchdog\ZDeskWatchdog.cpp'
$outputDirectory = Split-Path -Parent $Output
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$object = Join-Path $outputDirectory 'ZDeskWatchdog.obj'
$command = "call `"$vcvars`" >nul && cl.exe /nologo /utf-8 /std:c++17 /O1 /MT /DUNICODE /D_UNICODE `"$source`" /Fo`"$object`" /link user32.lib /OUT:`"$Output`""
& cmd.exe /d /c $command
if ($LASTEXITCODE -ne 0) { throw "Native watchdog build failed with exit code $LASTEXITCODE." }
