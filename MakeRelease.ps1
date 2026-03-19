#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Builds, signs, and packages a UIXtend release zip.
    Run this script (as Administrator) to produce a distributable release.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File MakeRelease.ps1
#>

$ErrorActionPreference = "Stop"

$ProjectDir  = $PSScriptRoot
$Version     = "0.1.0.0"
$VersionTag  = "v0.1.0-alpha"
$Thumbprint  = "6B3FA6C835D46770386FF501C111DAD283A1CCF3"
$SignTool    = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
$AppPackages = Join-Path $ProjectDir "..\AppPackages"
$TestDir     = Join-Path $AppPackages "UIXtend_${Version}_x64_Test"
$CleanDir    = Join-Path $AppPackages "UIXtend_${Version}_x64"
$MsixFile    = Join-Path $TestDir     "UIXtend_${Version}_x64.msix"
$ZipOut      = Join-Path $AppPackages "UIXtend-${VersionTag}-win-x64.zip"

Write-Host "`nBuilding UIXtend $VersionTag ...`n" -ForegroundColor Cyan
dotnet build "$ProjectDir\UIXtend.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

Write-Host "`nSigning MSIX ..." -ForegroundColor Cyan
& $SignTool sign /fd SHA256 /sha1 $Thumbprint /td SHA256 /tr http://timestamp.digicert.com $MsixFile
if ($LASTEXITCODE -ne 0) { throw "Signing failed." }

Write-Host "Exporting certificate ..." -ForegroundColor Cyan
Export-Certificate -Cert "Cert:\CurrentUser\My\$Thumbprint" -FilePath "$TestDir\UIXtend.cer" -Type CERT | Out-Null

Write-Host "Removing non-x64 dependencies ..." -ForegroundColor Cyan
@("arm64","win32","x86") | ForEach-Object {
    $p = Join-Path $TestDir "Dependencies\$_"
    if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}

Write-Host "Copying installer scripts ..." -ForegroundColor Cyan
Copy-Item "$ProjectDir\Installer\Install.bat"      $TestDir -Force
Copy-Item "$ProjectDir\Installer\InstallHelper.ps1" $TestDir -Force

Write-Host "Renaming output folder (removing _Test) ..." -ForegroundColor Cyan
if (Test-Path $CleanDir) { Remove-Item $CleanDir -Recurse -Force }
Rename-Item $TestDir "UIXtend_${Version}_x64"

Write-Host "Creating release zip ..." -ForegroundColor Cyan
if (Test-Path $ZipOut) { Remove-Item $ZipOut -Force }
Compress-Archive -Path "$CleanDir\*" -DestinationPath $ZipOut

$SizeMB = [math]::Round((Get-Item $ZipOut).Length / 1MB, 1)
Write-Host "`nDone. $ZipOut ($SizeMB MB)`n" -ForegroundColor Green
