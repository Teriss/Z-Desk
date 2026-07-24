param(
    [string] $Runtime = 'win-x64',
    [string] $Output = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = (([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.ZDeskVersion | Select-Object -First 1).InnerText
if ([string]::IsNullOrWhiteSpace($version)) { $version = '0.0.0' }
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = "artifacts\portable\ZDesk-$version-win-x64" }
$portableRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts\portable'))
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $root $Output))
if (-not $outputPath.StartsWith($portableRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Portable output must stay under $portableRoot"
}

if (Test-Path -LiteralPath $outputPath) { Remove-Item -LiteralPath $outputPath -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

dotnet publish (Join-Path $root 'ZDesk.csproj') `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $outputPath `
    -p:PortableSingleExe=true `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Portable publish failed with exit code $LASTEXITCODE." }

$files = @(Get-ChildItem -LiteralPath $outputPath -File)
if ($files.Count -ne 1 -or $files[0].Name -ne 'ZDesk.exe') {
    throw "Portable publish must contain only ZDesk.exe; found: $($files.Name -join ', ')"
}

$hash = (Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash
Write-Host "Portable executable: $($files[0].FullName)"
Write-Host "Size: $([Math]::Round($files[0].Length / 1MB, 2)) MB"
Write-Host "SHA-256: $hash"
