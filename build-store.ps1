<#
Packages dist\HeatTray.exe as an MSIX (desktop bridge, full trust) for the
Microsoft Store. Needs the Windows 10/11 SDK (makeappx.exe, signtool.exe).

Without -Sideload the .msix is left UNSIGNED, which is what Partner Center
wants for upload (the Store signs it for you).

With -Sideload the package is signed with a throwaway self-signed certificate
for local testing only. That build cannot be submitted to the Store.

IMPORTANT: Partner Center provides the real Identity Name, Publisher and
PublisherDisplayName for your reserved app name (Product identity page).
They MUST match what you pass here, or the Store submission is rejected.
The defaults below are the values Partner Center showed for the reserved name (2 Nov 2026; Store ID 9P77LQB5Q1FV,
PFN AndrewReardon.HeatTray_qnhc6s6t58sdy).
#>

param(
    [string]$IdentityName = "AndrewReardon.HeatTray",
    [string]$Publisher = "CN=BFD979DA-5768-4CA3-9A76-86E191905F0E",
    [string]$PublisherDisplayName = "Andrew Reardon",
    [switch]$Sideload
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $IdentityName -or -not $Publisher) {
    if ($Sideload) { $IdentityName = 'HeatTray.SideloadTest'; $Publisher = 'CN=HeatTraySideloadTest' }
    else { throw 'Pass -IdentityName and -Publisher exactly as Partner Center shows them (Product identity page), or use -Sideload for a local test build.' }
}

# 1. Build the exe
& "$scriptDir\build.ps1"

# 2. Read the version from the source and make it 4-part (1.5 -> 1.5.0.0)
$src = Get-Content "$scriptDir\src\HeatTray.cs" -Raw
if ($src -notmatch 'Version\s*=\s*"(\d+(?:\.\d+)*)"') {
    throw "Could not find the Version constant in src\HeatTray.cs."
}
$parts = @($Matches[1].Split('.'))
while ($parts.Count -lt 4) { $parts += '0' }
$version = ($parts | Select-Object -First 4) -join '.'
Write-Host "Package version: $version"

# 3. Stage exe + manifest + assets
$stage = "$scriptDir\store\stage"
$out = "$scriptDir\store\out"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
New-Item -ItemType Directory -Force $out | Out-Null

Copy-Item "$scriptDir\dist\HeatTray.exe" $stage
Copy-Item "$scriptDir\store\Assets" "$stage\Assets" -Recurse

$manifest = Get-Content "$scriptDir\store\AppxManifest.xml" -Raw
$manifest = $manifest.Replace('@IDENTITY_NAME@', [Security.SecurityElement]::Escape($IdentityName))
$manifest = $manifest.Replace('@PUBLISHER@', [Security.SecurityElement]::Escape($Publisher))
$manifest = $manifest.Replace('@PUBLISHER_DISPLAY_NAME@', [Security.SecurityElement]::Escape($PublisherDisplayName))
$manifest = $manifest.Replace('@VERSION@', $version)
[IO.File]::WriteAllText("$stage\AppxManifest.xml", $manifest, (New-Object Text.UTF8Encoding($false)))

# 4. Locate makeappx.exe and signtool.exe (newest Windows SDK)
$kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
function Find-SdkTool([string]$name) {
    # List the version folders first: in Windows PowerShell 5.1, Get-ChildItem "$kits\*\x64" -Filter returns nothing.
    $found = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$name" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if (-not $found) {
        throw "Could not find $name under $kits. Install the Windows 10/11 SDK."
    }
    return $found
}
$makeappx = Find-SdkTool "makeappx.exe"
Write-Host "Using makeappx: $makeappx"

# 5. Pack
$msix = "$out\HeatTray_${version}_x64.msix"
& $makeappx pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) {
    throw "makeappx failed."
}
Write-Host "Built $msix (unsigned; upload this one to Partner Center)"

# 6. Optional: self-signed sideload build for local testing
if ($Sideload) {
    $signtool = Find-SdkTool "signtool.exe"
    Write-Host "Using signtool: $signtool"

    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher `
        -KeyUsage DigitalSignature -FriendlyName "HeatTray sideload" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

    $pw = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char]$_ })
    $pfx = "$out\HeatTraySideload.pfx"
    $cer = "$out\HeatTraySideload.cer"
    Export-PfxCertificate -Cert $cert -FilePath $pfx `
        -Password (ConvertTo-SecureString $pw -AsPlainText -Force) | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null

    # Sign a copy: the unsigned package above is the one to upload to Partner Center.
    $signed = "$out\HeatTray_${version}_x64_sideload.msix"
    Copy-Item $msix $signed -Force
    & $signtool sign /fd SHA256 /a /f $pfx /p $pw $signed
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed."
    }

    Write-Host ""
    Write-Host "Signed $signed with a self-signed certificate (pfx password: $pw)."
    Write-Host "The .pfx is git-ignored; never commit it. To install for testing:"
    Write-Host "  1. In an ADMIN PowerShell, trust the certificate:"
    Write-Host "       Import-Certificate -FilePath `"$cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    Write-Host "  2. Install the package:"
    Write-Host "       Add-AppxPackage `"$signed`""
}
