param([string]$OutputDirectory = 'release-v4.8.6')
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory + '\Lan\UDA-Server-Manager.exe')))
$device = New-Object UDA.D
$device.Inf = 'oem151.inf'; $device.Version = '11.0.6000.1046'
if (-not [UDA.CurrentDriverState]::ApplyBinding($device, 'oem111.inf', '12.0.6000.1905', $null)) { throw 'Binding not applied' }
if ($device.Inf -ne 'oem111.inf' -or $device.Version -ne '12.0.6000.1905') { throw 'Stale state retained' }
if ([UDA.LanDriverClient]::NeedsInstall($device, '12.0.6000.1905')) { throw 'Installed version offered again' }
if ([UDA.CurrentDriverState]::ApplyBinding($device, '', '13.0', $null)) { throw 'Incomplete binding accepted' }
if ($device.Version -ne '12.0.6000.1905') { throw 'Incomplete binding overwrote state' }
[void][UDA.CurrentDriverState]::ApplyBinding($device, 'oem151.inf', '11.0.6000.1046', $null)
if (-not [UDA.LanDriverClient]::NeedsInstall($device, '12.0.6000.1905')) { throw 'Rollback hidden' }
$device.Error = 28
if (-not [UDA.LanDriverClient]::NeedsInstall($device, '11.0.6000.1046')) { throw 'Missing driver hidden' }
Write-Output 'PASS: binding refresh, equal-version exclusion, incomplete-read safety, rollback and missing-driver visibility'
$signed = Get-WmiObject Win32_PnPSignedDriver | Where-Object { $_.DeviceID -and $_.InfName -and $_.DriverVersion } | Select-Object -First 20
$verified = 0
foreach ($item in $signed) {
    $actual = New-Object UDA.D
    $actual.Id = $item.DeviceID; $actual.Version = '0.0'; $actual.Inf = 'stale.inf'
    if ([UDA.CurrentDriverState]::RefreshDevice($actual, $null)) {
        if ($actual.Inf -eq 'stale.inf' -or $actual.Version -eq '0.0') { throw 'Native API did not refresh binding' }
        $verified++
    }
}
if ($verified -eq 0) { throw 'No native device binding could be read on host' }
Write-Output ('PASS: read-only native Windows binding verification on ' + $verified + ' devices')
