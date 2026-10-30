#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Entfernt Monivox sauber: Split beenden, Gerät entfernen, Treiber aus dem Driver Store löschen,
  Registry-Konfiguration löschen. Entfernt auch das Treiberzertifikat.
#>
$ErrorActionPreference = "Continue"
$root = Join-Path $PSScriptRoot ".."

& (Join-Path $root "monivox.exe") stop
& (Join-Path $root "MonivoxDevice.exe") remove

$drivers = pnputil /enum-drivers | Out-String
$blocks = $drivers -split "(\r?\n){2,}"
foreach ($b in $blocks) {
    if ($b -match "monivoxidd\.inf" -and $b -match "(oem\d+\.inf)") {
        pnputil /delete-driver $Matches[1] /uninstall /force
    }
}

Remove-Item "HKLM:\SOFTWARE\Monivox" -Recurse -ErrorAction SilentlyContinue

foreach ($store in "Root", "TrustedPublisher") {
    $s = New-Object System.Security.Cryptography.X509Certificates.X509Store($store, "LocalMachine")
    $s.Open("ReadWrite")
    foreach ($c in @($s.Certificates | Where-Object { $_.Subject -eq "CN=MONIVOX Driver Signing" })) { $s.Remove($c) }
    $s.Close()
}
Write-Host "MONIVOX-Treiber und Zertifikat entfernt."
