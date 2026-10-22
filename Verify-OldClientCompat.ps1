param([string]$OutputDirectory='release-v5.1.0',[string]$OldDirectory='handoff-build')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Web.Extensions
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory+'\Lan\LanDriverService.exe')))
$root=Join-Path $PSScriptRoot ($OutputDirectory+'\old-client-test-'+[Guid]::NewGuid().ToString('N'))
$source=Join-Path $root 'source';[void][IO.Directory]::CreateDirectory($source)
Copy-Item (Join-Path $PSScriptRoot 'test-fixtures\organized-v1\*') $source
$logger=New-Object UDA.L((Join-Path $root 'server-logs'))
$cfg=New-Object UDA.LanServerConfig;$cfg.Host='localhost';$cfg.Token='preserved-client-token';$cfg.RepoRoot=Join-Path $root 'repo'
$tcp=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0);$tcp.Start();$cfg.Port=$tcp.LocalEndpoint.Port;$tcp.Stop()
$gen=New-Object UDA.OfflineRepoGenerator((New-Object UDA.TrustingSignatureVerifier),$logger)
[void]$gen.Generate($cfg.RepoRoot,[string[]]@($source),(New-Object UDA.OsTarget),$null,'test',[Threading.CancellationToken]::None,$null)
$server=New-Object UDA.LanDriverServer($cfg,$logger)
$script=@'
param([string]$OldAssembly,[string]$Url,[string]$Root)
$ErrorActionPreference='Stop'
$assembly=[Reflection.Assembly]::LoadFrom($OldAssembly)
$settings=New-Object UDA.LanClientConfig
$settings.ServerUrl=$Url;$settings.Token='preserved-client-token';$settings.StagingDir=Join-Path $Root 'cache';$settings.TimeoutMs=10000;$settings.DownloadTimeoutMs=10000
$device=New-Object UDA.D;$device.Id='OLD-CLIENT-DEVICE';$device.Name='Old client synthetic device';$device.Class='Display';$device.H.Add('PCI\VEN_1234&DEV_5678')
$devices=New-Object 'System.Collections.Generic.List[UDA.D]';$devices.Add($device)
$collector=[UDA.StaticHardwareCollector]::new($devices)
$target=New-Object UDA.OsTarget;$target.Architecture='amd64';$target.MajorVersion=10;$target.BuildNumber=19045
$logger=New-Object UDA.L((Join-Path $Root 'old-client-logs'))
$sig=New-Object UDA.TrustingSignatureVerifier;$admin=New-Object UDA.AlwaysAdminChecker;$runner=New-Object UDA.FakePnpInstallRunner;$sys=New-Object UDA.NoOpSystemOps
$receipts=$assembly.GetType('UDA.LanInstallReceiptStore').GetMethod('UseTestStore',[Reflection.BindingFlags]'Static,NonPublic')
[void]$receipts.Invoke($null,[object[]]@([string](Join-Path $Root 'test-receipts.txt')))
try{
 $client=New-Object UDA.LanDriverClient -ArgumentList $settings,$null,$collector,$target,$logger,$sig,$admin,$runner,$sys
 $result=$client.Run([Threading.CancellationToken]::None)
 if(-not $result.Reported -or $result.Downloaded-ne 1 -or $result.InstalledOk-ne 1){throw ('Old client failed: '+(New-Object System.Web.Script.Serialization.JavaScriptSerializer).Serialize($result))}
 Write-Output ('PASS: unmodified client '+[UDA.LanProtocol]::ServerVersion+' health/report/download/hash-check/fake-install against new server')
}finally{$logger.Dispose()}
'@
$scriptPath=Join-Path $root 'old-client.ps1';[IO.File]::WriteAllText($scriptPath,$script,[Text.Encoding]::UTF8)
function RunOld($name){
    $work=Join-Path $root $name;[void][IO.Directory]::CreateDirectory($work)
    $old=Join-Path $PSScriptRoot ($OldDirectory+'\Lan\LanDriverService.exe')
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath -OldAssembly $old -Url ('http://localhost:'+$cfg.Port) -Root $work
    if($LASTEXITCODE-ne 0){throw ('Old client '+$name+' failed')}
}
try{
    $server.Start()
    for($i=0;$i-lt 100;$i++){try{$h=Invoke-RestMethod ('http://localhost:'+$cfg.Port+'/api/health');if($h.RepoLoaded-and -not $h.ImportRunning){break}}catch{};Start-Sleep -Milliseconds 100}
    RunOld 'local'
    $instance=Get-Content (Join-Path $PSScriptRoot ($OutputDirectory+'\mysql-test\instance.json')) -Raw|ConvertFrom-Json
    [void][UDA.ServerStorage]::Migrate($cfg.RepoRoot,('Server=127.0.0.1;Port='+$instance.port+';Database=uda_test;User ID=root;SslMode=None;Connection Timeout=2'))
    RunOld 'mysql'
    Write-Output 'PASS: old deployed client needs no rebuild or Token change after database migration'
    Write-Output ('ARTIFACTS: '+$root)
}finally{$server.Dispose();$logger.Dispose()}
