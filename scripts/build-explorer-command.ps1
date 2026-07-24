param(
    [Parameter(Mandatory = $true)] [string] $Configuration,
    [Parameter(Mandatory = $true)] [string] $ProjectRoot
)

$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio Build Tools with MSVC x64 are required.' }
$install = & $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($install)) { throw 'MSVC x64 build tools were not found.' }
$vcvars = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat was not found: $vcvars" }

$source = Join-Path $ProjectRoot 'native\ZDeskExplorerCommand\ZDeskExplorerCommand.cpp'
$outputDirectory = Join-Path $ProjectRoot "bin\$Configuration\net10.0-windows\win-x64"
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$output = Join-Path $outputDirectory 'ZDeskExplorerCommand.dll'
$object = Join-Path $outputDirectory 'ZDeskExplorerCommand.obj'
$importLibrary = Join-Path $outputDirectory 'ZDeskExplorerCommand.lib'
$command = "call `"$vcvars`" >nul && cl.exe /nologo /utf-8 /std:c++17 /EHsc /LD /DUNICODE /D_UNICODE `"$source`" /Fo`"$object`" /link advapi32.lib shell32.lib /OUT:`"$output`" /IMPLIB:`"$importLibrary`""
& cmd.exe /d /c $command
if ($LASTEXITCODE -ne 0) { throw "Native Explorer command build failed with exit code $LASTEXITCODE." }
