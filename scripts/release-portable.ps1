param(
    [string] $Runtime = 'win-x64',
    [switch] $Yes,
    [switch] $SkipValidation
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Get-ProjectVersion {
    [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
    return [string](($props.Project.PropertyGroup.ZDeskVersion | Select-Object -First 1).InnerText)
}

function Get-NextPatchVersion([string] $version) {
    $parsed = [version]$version
    return "{0}.{1}.{2}" -f $parsed.Major, $parsed.Minor, ($parsed.Build + 1)
}

function Update-Version([string] $version) {
    $propsPath = Join-Path $root 'Directory.Build.props'
    $props = Get-Content $propsPath -Raw
    $props = [regex]::Replace($props, '<ZDeskVersion([^>]*)>[^<]+</ZDeskVersion>', ('<ZDeskVersion$1>' + $version + '</ZDeskVersion>'))
    Set-Content -LiteralPath $propsPath -Value $props -Encoding utf8NoBOM

    $manifestPath = Join-Path $root 'packaging\ModernContextMenu\AppxManifest.xml'
    $manifest = Get-Content $manifestPath -Raw
    $manifest = [regex]::Replace($manifest, 'Version="[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+"', ('Version="' + $version + '.0"'))
    Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding utf8NoBOM
}

if ((git status --porcelain) -and -not $SkipValidation) { throw 'Working tree has uncommitted changes.' }
if ((git branch --show-current) -ne 'main' -and -not $SkipValidation) { throw 'Release must run on main branch.' }

$version = Get-ProjectVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version: $version" }
$localTagExists = @(git tag --list "v$version").Count -gt 0
$remoteTagExists = ((git ls-remote --tags origin "refs/tags/v$version") -match "refs/tags/v$version")
if ($localTagExists -or $remoteTagExists) {
    $version = Get-NextPatchVersion $version
    Update-Version $version
    Write-Host "Version bumped to $version"
}

if (-not $Yes) {
    $answer = Read-Host "Release Z-Desk $version? (Y/N)"
    if ($answer -notmatch '^(y|yes)$') { throw 'Release cancelled.' }
}

if (-not $SkipValidation) {
    dotnet build ZDesk.csproj -c Debug --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Debug build failed.' }
    dotnet build ZDesk.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet run --project tests\ZDesk.SmokeTests\ZDesk.SmokeTests.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'SmokeTests failed.' }
}

& (Join-Path $root 'scripts\publish-portable.ps1') -Runtime $Runtime
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }

if ((git status --porcelain)) {
    git add Directory.Build.props packaging/ModernContextMenu/AppxManifest.xml
    git commit -m "Release Z-Desk $version"
    if ($LASTEXITCODE -ne 0) { throw 'Version commit failed.' }
}
git tag -a "v$version" -m "Z-Desk $version"
if ($LASTEXITCODE -ne 0) { throw 'Tag creation failed.' }
git push origin main
if ($LASTEXITCODE -ne 0) { throw 'Pushing main failed.' }
git push origin "v$version"
if ($LASTEXITCODE -ne 0) { throw 'Pushing release tag failed.' }
Write-Host "Pushed v$version. GitHub Actions will create the Release."
