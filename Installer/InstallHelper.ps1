$Dir = $PSScriptRoot
$ESC = [char]27
$B   = "$ESC[38;2;0;103;192m"    # brand blue  RGB(0, 103, 192)
$W   = "$ESC[38;2;255;255;255m"  # white
$R   = "$ESC[0m"                  # reset

# ── Banner ────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host ("  " + $B + " _   _ ___ " + $W + "__  __  _                   _ "    + $R)
Write-Host ("  " + $B + "| | | |_ _|" + $W + "\ \/ /| |_ ___ _ _   __| |"       + $R)
Write-Host ("  " + $B + "| |_| || | " + $W + " >  < |  _/ -_) ' \ / _`` |"      + $R)
Write-Host ("  " + $B + " \___/|___|" + $W + "/_/\_\ \__\___|_||_|\__,_|"        + $R)
Write-Host ""
Write-Host "  v0.1.0-alpha"
Write-Host ""
Write-Host "  2026  Grant Harris"
Write-Host "  https://github.com/gstroudharris/UIXtend"
Write-Host ""
Write-Host ("  " + ([string][char]0x2500 * 54))
Write-Host ""

# ── Install ───────────────────────────────────────────────────────────────────
$cer  = Join-Path $Dir "UIXtend.cer"
$dep  = Join-Path $Dir "Dependencies\x64\Microsoft.WindowsAppRuntime.1.6.msix"
$msix = Join-Path $Dir "UIXtend_0.1.0.0_x64.msix"

Write-Host "  Installing signing certificate..."
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null

Write-Host "  Installing Windows App Runtime..."
Add-AppxPackage -Path $dep -ErrorAction SilentlyContinue

Write-Host "  Installing UIXtend..."
Add-AppxPackage -Path $msix

Write-Host "  Creating desktop shortcut..."
$pkg     = Get-AppxPackage -Name "GrantHarris.UIXtend"
$aumid   = $pkg.PackageFamilyName + "!App"
$desktop = [Environment]::GetFolderPath("Desktop")
$lnk     = (New-Object -ComObject WScript.Shell).CreateShortcut("$desktop\UIXtend.lnk")
$lnk.TargetPath  = "explorer.exe"
$lnk.Arguments   = "shell:AppsFolder\$aumid"
$lnk.Description = "UIXtend screen overlay"
$lnk.Save()

# ── Done ──────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host ("  " + ([string][char]0x2500 * 54))
Write-Host ""
Write-Host ("  Install of UIXtend complete! Launch it from your " + $B + "Start menu" + $R + " or " + $B + "Desktop shortcut" + $R + ".")
Write-Host ""
