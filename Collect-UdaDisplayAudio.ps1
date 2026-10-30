<# Read-only: collect INF selection evidence; never install/remove drivers. #>
[CmdletBinding()]
param([string[]]$InfNames = @('oem50.inf','oem51.inf'), [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = [Environment]::GetFolderPath('Desktop') }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$folder = Join-Path $OutputDirectory ('UDA-DisplayAudio-' + $stamp)
[void](New-Item -ItemType Directory -Path $folder)
$devicePrefix = 'INTELAUDIO\FUNC_01&VEN_8086&DEV_2812'
$devices = @(Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId.StartsWith($devicePrefix,[StringComparison]::OrdinalIgnoreCase) })
$devices | Format-List * | Out-String | Set-Content -LiteralPath (Join-Path $folder 'devices.txt') -Encoding UTF8
foreach ($device in $devices) {
    Get-PnpDeviceProperty -InstanceId $device.InstanceId | Format-List * | Out-String | Add-Content -LiteralPath (Join-Path $folder 'properties.txt') -Encoding UTF8
    (& pnputil.exe /enum-devices /instanceid $device.InstanceId /drivers 2>&1 | Out-String) | Add-Content -LiteralPath (Join-Path $folder 'compatible-drivers.txt') -Encoding UTF8
}
foreach ($name in $InfNames) {
    if ($name -notmatch '^oem[0-9]+[.]inf$') { throw 'Only published oemN.inf filenames are accepted.' }
    $source = Join-Path (Join-Path $env:windir 'INF') $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $folder }
}
$setupLog = Join-Path $env:windir 'INF\setupapi.dev.log'
if (Test-Path -LiteralPath $setupLog) {
    $text = [IO.File]::ReadAllText($setupLog)
    $blocks = [regex]::Split($text, '(?m)(?=^>>>  \[)')
    $hits = @($blocks | Where-Object { $_ -match 'DEV_2812|mshdadac[.]inf|oem50[.]inf|oem51[.]inf' } | Select-Object -Last 30)
    $hits -join "`r`n" | Set-Content -LiteralPath (Join-Path $folder 'setupapi-relevant.txt') -Encoding UTF8
}
$zip = $folder + '.zip'
Compress-Archive -LiteralPath $folder -DestinationPath $zip
Write-Host ('Collected (no drivers changed): ' + $zip)
