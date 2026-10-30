param([string]$OutputDirectory='release-v5.0.4',[switch]$NativeSmoke)
$ErrorActionPreference='Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory+'\Lan\UDA-Server-Manager.exe')))
$old=[datetime]'1968-07-18';$new=[datetime]'2022-04-11';$rank=[uint32]0x00FF2001
if([UDA.WindowsDriverPreference]::IsBetter($rank,$old,'10.1.41.5',$rank,$new,'8.7.10802.26924')){throw 'RocketLake incorrectly outranks DPTF'}
if(-not [UDA.WindowsDriverPreference]::IsBetter($rank,$new,'8.7.10802.26924',$rank,$old,'10.1.41.5')){throw 'Date priority lost'}
if(-not [UDA.WindowsDriverPreference]::IsBetter(1,$old,'1.0',2,$new,'99.0')){throw 'Native rank must precede date/version'}
if(-not [UDA.WindowsDriverPreference]::IsBetter($rank,$new,'8.8',$rank,$new,'8.7')){throw 'Version tiebreaker lost'}
if([UDA.WindowsDriverPreference]::IsBetter($rank,$new,'8.7',$rank,$new,'8.7')){throw 'Identical node offered again'}
Write-Output 'PASS: supplied RocketLake/DPTF ranks and dates, rank/date/version ordering, equivalent node rejection'
if($NativeSmoke){
 $tested=0
 foreach($row in (Get-CimInstance Win32_PnPSignedDriver|Where-Object {$_.InfName -and $_.DeviceID}|Select-Object -First 40)){
   $device=New-Object UDA.D;$device.Id=$row.DeviceID;$device.Inf=$row.InfName;$device.Version=$row.DriverVersion
   if(-not [UDA.CurrentDriverState]::RefreshDevice($device,$null)){continue}
   $inf=Join-Path $env:windir ('INF\'+$device.Inf)
   $verdict=[UDA.WindowsDriverPreference]::IsBetterForDevice($device,$inf)
   if($null-eq $verdict){continue}
   if($verdict){throw ('Native API prefers identical currently-bound package: '+$device.Id)}
   $tested++;if($tested-ge 3){break}
 }
 if($tested-eq 0){throw 'No successful native SetupAPI ranking evaluation; investigate interop'}
 Write-Output ('PASS: native SetupAPI compatible-list rank comparison on '+$tested+' local devices (read-only, no installs)')
}
