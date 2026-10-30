#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installiert den MONIVOX-Anzeigetreiber und legt das virtuelle Gerät an.

.DESCRIPTION
  KEIN Testsignatur-Modus, Secure Boot kann an bleiben.

  Der Treiber ist ein User-Mode-Treiber (UMDF/IddCx). Für solche Treiber reicht eine
  Authenticode-Signatur, deren Zertifikat Windows vertraut. Das Zertifikat liegt dem Paket bei
  (MonivoxDriver.cer), kann nur Code signieren, und sein privater Schlüssel wurde nach dem Build vernichtet.
  Die Kernel-Teile (WUDFRd.sys, IndirectKmd.sys) stammen von Microsoft und sind von Microsoft signiert.

  Ablauf:
   1. Alte MONIVOX-Zertifikate entfernen, neues in "Vertrauenswürdige Stammzertifizierungsstellen"
      und "Vertrauenswürdige Herausgeber" (lokaler Computer) importieren.
   2. Treiber in den Driver Store aufnehmen (pnputil).
   3. Software-Gerät anlegen (MonivoxDevice.exe create).
#>
param(
    [string]$PackageDir = (Join-Path $PSScriptRoot "..\driver-package")
)

$ErrorActionPreference = "Stop"
$inf = Get-ChildItem -Path $PackageDir -Filter MonivoxIdd.inf -Recurse | Select-Object -First 1
if (-not $inf) { throw "MonivoxIdd.inf nicht in $PackageDir gefunden." }
$pkg = $inf.Directory.FullName
$cerPath = Join-Path $pkg "MonivoxDriver.cer"
if (-not (Test-Path $cerPath)) { throw "MonivoxDriver.cer fehlt im Treiberpaket." }

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cerPath)

# Katalog muss mit genau diesem Zertifikat signiert sein
$sig = Get-AuthenticodeSignature -FilePath (Join-Path $pkg "MonivoxIdd.cat")
if (-not $sig.SignerCertificate -or $sig.SignerCertificate.Thumbprint -ne $cert.Thumbprint) {
    throw "Signatur des Treiberkatalogs passt nicht zum mitgelieferten Zertifikat – Paket beschädigt?"
}

foreach ($store in "Root", "TrustedPublisher") {
    $s = New-Object System.Security.Cryptography.X509Certificates.X509Store($store, "LocalMachine")
    $s.Open("ReadWrite")
    foreach ($old in @($s.Certificates | Where-Object { $_.Subject -eq "CN=MONIVOX Driver Signing" -and $_.Thumbprint -ne $cert.Thumbprint })) {
        $s.Remove($old)
    }
    $s.Add($cert)
    $s.Close()
}
Write-Host "Treiberzertifikat vertraut: $($cert.Subject) ($($cert.Thumbprint))"

pnputil /add-driver $inf.FullName /install
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 259 -and $LASTEXITCODE -ne 3010) { throw "pnputil ist fehlgeschlagen ($LASTEXITCODE)." }

$tool = Join-Path $PSScriptRoot "..\MonivoxDevice.exe"
& $tool create
if ($LASTEXITCODE -ne 0) { throw "Gerät konnte nicht angelegt werden." }

Write-Host ""
Write-Host "Fertig. Testen mit:  monivox list   und   monivox apply half"
