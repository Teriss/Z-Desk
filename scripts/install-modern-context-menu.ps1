param([switch] $Uninstall)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$packageName = 'ZDesk.ModernContextMenu'

if ($Uninstall) {
    Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Remove-AppxPackage
    Write-Host 'Z-Desk modern context menu package removed.'
    exit 0
}

$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64'
$makeAppx = Join-Path $sdk 'makeappx.exe'
$signTool = Join-Path $sdk 'signtool.exe'
if (-not (Test-Path $makeAppx) -or -not (Test-Path $signTool)) { throw 'Windows 11 SDK MakeAppx/SignTool is required.' }

dotnet publish (Join-Path $root 'ZDesk.csproj') -c Release --no-restore -o (Join-Path $root 'artifacts\modern-context-menu\payload')
$payload = Join-Path $root 'artifacts\modern-context-menu\payload'
Copy-Item (Join-Path $root 'packaging\ModernContextMenu\AppxManifest.xml') (Join-Path $payload 'AppxManifest.xml') -Force

Add-Type -AssemblyName System.Drawing
$assets = Join-Path $payload 'Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
foreach ($asset in @(@('StoreLogo.png',50), @('Square44x44Logo.png',44), @('Square150x150Logo.png',150))) {
    $size = [int]$asset[1]
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $circle = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(15, 197, 232))
    $graphics.FillEllipse($circle, ($size * 0.04), ($size * 0.04), ($size * 0.92), ($size * 0.92))
    $font = New-Object System.Drawing.Font 'Segoe UI', ([Math]::Max(10, $size * 0.48)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $textSize = $graphics.MeasureString('Z', $font)
    $graphics.DrawString('Z', $font, $brush, (($size - $textSize.Width) / 2), (($size - $textSize.Height) / 2))
    $bitmap.Save((Join-Path $assets $asset[0]), [System.Drawing.Imaging.ImageFormat]::Png)
    $brush.Dispose(); $font.Dispose(); $circle.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=ZDesk Development' | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=ZDesk Development' -KeyUsage DigitalSignature `
        -FriendlyName 'Z-Desk Development Package Certificate' -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.19={text}', '2.5.29.37={text}1.3.6.1.5.5.7.3.3')
}
$certificateFile = Join-Path $root 'artifacts\modern-context-menu\ZDeskDevelopment.cer'
Export-Certificate -Cert $cert -FilePath $certificateFile -Force | Out-Null
Import-Certificate -FilePath $certificateFile -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
Import-Certificate -FilePath $certificateFile -CertStoreLocation Cert:\CurrentUser\Root | Out-Null

$msix = Join-Path $root 'artifacts\modern-context-menu\ZDesk.ModernContextMenu.msix'
if (Test-Path $msix) { Remove-Item -LiteralPath $msix -Force }
& $makeAppx pack /d $payload /p $msix /o
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed.' }
& $signTool sign /fd SHA256 /sha1 $cert.Thumbprint /s My $msix
if ($LASTEXITCODE -ne 0) { throw 'SignTool failed.' }
Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Remove-AppxPackage
try { Add-AppxPackage -Path $msix }
catch {
    Write-Warning 'Certificate trust was rejected; installing this local development package with -AllowUnsigned.'
    Add-AppxPackage -Path $msix -AllowUnsigned
}
Write-Host "Installed: $msix"
