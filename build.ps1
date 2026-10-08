<#
Builds dist\HeatTray.exe from src\HeatTray.cs using the C# compiler built
into .NET Framework, which ships with every Windows 10/11 install.
No downloads, no SDK install required. Run this after moving the
folder to a new machine, or after editing src\HeatTray.cs.
#>

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$csc = Get-ChildItem "C:\Windows\Microsoft.NET\Framework64" -Filter csc.exe -Recurse -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName

if (-not $csc) {
    $csc = Get-ChildItem "C:\Windows\Microsoft.NET\Framework" -Filter csc.exe -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}

if (-not $csc) {
    throw "Could not find csc.exe under C:\Windows\Microsoft.NET. Is .NET Framework installed?"
}

Write-Host "Using compiler: $csc"

New-Item -ItemType Directory -Force "$scriptDir\dist" | Out-Null

& $csc /nologo /target:winexe /platform:x64 `
    /out:"$scriptDir\dist\HeatTray.exe" `
    /win32icon:"$scriptDir\src\HeatTray.ico" `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll `
    "$scriptDir\src\HeatTray.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Build failed."
}

Write-Host "Built $scriptDir\dist\HeatTray.exe"
