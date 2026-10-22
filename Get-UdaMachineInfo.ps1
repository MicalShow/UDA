# Run on the target computer. No administrator rights required.

$ErrorActionPreference = "Stop"

$machine = Get-WmiObject Win32_ComputerSystem

$os = Get-WmiObject Win32_OperatingSystem

$version = [Version]$os.Version

$arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }

$arch = switch ($arch.ToUpperInvariant()) { "AMD64" { "amd64" }; "ARM64" { "arm64" }; default { "x86" } }

$system = if ($version.Major -eq 10) { if ([int]$os.BuildNumber -ge 22000) { "windows11" } else { "windows10" } } elseif ($version.Minor -eq 1) { "windows7" } elseif ($version.Minor -eq 2) { "windows8" } elseif ($version.Minor -eq 3) { "windows81" } else { $os.Caption }

$result = [ordered]@{ Manufacturer=$machine.Manufacturer.Trim(); Model=$machine.Model.Trim(); OperatingSystem=$system; Architecture=$arch; BuildNumber=[int]$os.BuildNumber; ComputerName=$env:COMPUTERNAME }

$json = $result | ConvertTo-Json

$json

$file = Join-Path ([Environment]::GetFolderPath("Desktop")) "UDA-MachineInfo.json"

$json | Out-File -LiteralPath $file -Encoding UTF8

Write-Host "Saved: $file"

Read-Host "Press Enter to close"

