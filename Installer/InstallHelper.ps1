# Copyright (C) 2026  Grant Harris
# SPDX-License-Identifier: GPL-3.0-or-later

$ErrorActionPreference = "Stop"
$Dir     = $PSScriptRoot
$LogFile = Join-Path $Dir "install_log.txt"

function Log($msg) {
    $ts   = (Get-Date).ToString("HH:mm:ss")
    $line = "[$ts] $msg"
    Write-Host "  $msg"
    Add-Content -Path $LogFile -Value $line
}

if (Test-Path $LogFile) { Remove-Item $LogFile -Force }
Add-Content -Path $LogFile -Value "UIXtend installer started $(Get-Date)"
Add-Content -Path $LogFile -Value "Script root: $Dir"

$ESC = [char]27
$B   = "$ESC[38;2;0;103;192m"
$W   = "$ESC[38;2;255;255;255m"
$R   = "$ESC[0m"

# Banner
Write-Host ""
Write-Host ("  " + $B + " _   _ ___ " + $W + "__  __  _                   _ "  + $R)
Write-Host ("  " + $B + "| | | |_ _|" + $W + "\ \/ /| |_ ___ _ _   __| |"     + $R)
Write-Host ("  " + $B + "| |_| || | " + $W + " >  < |  _/ -_) ' \ / _`` |"    + $R)
Write-Host ("  " + $B + " \___/|___|" + $W + "/_/\_\ \__\___|_||_|\__,_|"      + $R)
Write-Host ""
Write-Host "  v0.1.0-alpha"
Write-Host ""
Write-Host "  2026  Grant Harris"
Write-Host "  https://github.com/gstroudharris/UIXtend"
Write-Host ""
Write-Host ("  " + ([string][char]0x2500 * 54))
Write-Host ""

# Paths
$cer  = Join-Path $Dir "UIXtend.cer"
$dep  = Join-Path $Dir "Dependencies\x64\Microsoft.WindowsAppRuntime.1.6.msix"
$msix = Join-Path $Dir "UIXtend_0.1.0.0_x64.msix"

Log "Script directory : $Dir"
Log "Certificate      : $cer  [$(if (Test-Path $cer)  {'FOUND'} else {'MISSING'})]"
Log "Runtime package  : $dep  [$(if (Test-Path $dep)  {'FOUND'} else {'MISSING'})]"
Log "App package      : $msix [$(if (Test-Path $msix) {'FOUND'} else {'MISSING'})]"
Write-Host ""

# Install certificate
try {
    Log "Installing signing certificate..."
    if (-not (Test-Path $cer)) { throw "Certificate file not found: $cer" }
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    Log "Certificate installed."
} catch {
    Log "ERROR installing certificate: $_"
    Write-Host "  See log: $LogFile" -ForegroundColor Red
    Read-Host "`n  Press Enter to exit"
    exit 1
}

# Install Windows App Runtime
try {
    Log "Installing Windows App Runtime..."
    if (-not (Test-Path $dep)) { throw "Runtime package not found: $dep" }
    Add-AppxPackage -Path $dep -ErrorAction SilentlyContinue
    Log "Runtime install attempted (already installed is OK)."
} catch {
    Log "WARNING: Runtime install failed (may already be installed): $_"
}

# Install UIXtend
try {
    Log "Installing UIXtend..."
    if (-not (Test-Path $msix)) { throw "App package not found: $msix" }
    Add-AppxPackage -Path $msix
    Log "UIXtend installed successfully."
} catch {
    Log "ERROR installing UIXtend: $_"
    Write-Host "  See log: $LogFile" -ForegroundColor Red
    Read-Host "`n  Press Enter to exit"
    exit 1
}

# Create desktop shortcut
try {
    Log "Creating desktop shortcut..."
    $pkg = Get-AppxPackage -Name "GrantHarris.UIXtend"
    if (-not $pkg) { throw "Package not found after install - Get-AppxPackage returned nothing." }
    $aumid   = $pkg.PackageFamilyName + "!App"
    $desktop = [Environment]::GetFolderPath("Desktop")
    $lnk     = (New-Object -ComObject WScript.Shell).CreateShortcut("$desktop\UIXtend.lnk")
    $lnk.TargetPath   = "explorer.exe"
    $lnk.Arguments    = "shell:AppsFolder\$aumid"
    $lnk.Description  = "UIXtend screen overlay"
    $lnk.IconLocation = (Join-Path $pkg.InstallLocation "assets\UIXtend.ico") + ",0"
    $lnk.Save()
    Log "Desktop shortcut created."
} catch {
    Log "WARNING: Could not create desktop shortcut: $_"
}

# Done
Write-Host ""
Write-Host ("  " + ([string][char]0x2500 * 54))
Write-Host ""
Write-Host ("  Install of UIXtend complete! Launch it from your " + $B + "Start menu" + $R + " or " + $B + "Desktop shortcut" + $R + ".")
Write-Host ""
Log "Install complete."
Write-Host "  Full log: $LogFile"
Write-Host ""
Read-Host "  Press Enter to exit"
