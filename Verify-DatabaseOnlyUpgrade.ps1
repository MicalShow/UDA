[CmdletBinding()]param([string]$Repository,[string]$Connection,[string]$Source,[string]$WorkDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Assert-Upgrade($condition,$message){if(-not $condition){throw $message};Write-Output ('PASS: '+$message)}
$failed=$false;try{[UDA.ServerStorage]::RequireMigratedDatabase($Repository)}catch{$failed=$true}
Assert-Upgrade $failed 'file-backed deployment is rejected before upgrade'
$baseManifest=[UDA.OfflineRepoGenerator]::TryLoadManifest($Repository)
$dedicatedSource=$baseManifest.Entries[0].SourceName
$generic=Join-Path $WorkDirectory 'generic';[void][IO.Directory]::CreateDirectory($generic)
$genericInf=([IO.File]::ReadAllText((Join-Path $Source 'tls-test.inf'))).Replace('1.0.0.0','99.0.0.0')
[IO.File]::WriteAllText((Join-Path $generic 'generic.inf'),$genericInf)
$generator=New-Object UDA.OfflineRepoGenerator((New-Object UDA.TrustingSignatureVerifier),$null)
[void]$generator.AppendOrganizedSource($Repository,$generic,[Threading.CancellationToken]::None,$null)
[void][UDA.OfflineRepoGenerator]::PrepareDownloadPackages($Repository,$null)
[void][UDA.ServerStorage]::Migrate($Repository,$Connection)
[UDA.ServerStorage]::RequireMigratedDatabase($Repository)
Assert-Upgrade $true 'live MySQL dataset passes upgrade preflight'
# Deployment AI configuration must stay local, including its machine-bound key.
$aiConfig=New-Object UDA.AiSubscription
$aiConfig.Name='upgrade-preservation';$aiConfig.BaseUrl='https://example.invalid/v1';$aiConfig.Model='test'
[UDA.AiSubscriptionStore]::Save($Repository,$aiConfig,'UDA-UPGRADE-TEST-KEY')
$aiConfigPath=Join-Path $Repository 'ai-subscription.json'
$aiConfigOriginal=[IO.File]::ReadAllText($aiConfigPath)
# Simulate the incomplete migration produced by an older service.
$missed=Join-Path $Repository 'ai-preflight.json'
[IO.File]::WriteAllText($missed,'{"result":"补迁测试"}')
$before=([UDA.ServerStorage]::Config($Repository)).RepositoryId
$check=[UDA.ServerStorage]::PrepareCompleteMigration($Repository,$Connection,$false)
Assert-Upgrade ($check.Contains('max_allowed_packet') -and (Test-Path -LiteralPath $missed)) 'precheck inspects packet size without removing local data'
$prepared=[UDA.ServerStorage]::PrepareCompleteMigration($Repository,$Connection,$true)
Assert-Upgrade (([UDA.ServerStorage]::Documents($Repository,'repo.ai-preflight.json')).Count -eq 1) 'complete preparation backfills previously omitted AI data'
Assert-Upgrade (([UDA.ServerStorage]::Documents($Repository,'repo.index-state.json')).Count -eq 1) 'complete preparation uploads hardware index state'
Assert-Upgrade ((Test-Path -LiteralPath $missed) -and (Test-Path -LiteralPath (Join-Path $Repository 'repo.udadb')) -and -not ([UDA.ServerStorage]::Config($Repository)).DatabaseOnly) 'preparation retains originals and old server mode'
[UDA.ServerStorage]::Write($Repository,'repo.ai-preflight.json','{"result":"new SQL value"}',$false)
[IO.File]::WriteAllText($missed,'{"result":"stale file"}')
[void][UDA.ServerStorage]::PrepareCompleteMigration($Repository,$Connection,$true)
Assert-Upgrade (([UDA.ServerStorage]::Read($Repository,'repo.ai-preflight.json')) -eq '{"result":"new SQL value"}' -and ([UDA.ServerStorage]::Config($Repository)).RepositoryId -eq $before) 'repeated preparation preserves SQL changes and repository identity'

$policy=New-Object UDA.MachineDriverPackage
$policy.Manufacturer='Test Maker';$policy.Model='Model A';$policy.SourceName=$dedicatedSource;$policy.Architecture='amd64';$policy.MajorVersion=10;$policy.MaxBuild=21999
$policies=New-Object 'Collections.Generic.List[UDA.MachineDriverPackage]';$policies.Add($policy)
[UDA.MachineDriverPackages]::Save($Repository,$policies)
$target=New-Object UDA.OsTarget;$target.Architecture='amd64';$target.MajorVersion=10;$target.BuildNumber=19045
Assert-Upgrade (([UDA.MachineDriverPackages]::Select($policies,' test   maker ','MODEL A',$target)).SourceName -eq $dedicatedSource) 'model matching normalizes whitespace and case without fuzzy matching'
Assert-Upgrade ($null -eq [UDA.MachineDriverPackages]::Select($policies,'Test Maker','Model AB',$target)) 'different models do not accidentally match'
$target.BuildNumber=26100
Assert-Upgrade ($null -eq [UDA.MachineDriverPackages]::Select($policies,'Test Maker','Model A',$target)) 'known model without matching OS falls back to generic drivers'
$target.BuildNumber=0
Assert-Upgrade ($null -eq [UDA.MachineDriverPackages]::Select($policies,'Test Maker','Model A',$target)) 'unknown Windows build skips build-specific model package'
$policies.Add($policy);$failed=$false;try{[UDA.MachineDriverPackages]::Save($Repository,$policies)}catch{$failed=$true};$policies.RemoveAt(1)
# Separate duplicate objects exercise the overlap guard rather than reference identity.
$duplicate=New-Object UDA.MachineDriverPackage;$duplicate.Manufacturer=$policy.Manufacturer;$duplicate.Model=$policy.Model;$duplicate.SourceName=$policy.SourceName;$duplicate.MaxBuild=21999
$policies.Add($duplicate);$failed=$false;try{[UDA.MachineDriverPackages]::Save($Repository,$policies)}catch{$failed=$true};$policies.RemoveAt(1)
Assert-Upgrade $failed 'overlapping model and operating-system mappings are rejected'
$manifest=[UDA.OfflineRepoGenerator]::TryLoadManifest($Repository)
$entry=$manifest.Entries | Where-Object {$_.SourceName-eq $dedicatedSource} | Select-Object -First 1
$archive=[UDA.OfflineRepoGenerator]::DownloadArchivePath($Repository,$entry)
$backup=Join-Path $WorkDirectory 'original-driver-archive.zip';[IO.File]::Copy($archive,$backup,$true)
try{
 $zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Update)
 try{$bad=$zip.Entries | Where-Object {$_.FullName.EndsWith('.inf')} | Select-Object -First 1;$name=$bad.FullName;$bad.Delete();$writer=New-Object IO.StreamWriter($zip.CreateEntry($name).Open());$writer.Write('corrupt INF');$writer.Dispose()}finally{$zip.Dispose()}
 $failed=$false;try{[void][UDA.DatabaseArchiveRepository]::Compact($Repository)}catch{$failed=$true}
 Assert-Upgrade ($failed -and (Test-Path -LiteralPath (Join-Path $Repository 'packages')) -and -not ([UDA.ServerStorage]::Config($Repository)).DatabaseOnly) 'corrupt driver ZIP blocks cleanup and preserves raw drivers'
}finally{[IO.File]::Copy($backup,$archive,$true)}
$reclaimed=[UDA.DatabaseArchiveRepository]::Compact($Repository)
Assert-Upgrade ((Test-Path -LiteralPath $aiConfigPath) -and [IO.File]::ReadAllText($aiConfigPath)-eq $aiConfigOriginal) 'database activation preserves local AI configuration and encrypted key byte-for-byte'
Assert-Upgrade (([UDA.ServerStorage]::Documents($Repository,'repo.ai-subscription.json')).Count-eq 0) 'AI deployment configuration is not imported as business data'
[UDA.ServerStorage]::ActivateDatabaseOnly($Repository)
Assert-Upgrade ([IO.File]::ReadAllText($aiConfigPath)-eq $aiConfigOriginal) 'repeat database-only upgrade accepts local AI configuration without requiring a SQL shadow row'
# Actual SQL-backed independent AI workers and durable restart recovery.
Add-Type -TypeDefinition 'using System; using System.Threading; public static class UdaSqlAiWorker { public static string Run(string kind, CancellationToken token, Action<string> progress) { progress("SQL lifecycle worker started"); while(true){token.ThrowIfCancellationRequested();progress("SQL lifecycle checkpoint");Thread.Sleep(20);} } }'
$aiRunner=New-Object UDA.AiDriverTaskRunner($Repository,[Func[UDA.OfflineRepo]]{return $null},[Threading.CancellationToken]::None)
$aiField=$aiRunner.GetType().GetField('TestRun',[Reflection.BindingFlags]'Instance,NonPublic')
$aiField.SetValue($aiRunner,[Delegate]::CreateDelegate($aiField.FieldType,[UdaSqlAiWorker].GetMethod('Run')))
try {
 Assert-Upgrade ($aiRunner.Begin('releases',$false) -and $aiRunner.Begin('news',$false)) 'SQL-backed AI jobs run independently'
 $aiStatusJson=New-Object Web.Script.Serialization.JavaScriptSerializer
 $aiStatus=$aiStatusJson.DeserializeObject($aiStatusJson.Serialize($aiRunner.Status()))
 foreach($task in $aiStatus['tasks']){[void]$aiRunner.Control($task['id'],'cancel')}
 for($i=0;$i-lt 300 -and $aiRunner.IsRunning;$i++){Start-Sleep -Milliseconds 10}
 Assert-Upgrade (-not $aiRunner.IsRunning) 'SQL-backed cancelled jobs release active status'
} finally {$aiRunner.Dispose()}
for($aiWait=0;$aiWait-lt 300;$aiWait++){
 $aiRows=[UDA.ServerStorage]::Documents($Repository,'repo.ai-task.')
 if($aiRows.Count-eq 2){break}
 Start-Sleep -Milliseconds 20
}
Assert-Upgrade ($aiRows.Count-eq 2 -and -not (Get-ChildItem -LiteralPath $Repository -Filter 'repo.ai-task.*.json')) 'AI task progress and logs persist in MySQL without local business files'
$interruptedAiId=[Guid]::NewGuid().ToString('N')
[UDA.ServerStorage]::Write($Repository,('repo.ai-task.'+$interruptedAiId+'.json'),('{"id":"'+$interruptedAiId+'","action":"news","state":"running","startedUtc":"'+[DateTime]::UtcNow.ToString('o')+'"}'),$false)
$aiRecovered=New-Object UDA.AiDriverTaskRunner($Repository,[Func[UDA.OfflineRepo]]{return $null},[Threading.CancellationToken]::None)
try {$aiRecovered.Start();Assert-Upgrade (-not $aiRecovered.IsRunning -and $aiStatusJson.Serialize($aiRecovered.Status()).Contains('interrupted')) 'SQL task recovery marks stale running records interrupted after restart'} finally {$aiRecovered.Dispose()}
$autoRunner=New-Object UDA.AiDriverTaskRunner($Repository,[Func[UDA.OfflineRepo]]{return $null},[Threading.CancellationToken]::None)
try {
 $planEnvelope=$aiStatusJson.DeserializeObject($aiStatusJson.Serialize($autoRunner.ScheduleStatus()))
 $autoPlan=$aiStatusJson.Deserialize($aiStatusJson.Serialize($planEnvelope['settings']),[UDA.AiAutoTaskSettings])
 $autoPlan.enabled=$false;$autoPlan.tasksEnabled=$false;$autoPlan.news.frequency='weekly';$autoPlan.news.time='22:15';$autoPlan.news.weekDay=5
 $autoPlan.news.deviceTypes=New-Object 'Collections.Generic.List[string]';$autoPlan.news.deviceTypes.Add('display')
 $autoRunner.SaveSchedule($autoPlan)
 Assert-Upgrade (-not (Test-Path -LiteralPath (Join-Path $Repository 'repo.ai-auto-schedule.json')) -and ([UDA.ServerStorage]::Documents($Repository,'repo.ai-auto-schedule.json')).Count-eq 1) 'automatic task plan and device scope live only in MySQL'
} finally {$autoRunner.Dispose()}
$autoRestart=New-Object UDA.AiDriverTaskRunner($Repository,[Func[UDA.OfflineRepo]]{return $null},[Threading.CancellationToken]::None)
try {
 $reloaded=$aiStatusJson.DeserializeObject($aiStatusJson.Serialize($autoRestart.ScheduleStatus()))
 Assert-Upgrade (-not $reloaded['settings']['enabled'] -and -not $reloaded['settings']['tasksEnabled'] -and $reloaded['settings']['news']['time']-eq '22:15' -and $reloaded['settings']['news']['deviceTypes'][0]-eq 'display') 'restart restores disabled automatic plan, execution time and chosen device types from SQL'
} finally {$autoRestart.Dispose()}
$historyEvent=New-Object UDA.DriverInstallEvent;$historyEvent.EventId=[Guid]::NewGuid().ToString('N');$historyEvent.RunId=[Guid]::NewGuid().ToString('N');$historyEvent.Machine='Historical-Lab-PC';$historyEvent.Status='log';$historyEvent.Detail='Preserved synthetic historical run';$historyEvent.Utc='2026-10-01T01:00:00Z';$historyEvent.CountInUsage=$false;$historyEvent.RecoverySource='Legacy imported log';$historyEvent.MachineId=('c'*64)
$historyBatch=New-Object 'Collections.Generic.List[UDA.DriverInstallEvent]';$historyBatch.Add($historyEvent)
$pageIndexType=[UDA.ServerStorage].Assembly.GetType('UDA.InstallLogPages')
$pageIndex=[Activator]::CreateInstance($pageIndexType,[object[]]@($Repository))
$readPages=$pageIndexType.GetMethod('Read')
$beforeIndex=$readPages.Invoke($pageIndex,[object[]]@($false))
$beforeCount=$beforeIndex.GetType().GetField('EventCount').GetValue($beforeIndex)
$firstHistory=[UDA.ServerStorage]::ImportHistoricalEvents($Repository,$historyBatch)
$repeatHistory=[UDA.ServerStorage]::ImportHistoricalEvents($Repository,$historyBatch)
Assert-Upgrade ($firstHistory[0]-eq 1 -and $repeatHistory[1]-eq 1 -and -not (Test-Path -LiteralPath (Join-Path $Repository ('install-events\'+$historyEvent.EventId+'.json')))) 'historical recovery is SQL-only, transactional and idempotent'
Assert-Upgrade (([UDA.ServerStorage]::Events($Repository) | Where-Object {$_ -like '*Historical-Lab-PC*'}).Count-eq 1) 'original historical run is retained once with original time'
$afterIndex=$readPages.Invoke($pageIndex,[object[]]@($false))
$fullReads=$pageIndexType.GetField('FullReads',[Reflection.BindingFlags]'NonPublic,Instance').GetValue($pageIndex)
Assert-Upgrade ($fullReads-eq 1 -and $afterIndex.GetType().GetField('EventCount').GetValue($afterIndex)-eq $beforeCount+1) 'SQL historical commit updates warm page directory incrementally without rereading all bodies'
$detail=$afterIndex.GetType().GetMethod('Details').Invoke($afterIndex,[object[]]@($Repository,$historyEvent.RunId,'',0,100))
Assert-Upgrade ($aiStatusJson.Serialize($detail).Contains('Preserved synthetic historical run')) 'full session body loads from MySQL by event keys after metadata-only paging'
$beforeMachines=$beforeIndex.GetType().GetField('Machines').GetValue($beforeIndex)
$afterMachines=$afterIndex.GetType().GetField('Machines').GetValue($afterIndex)
Assert-Upgrade ($afterMachines-eq $beforeMachines+1) 'legacy recovered runtime log counts its machine despite the old false usage flag'
$priorHardware=$afterIndex.GetType().GetField('Hardware').GetValue($afterIndex)
$priorUpdates=$afterIndex.GetType().GetField('Updates').GetValue($afterIndex)
$successHistory=New-Object UDA.DriverInstallEvent
$successHistory.EventId=[Guid]::NewGuid().ToString('N');$successHistory.RunId=[Guid]::NewGuid().ToString('N');$successHistory.Machine=$historyEvent.Machine;$successHistory.MachineId=$historyEvent.MachineId
$successHistory.Status='updated';$successHistory.Device='HISTORICAL-NIC';$successHistory.CountInUsage=$false;$successHistory.RecoverySource='Legacy imported success';$successHistory.Utc=$historyEvent.Utc
[UDA.ServerStorage]::Write($Repository,('install-events/'+$successHistory.EventId+'.json'),$aiStatusJson.Serialize($successHistory),$true)
$counted=$readPages.Invoke($pageIndex,[object[]]@($false))
Assert-Upgrade ($counted.GetType().GetField('Machines').GetValue($counted)-eq $afterMachines -and $counted.GetType().GetField('Hardware').GetValue($counted)-eq $priorHardware+1 -and $counted.GetType().GetField('Updates').GetValue($counted)-eq $priorUpdates+1) 'legacy successful update participates in SQL-backed device/driver usage with machine deduplication'
$nextHistory=New-Object UDA.DriverInstallEvent
$nextHistory.EventId=[Guid]::NewGuid().ToString('N');$nextHistory.RunId=[Guid]::NewGuid().ToString('N');$nextHistory.Machine=$successHistory.Machine;$nextHistory.MachineId=$successHistory.MachineId;$nextHistory.Device=$successHistory.Device
$nextHistory.Status='updated';$nextHistory.RecoverySource=$successHistory.RecoverySource;$nextHistory.CountInUsage=$false;$nextHistory.Utc='2026-10-28T10:00:00Z'
[UDA.ServerStorage]::Write($Repository,('install-events/'+$nextHistory.EventId+'.json'),$aiStatusJson.Serialize($nextHistory),$true)
$twice=$readPages.Invoke($pageIndex,[object[]]@($false))
Assert-Upgrade ($twice.GetType().GetField('Machines').GetValue($twice)-eq $afterMachines -and $twice.GetType().GetField('Hardware').GetValue($twice)-eq $priorHardware+2 -and $twice.GetType().GetField('Updates').GetValue($twice)-eq $priorUpdates+2) 'same SQL-backed machine/device updated in two tasks counts two operations with one physical computer'
$nextHistory.EventId=[Guid]::NewGuid().ToString('N')
[UDA.ServerStorage]::Write($Repository,('install-events/'+$nextHistory.EventId+'.json'),$aiStatusJson.Serialize($nextHistory),$true)
$repeated=$readPages.Invoke($pageIndex,[object[]]@($false))
Assert-Upgrade ($repeated.GetType().GetField('Hardware').GetValue($repeated)-eq $priorHardware+2 -and $repeated.GetType().GetField('Updates').GetValue($repeated)-eq $priorUpdates+2) 'repeated success fragments within the same SQL-backed task remain deduplicated'







Assert-Upgrade ($reclaimed -gt 0 -and -not (Test-Path -LiteralPath (Join-Path $Repository 'packages'))) 'verified driver archives replace raw packages and reclaim space'
Assert-Upgrade (-not (Test-Path -LiteralPath (Join-Path $Repository 'repo.manifest.json')) -and -not (Test-Path -LiteralPath (Join-Path $Repository 'repo.udadb'))) 'catalog and binary hardware index no longer remain as persistent local copies'
Assert-Upgrade (([UDA.ServerStorage]::Config($Repository)).DatabaseOnly -and $null-ne [UDA.ServerStorage]::Read($Repository,'repo.index-state.json')) 'SQL contains hardware index blocks and database-only state'
$archiveJson=New-Object Web.Script.Serialization.JavaScriptSerializer
$recordsBefore=$archiveJson.DeserializeObject([UDA.ServerStorage]::Read($Repository,'repo.archive-verification.json'))
Assert-Upgrade ($recordsBefore.Count-ge 1 -and -not (Test-Path -LiteralPath (Join-Path $Repository 'repo.archive-verification.json'))) 'archive verification checkpoints persist in MySQL only'
[void][UDA.DatabaseArchiveRepository]::Compact($Repository)
$recordsAfter=$archiveJson.DeserializeObject([UDA.ServerStorage]::Read($Repository,'repo.archive-verification.json'))
$reused=$true;foreach($key in $recordsBefore.Keys){if($recordsBefore[$key].VerifiedUtc-ne $recordsAfter[$key].VerifiedUtc -or [string]::IsNullOrEmpty($recordsAfter[$key].FileState)){$reused=$false}}
Assert-Upgrade $reused 'unchanged verified archives skip repeat verification on the next installation'
$modelBefore=[UDA.ServerStorage]::Read($Repository,'repo.model-archive-verification.json')
[void][UDA.DatabaseArchiveRepository]::Compact($Repository)
Assert-Upgrade ($modelBefore-eq [UDA.ServerStorage]::Read($Repository,'repo.model-archive-verification.json')) 'unchanged derived model packages are reused without recompression'

$oldWrite=[IO.File]::GetLastWriteTimeUtc($archive);$oldLength=(Get-Item -LiteralPath $archive).Length
try{
 $damage=[IO.File]::Open($archive,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$damage.Position=0;$damage.WriteByte(0)}finally{$damage.Dispose()}
 [IO.File]::SetLastWriteTimeUtc($archive,$oldWrite)
 Assert-Upgrade ((Get-Item -LiteralPath $archive).Length-eq $oldLength -and [IO.File]::GetLastWriteTimeUtc($archive)-eq $oldWrite) 'corruption test preserves file length and restores last-write timestamp'
 $failed=$false;try{[void][UDA.DatabaseArchiveRepository]::Compact($Repository)}catch{$failed=$true}
 Assert-Upgrade $failed 'native file change identity prevents stale verification reuse for a modified archive even with restored timestamp'
}finally{[IO.File]::Copy($backup,$archive,$true)}
$replacement=$archive+'.replacement-'+[Guid]::NewGuid().ToString('N')
[IO.File]::Copy($backup,$replacement,$true)
[IO.File]::Replace($replacement,$archive,($replacement+'.old'))
[void][UDA.DatabaseArchiveRepository]::Compact($Repository)
$recordsFresh=$archiveJson.DeserializeObject([UDA.ServerStorage]::Read($Repository,'repo.archive-verification.json'))
$archiveKey=[IO.Path]::GetFileName($archive)
Assert-Upgrade ($recordsFresh[$archiveKey].VerifiedUtc-ne $recordsBefore[$archiveKey].VerifiedUtc) 'changed or replaced archive is completely verified again'
[UDA.ServerStorage]::Write($Repository,'repo.archive-verification.json','{}',$false)
[void][UDA.DatabaseArchiveRepository]::Compact($Repository)
$recordsRebuilt=$archiveJson.DeserializeObject([UDA.ServerStorage]::Read($Repository,'repo.archive-verification.json'))
Assert-Upgrade ($recordsRebuilt.Count-eq $recordsBefore.Count -and $recordsRebuilt[[IO.Path]::GetFileName($archive)].VerifiedUtc-ne $recordsFresh[[IO.Path]::GetFileName($archive)].VerifiedUtc) 'missing verification records trigger a full verification rather than a skip'
try{
 $zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Update)
 try{$writer=New-Object IO.StreamWriter($zip.CreateEntry('unexpected-payload.sys').Open());$writer.Write('changed binary payload, INF unchanged');$writer.Dispose()}finally{$zip.Dispose()}
 $failed=$false;try{[void][UDA.DatabaseArchiveRepository]::Compact($Repository)}catch{$failed=$true}
 Assert-Upgrade $failed 'changed non-INF payload cannot replace an already published archive fingerprint'
}finally{[IO.File]::Copy($backup,$archive,$true)}
[void][UDA.DatabaseArchiveRepository]::Compact($Repository)


$staleCatalog=Join-Path $Repository 'repo.manifest.json';[IO.File]::WriteAllText($staleCatalog,'invalid stale file cache')
[void][UDA.DatabaseArchiveRepository]::Compact($Repository)
Assert-Upgrade (-not (Test-Path -LiteralPath $staleCatalog) -and $null-ne [UDA.OfflineRepoGenerator]::TryLoadManifest($Repository)) 'repeat upgrade removes stale local catalog without overwriting SQL and safely rebuilds a missing local index'
$opened=[UDA.OfflineRepo]::Open($Repository,$null)
try{Assert-Upgrade ($opened.Count -ge 2 -and $opened.VerifyFile($entry.RelativePath)) 'repository loads from MySQL and verifies INF inside ZIP without raw files'}finally{$opened.Dispose()}
$modelArchive=[UDA.MachineDriverPackages]::Archive($Repository,$dedicatedSource)
$modelZip=[IO.Compression.ZipFile]::OpenRead($modelArchive)
try{Assert-Upgrade ($null-ne $modelZip.GetEntry($entry.RelativePath)) 'whole-model ZIP preserves repository-relative INF paths'}finally{$modelZip.Dispose()}
$failed=$false;try{[UDA.ServerStorage]::UseLocal($Repository)}catch{$failed=$true}
Assert-Upgrade $failed 'database-only release refuses switching back to file storage'
$storageFile=Join-Path $Repository '.uda-storage.json';$serializer=New-Object Web.Script.Serialization.JavaScriptSerializer
$original=[IO.File]::ReadAllText($storageFile);$broken=$serializer.DeserializeObject($original)
$broken.ProtectedConnection=[Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('Server=127.0.0.1;Port=1;User ID=test;Connection Timeout=1;Pooling=False'),$null,[Security.Cryptography.DataProtectionScope]::LocalMachine))
$poison=Join-Path $Repository 'repo.manifest.json';[IO.File]::WriteAllText($poison,'stale local catalog')
try{
 [IO.File]::WriteAllText($storageFile,$serializer.Serialize($broken));$readFailed=$false;$writeFailed=$false
 try{[void][UDA.ServerStorage]::Read($Repository,'repo.manifest.json')}catch{$readFailed=$true}
 try{[UDA.ServerStorage]::Write($Repository,'repo.failure-test.json','{}',$false)}catch{$writeFailed=$true}
 Assert-Upgrade ($readFailed -and $writeFailed -and -not (Test-Path -LiteralPath (Join-Path $Repository 'repo.failure-test.json'))) 'database failure cannot silently fall back to stale files or create a second data source'
}finally{[IO.File]::WriteAllText($storageFile,$original);[IO.File]::Delete($poison)}
$listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
$cfg=New-Object UDA.LanServerConfig;$cfg.RepoRoot=$Repository;$cfg.Host='localhost';$cfg.Port=$port;$cfg.AdminCredentialsFile=Join-Path $WorkDirectory 'test-admin.json'
$server=New-Object UDA.LanDriverServer($cfg,$null)
[UDA.LanDriverServer].GetField('TestWatchDebounceMs',[Reflection.BindingFlags]'Instance,NonPublic').SetValue($server,600000)
function Report-Model($model,$build){
 $report=New-Object UDA.LanReportRequest;$report.RequestId=[Guid]::NewGuid().ToString('N');$report.MachineManufacturer='Test Maker';$report.MachineModel=$model;$report.OsArchitecture='amd64';$report.OsMajorVersion=10;$report.OsBuildNumber=$build
 $device=New-Object UDA.LanDeviceDto;$device.DeviceInstanceId='TEST-DEVICE';$device.HardwareIds.Add('PCI\VEN_FFFF&DEV_F001');$report.Devices.Add($device)
 $request=[Net.HttpWebRequest]::Create('http://localhost:'+$port+'/api/report');$request.Proxy=$null;$request.Method='POST';$request.Headers['X-UDA-Protocol']=[string][UDA.LanProtocol]::Version;$request.ContentType='application/json';$bytes=[Text.Encoding]::UTF8.GetBytes($serializer.Serialize($report));$request.ContentLength=$bytes.Length;$stream=$request.GetRequestStream();$stream.Write($bytes,0,$bytes.Length);$stream.Dispose()
 try{$response=$request.GetResponse()}catch [Net.WebException]{$response=$_.Exception.Response;if($null-eq $response){throw}}
 try{$reader=New-Object IO.StreamReader($response.GetResponseStream());return @{Status=[int]$response.StatusCode;Data=$serializer.DeserializeObject($reader.ReadToEnd())}}finally{$response.Dispose()}
}
try{
 # Emulate a reboot where SQL is temporarily unreachable, with no local manifest fallback.
 [IO.File]::WriteAllText($storageFile,$serializer.Serialize($broken))
 try {
  $server.Start()
  $startupHealth=$null
  for($i=0;$i-lt 100;$i++){
   $startupHealth=Invoke-RestMethod ('http://localhost:'+$port+'/api/health')
   if($startupHealth.ImportStage-eq 'failed'){break}
   Start-Sleep -Milliseconds 100
  }
  Assert-Upgrade (-not $startupHealth.RepoLoaded -and $startupHealth.ImportStage-eq 'failed' -and $startupHealth.RepoRetryUtc) 'SQL-only startup outage is reported as failed and schedules automatic recovery'
 } finally { [IO.File]::WriteAllText($storageFile,$original) }
 for($i=0;$i-lt 300 -and -not $server.RepoLoaded;$i++){Start-Sleep -Milliseconds 100}
 Assert-Upgrade $server.RepoLoaded 'restoring SQL connectivity automatically loads the repository without restarting the service'
 $notice=New-Object UDA.DriverNotice;$notice.Hardware='Synthetic SQL Hardware 4070';$notice.Version='580.00';$notice.Title='Synthetic driver security fix';$notice.Detail='Synthetic fixture';$notice.Critical=$true;$notice.SourceUrl='https://www.nvidia.com/';$notice.Evidence='Synthetic evidence'
 [void][UDA.DriverNoticeStore]::Add($Repository,$notice)
 [void][UDA.DriverNoticeStore]::Add($Repository,$notice)
 $sqlNotices=[UDA.DriverNoticeStore]::Items($Repository)
 Assert-Upgrade ($sqlNotices.Count-eq 1 -and $sqlNotices[0].Critical -and -not (Test-Path -LiteralPath (Join-Path $Repository ('repo.driver-notice.'+$notice.Id+'.json')))) 'driver notices are deduplicated and stored in MySQL without local business files'
 [UDA.DriverNoticeStore]::MarkRead($Repository,'admin-a',[string[]]@($notice.Id))
 Assert-Upgrade ([UDA.DriverNoticeStore]::ReadIds($Repository,'admin-a').Contains($notice.Id) -and -not [UDA.DriverNoticeStore]::ReadIds($Repository,'admin-b').Contains($notice.Id)) 'MySQL persists independent administrator read receipts'
 $specific=Report-Model 'Model A' 19045
 Assert-Upgrade ($specific.Status-eq 200 -and $specific.Data.Candidates.Count-eq 1 -and $specific.Data.Candidates[0].DriverVerVersion-eq '1.0.0.0' -and $specific.Data.Candidates[0].PackageId.StartsWith('model-')) 'specific model uses its approved older package instead of newer generic drivers'
 $general=Report-Model 'Unknown Model' 19045
 Assert-Upgrade ($general.Status-eq 200 -and $general.Data.Candidates[0].DriverVerVersion-eq '99.0.0.0' -and $general.Data.Candidates[0].PackageId-eq '') 'unmapped model uses generic source and excludes dedicated sources'
 $unsupported=Report-Model 'Model A' 26100
 Assert-Upgrade ($unsupported.Status-eq 200 -and $unsupported.Data.Candidates[0].DriverVerVersion-eq '99.0.0.0' -and $unsupported.Data.Candidates[0].PackageId-eq '') 'model without requested OS package uses generic drivers through actual report API'
 $cookies=New-Object Net.CookieContainer
 function Upload-Request($path,$body,$method='POST'){
  $request=[Net.HttpWebRequest]::Create('http://localhost:'+$port+$path);$request.Proxy=$null;$request.CookieContainer=$cookies;$request.Method=$method;$request.Headers['X-UDA-Protocol']=if($path-eq '/api/admin/drivers/delete' -or $path-eq '/api/admin/organize' -or $path-eq '/api/admin/dedupe' -or $path.StartsWith('/api/admin/import?')){[string][UDA.LanProtocol]::Version}else{'1'}
  if($null-ne $body){$bytes=[Text.Encoding]::UTF8.GetBytes($body);$request.ContentType='application/json';$request.ContentLength=$bytes.Length;$stream=$request.GetRequestStream();$stream.Write($bytes,0,$bytes.Length);$stream.Dispose()}
  try{$response=$request.GetResponse()}catch [Net.WebException]{if($_.Exception.Response){$failureResponse=$_.Exception.Response;try{$failureReader=New-Object IO.StreamReader($failureResponse.GetResponseStream());throw ($path+': '+$failureReader.ReadToEnd())}finally{$failureResponse.Dispose()}};throw}
  try{$reader=New-Object IO.StreamReader($response.GetResponseStream());return $serializer.DeserializeObject($reader.ReadToEnd())}finally{$response.Dispose()}
 }
 [void](Upload-Request '/api/admin/login' '{"user":"tls-admin","password":"Isolated-Admin-Password-123!"}')
 $modelEditor=Upload-Request '/api/admin/machine-packages' $null 'GET'
 Assert-Upgrade ($modelEditor.observed.Count-ge 2 -and $modelEditor.sources.Count-gt 0) 'authenticated model editor reads reported manufacturers and models from SQL for exact autofill'
 $upload=Upload-Request '/api/admin/upload/start' '{"FileCount":1,"TotalBytes":5}';$uploadRoot=[UDA.WebAdminController]::UploadedBatchRoot($Repository,$upload.batch)
 Assert-Upgrade (-not (Test-Path -LiteralPath (Join-Path $uploadRoot 'session.json')) -and $null-ne [UDA.ServerStorage]::Read($Repository,('repo.upload.'+$upload.batch+'.session.json'))) 'new upload session inventory is stored only in MySQL'
 $uploadQuery='?batch='+$upload.batch+'&path=test.inf&total=5&stamp=test-stamp'
 [void](Upload-Request ('/api/admin/upload/chunk'+$uploadQuery+'&offset=0') 'hello')
 $resume=Upload-Request ('/api/admin/upload/status'+$uploadQuery) $null 'GET'
 Assert-Upgrade ($resume.complete -and $resume.received-eq 5 -and @([UDA.ServerStorage]::Documents($Repository,('repo.upload.'+$upload.batch+'.')).Keys).Count-eq 2) 'chunk resume state is stored in SQL while temporary upload bytes remain on disk'
 [void](Upload-Request ('/api/admin/upload/discard?batch='+$upload.batch) '{}')
 Assert-Upgrade (-not (Test-Path -LiteralPath $uploadRoot) -and [UDA.ServerStorage]::Documents($Repository,('repo.upload.'+$upload.batch+'.')).Count-eq 0) 'discard clears both temporary uploaded bytes and SQL session state'
 $request=[Net.HttpWebRequest]::Create('http://localhost:'+$port+'/api/download?path='+[Uri]::EscapeDataString($specific.Data.Candidates[0].InfRelativePath));$request.Proxy=$null;$request.Headers['X-UDA-Protocol']=[string][UDA.LanProtocol]::Version
 $response=$request.GetResponse();try{$data=New-Object IO.MemoryStream;$response.GetResponseStream().CopyTo($data);$data.Position=0;$zip=New-Object IO.Compression.ZipArchive($data,[IO.Compression.ZipArchiveMode]::Read);try{Assert-Upgrade ($null-ne $zip.GetEntry($specific.Data.Candidates[0].PackageInfPath)) 'actual client download serves whole model archive after raw folders are removed'}finally{$zip.Dispose();$data.Dispose()}}finally{$response.Dispose()}

 [IO.File]::WriteAllText((Join-Path $Source 'model-readme.txt'),'model package ancillary file')
 $mapping=@{Manufacturer='Test Maker';Model='Imported Model';Architecture='amd64';MajorVersion=10;MinorVersion=0;MinBuild=0;MaxBuild=21999}
 $imported=Upload-Request '/api/admin/machine-packages/import' ($serializer.Serialize(@{Source=$Source;Mapping=$mapping}))
 $importPolicy=$imported.items | Where-Object {$_.Model-eq 'Imported Model'}
 $whole=[UDA.MachineDriverPackages]::Archive($Repository,$importPolicy.SourceName)
 $wholeZip=[IO.Compression.ZipFile]::OpenRead($whole)
 try{Assert-Upgrade (@($wholeZip.Entries | Where-Object {$_.FullName.EndsWith('/model-readme.txt')}).Count-eq 1) 'compressed model package retains ancillary files from the entire folder'}finally{$wholeZip.Dispose()}

 Assert-Upgrade ($importPolicy.SourceName.StartsWith('Machine-') -and -not (Test-Path -LiteralPath (Join-Path $Repository 'packages'))) 'directory import creates independent dedicated compressed package and removes expanded repository files'
 $importReport=Report-Model 'Imported Model' 19045
 Assert-Upgrade ($importReport.Status-eq 200 -and $importReport.Data.Candidates.Count-eq 1 -and $importReport.Data.Candidates[0].DriverVerVersion-eq '1.0.0.0' -and $importReport.Data.Candidates[0].PackageId.StartsWith('model-')) 'independent imported model uses its own package even with identical generic content'
 $fallback=Report-Model 'Imported Model' 26100
 Assert-Upgrade ($fallback.Status-eq 200 -and $fallback.Data.Candidates[0].DriverVerVersion-eq '99.0.0.0' -and $fallback.Data.Candidates[0].PackageId-eq '') 'imported model without Windows 11 package falls back to generic'
 $mapping.MaxBuild=0;$mapping.MinBuild=22000
 [void](Upload-Request '/api/admin/machine-packages/import' ($serializer.Serialize(@{Source=$Source;Mapping=$mapping})))

 $uploadInf=[IO.File]::ReadAllText((Join-Path $Source 'tls-test.inf'));$uploadBytes=[Text.Encoding]::UTF8.GetByteCount($uploadInf)
 $batch=Upload-Request '/api/admin/upload/start' ($serializer.Serialize(@{FileCount=1;TotalBytes=$uploadBytes}))
 [void](Upload-Request ('/api/admin/upload/chunk?batch='+$batch.batch+'&path=driver.inf&total='+$uploadBytes+'&stamp=model&offset=0') $uploadInf)
 $mapping.Model='Uploaded Model'
 [void](Upload-Request '/api/admin/machine-packages/import' ($serializer.Serialize(@{Source=$batch.source;Mapping=$mapping})))
 Assert-Upgrade (-not (Test-Path -LiteralPath ([UDA.WebAdminController]::UploadedBatchRoot($Repository,$batch.batch))) -and [UDA.ServerStorage]::Documents($Repository,('repo.upload.'+$batch.batch+'.')).Count-eq 0) 'uploaded model folder imports successfully and reclaims consumed temporary files and SQL session state'

 function Wait-MachineJob($id){
  for($wait=0;$wait-lt 600;$wait++){$state=Upload-Request ('/api/admin/machine-packages/import-status?id='+$id) $null 'GET';if($state.job.State-ne 'running' -and $state.job.State-ne 'queued'){return $state.job};Start-Sleep -Milliseconds 100}
  throw 'Machine import task timeout'
 }
 $asyncMapping=@{Manufacturer='Test Maker';Model='Async SQL Model';Architecture='amd64';MajorVersion=10;MinorVersion=0;MinBuild=0;MaxBuild=21999}
 $task=Upload-Request '/api/admin/machine-packages/import/start' ($serializer.Serialize(@{Source=$Source;Mapping=$asyncMapping}))
 $taskResult=Wait-MachineJob $task.job.Id
 Assert-Upgrade ($taskResult.State-eq 'completed' -and $taskResult.Percent-eq 100 -and $taskResult.LogCount-gt 5) 'background model import reports completed progress and phase logs through actual MySQL-backed HTTP API'
 $jobKey='repo.machine-import.'+$task.job.Id+'.json'
 Assert-Upgrade ($null-ne [UDA.ServerStorage]::Read($Repository,$jobKey) -and -not (Test-Path -LiteralPath (Join-Path $Repository $jobKey)) -and [UDA.ServerStorage]::Documents($Repository,('repo.machine-import-log.'+$task.job.Id+'.')).Count-gt 0) 'task status and complete paged import logs persist in MySQL without local business files'
 $history=Upload-Request '/api/admin/machine-packages/import-status' $null 'GET'
 Assert-Upgrade (@($history.jobs | Where-Object {$_.Id-eq $task.job.Id}).Count-eq 1) 'reopened console can discover persisted import task history'
 $empty=Join-Path $WorkDirectory 'no-inf-source';[void][IO.Directory]::CreateDirectory($empty);[IO.File]::WriteAllText((Join-Path $empty 'readme.txt'),'No INF')
 $manifestBefore=[UDA.ServerStorage]::Read($Repository,'repo.manifest.json');$policiesBefore=[UDA.ServerStorage]::Read($Repository,'repo.machine-packages.json')
 $failedTask=Upload-Request '/api/admin/machine-packages/import/start' ($serializer.Serialize(@{Source=[string]$empty;Mapping=$asyncMapping}))
 $failedResult=Wait-MachineJob $failedTask.job.Id
 Assert-Upgrade ($failedResult.State-eq 'failed' -and $failedResult.Error.Contains('INF') -and (Test-Path -LiteralPath $empty)) 'failed background import retains source and exposes actionable failure instead of a generic HTTP 400'
 Assert-Upgrade ([UDA.ServerStorage]::Read($Repository,'repo.manifest.json')-eq $manifestBefore -and [UDA.ServerStorage]::Read($Repository,'repo.machine-packages.json')-eq $policiesBefore) 'failed background import restores original SQL catalog and model mappings'
 $stale=New-Object UDA.MachineImportJob;$stale.Id=[Guid]::NewGuid().ToString('N');$stale.State='running';$stale.Source=$Source;$stale.Mapping=New-Object UDA.MachineDriverPackage
 [UDA.MachineImportJobs]::Report($Repository,$stale,'simulated process restart',$true)
 $interrupted=Upload-Request ('/api/admin/machine-packages/import-status?id='+$stale.Id) $null 'GET'
 Assert-Upgrade ($interrupted.job.State-eq 'interrupted' -and $interrupted.job.Error.Length-gt 0) 'persisted running task without a live worker is marked interrupted after service restart'
 $multi=Report-Model 'Imported Model' 26100
 Assert-Upgrade ($multi.Status-eq 200 -and $multi.Data.Candidates[0].PackageId.StartsWith('model-')) 'same model supports independent Windows 10 and Windows 11 packages'
 # Establish the ordinary compact database-only baseline after the failure/recovery fixture.
 [void][UDA.DatabaseArchiveRepository]::Compact($Repository)
 # Verify a normal uploaded-source append never invalidates unrelated ZIP verification.
 $baseChecks=$serializer.DeserializeObject([UDA.ServerStorage]::Read($Repository,'repo.archive-verification.json'))
 $oldModels=@{};foreach($policyItem in [UDA.MachineDriverPackages]::Load($Repository)){$modelPath=[UDA.MachineDriverPackages]::Archive($Repository,$policyItem.SourceName,$policyItem.PackagePrefix,$false);$oldModels[$modelPath]=(Get-Item -LiteralPath $modelPath).LastWriteTimeUtc.Ticks}
 $incremental=Join-Path $WorkDirectory 'single-increment';[void][IO.Directory]::CreateDirectory($incremental)
 $incrementText=([IO.File]::ReadAllText((Join-Path $Source 'tls-test.inf'))).Replace('DEV_F001','DEV_F099').Replace('1.0.0.0','4.0.0.0')
 [IO.File]::WriteAllText((Join-Path $incremental 'increment.inf'),$incrementText)
 function Wait-Append {for($spin=0;$spin-lt 1200;$spin++){$status=Upload-Request '/api/admin/import-status' $null 'GET';if(-not $status.running){if($status.stage-ne 'completed'){throw ('Append failed: '+$status.error)};return $status};Start-Sleep -Milliseconds 100};throw 'Append timeout'}
 [void](Upload-Request ('/api/admin/import?src='+[Uri]::EscapeDataString($incremental)) '{}')
 $appendStatus=Wait-Append
 Assert-Upgrade ($appendStatus.addedInfCount-eq 1) ('actual generic import API appends only the newly supplied INF: '+$serializer.Serialize($appendStatus))
 $afterChecks=$serializer.DeserializeObject([UDA.ServerStorage]::Read($Repository,'repo.archive-verification.json'))
 $changedChecks=@($baseChecks.Keys | Where-Object {$afterChecks[$_].VerifiedUtc-ne $baseChecks[$_].VerifiedUtc -or $afterChecks[$_].ArchiveHash-ne $baseChecks[$_].ArchiveHash})
 Assert-Upgrade ($changedChecks.Count-eq 0 -and $afterChecks.Count-eq ($baseChecks.Count+1)) 'adding one raw package verifies only the new ZIP and reuses all existing verification records'
 $changedModels=@($oldModels.Keys | Where-Object {-not (Test-Path -LiteralPath $_) -or (Get-Item -LiteralPath $_).LastWriteTimeUtc.Ticks-ne $oldModels[$_]})
 Assert-Upgrade ($changedModels.Count-eq 0) 'unrelated model archives retain identity and timestamps after a generic append'
 Assert-Upgrade (-not (Test-Path -LiteralPath (Join-Path $Repository 'packages'))) 'incremental append cleans only its temporary raw input copy'
 [void](Upload-Request ('/api/admin/import?src='+[Uri]::EscapeDataString($incremental)) '{}')
 $duplicateStatus=Wait-Append
 $duplicateChecks=$serializer.DeserializeObject([UDA.ServerStorage]::Read($Repository,'repo.archive-verification.json'))
 $duplicateChanged=@($afterChecks.Keys | Where-Object {$duplicateChecks[$_].VerifiedUtc-ne $afterChecks[$_].VerifiedUtc})
 Assert-Upgrade ($duplicateStatus.addedInfCount-eq 0 -and $duplicateChanged.Count-eq 0) 'duplicate source append neither copies archived packages nor repeats archive content verification'
 foreach($policyItem in [UDA.MachineDriverPackages]::Load($Repository)){$modelPath=[UDA.MachineDriverPackages]::Archive($Repository,$policyItem.SourceName,$policyItem.PackagePrefix,$false);Assert-Upgrade ($oldModels.ContainsKey($modelPath)) 'model package identity depends on scoped content rather than whole catalog revision'}
 # Active workspaces are preserved; an interrupted workspace is recovered without touching live data.
 $recoveryCatalog=[UDA.ServerStorage]::Read($Repository,'repo.manifest.json')
 $orphan=New-Object UDA.ServerStorage+OnlineWorkspace($Repository)
 try{
  $recoverMethod=[UDA.ServerStorage].GetMethod('RecoverOnlineWorkspaces',[Reflection.BindingFlags]'NonPublic,Static')
  $recoverMethod.Invoke($null,@($Repository))
  Assert-Upgrade (Test-Path -LiteralPath $orphan.Root) 'startup recovery preserves a workspace owned by a running process'
  $ownerPath=Join-Path $orphan.Root '.owner.json';$owner=$serializer.DeserializeObject([IO.File]::ReadAllText($ownerPath));$owner['pid']=[string][int]::MaxValue
  [IO.File]::WriteAllText($ownerPath,$serializer.Serialize($owner))
  $recoverMethod.Invoke($null,@($Repository))
  Assert-Upgrade (-not (Test-Path -LiteralPath $orphan.Root) -and [UDA.ServerStorage]::Read($Repository,'repo.manifest.json')-eq $recoveryCatalog) 'startup recovery removes an interrupted private workspace and preserves the published catalog'
 }finally{$orphan.Dispose()}
 # Deterministically pause the real background worker just before publication.
 Add-Type -TypeDefinition @"
using System; using System.Threading;
public sealed class UdaOnlineMaintenanceGate {
 public readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false);
 public bool Fail;
 public Action Callback { get { return Hold; } }
 private void Hold() { Entered.Set(); if(!Release.WaitOne(20000))throw new TimeoutException("test publication gate"); if(Fail)throw new InvalidOperationException("injected failure before publication"); }
}
"@
 $hook=[UDA.LanDriverServer].GetField('TestOnlineBeforeCommit',[Reflection.BindingFlags]'Instance,NonPublic')
 function Download-CandidateHash($candidate){
  $url='http://localhost:'+$port+'/api/download?path='+[Uri]::EscapeDataString($candidate.InfRelativePath)
  if($candidate.PackageId){$url+='&package='+[Uri]::EscapeDataString($candidate.PackageId)}
  $request=[Net.HttpWebRequest]::Create($url);$request.Proxy=$null;$request.Timeout=5000;$request.Headers['X-UDA-Protocol']=[string][UDA.LanProtocol]::Version
  $response=$request.GetResponse();try{$hash=[Security.Cryptography.SHA256]::Create();try{return [Convert]::ToBase64String($hash.ComputeHash($response.GetResponseStream()))}finally{$hash.Dispose()}}finally{$response.Dispose()}
 }
 $maintSource=Join-Path $WorkDirectory 'online-input';[void][IO.Directory]::CreateDirectory($maintSource)
 $maintInf=([IO.File]::ReadAllText((Join-Path $Source 'tls-test.inf'))).Replace('DEV_F001','DEV_F100').Replace('1.0.0.0','5.0.0.0')
 [IO.File]::WriteAllText((Join-Path $maintSource 'one.inf'),$maintInf);[IO.File]::WriteAllText((Join-Path $maintSource 'duplicate.inf'),$maintInf)
 $payload=New-Object byte[] (16*1024*1024);$rng=[Security.Cryptography.RandomNumberGenerator]::Create();try{$rng.GetBytes($payload)}finally{$rng.Dispose()};[IO.File]::WriteAllBytes((Join-Path $maintSource 'payload.bin'),$payload);$payload=$null
 foreach($operation in @('append','update-version','organize','dedupe','failed-append')){
  foreach($watcher in [UDA.LanDriverServer].GetField('_sourceWatchers',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($server)){$watcher.EnableRaisingEvents=$false}
  $beforeCatalog=[UDA.ServerStorage]::Read($Repository,'repo.manifest.json')
  $candidateA=(Report-Model 'Model A' 19045).Data.Candidates[0]
  $candidateB=(Report-Model 'Unknown Model' 19045).Data.Candidates[0]
  $oldEntry=([UDA.OfflineRepoGenerator]::TryLoadManifest($Repository)).Entries | Where-Object {$_.HardwareIds-contains 'PCI\VEN_FFFF&DEV_F099' -and $_.DriverVerVersion-eq '4.0.0.0'} | Select-Object -First 1
  $candidateC=@{InfRelativePath=$oldEntry.RelativePath;PackageId=''}
  $beforeA=Download-CandidateHash $candidateA;$beforeB=Download-CandidateHash $candidateB;$beforeC=Download-CandidateHash $candidateC
  if($operation-eq 'organize'){$preOrganizationGeneric=$candidateB}
  $gate=New-Object UdaOnlineMaintenanceGate;$gate.Fail=($operation-eq 'failed-append');$hook.SetValue($server,$gate.Callback)
  try{
   if($operation-eq 'append' -or $operation-eq 'update-version' -or $operation-eq 'failed-append'){
    if($operation-eq 'update-version'){$changedInf=$maintInf.Replace('DEV_F100','DEV_F099').Replace('5.0.0.0','6.0.0.0');[IO.File]::WriteAllText((Join-Path $maintSource 'one.inf'),$changedInf);[IO.File]::WriteAllText((Join-Path $maintSource 'duplicate.inf'),$changedInf)}
    if($operation-eq 'failed-append'){[IO.File]::WriteAllText((Join-Path $maintSource 'one.inf'),$maintInf.Replace('DEV_F100','DEV_F101'))}
    [void](Upload-Request ('/api/admin/import?src='+[Uri]::EscapeDataString($maintSource)) '{}')
   }else{[void](Upload-Request ('/api/admin/'+$operation) '{}')}
   if(-not $gate.Entered.WaitOne(20000)){$failedStatus=Upload-Request '/api/admin/import-status' $null 'GET';throw ($operation+': '+$serializer.Serialize($failedStatus))}
   Assert-Upgrade $true ($operation+': actual worker reaches isolated publication checkpoint')
   Assert-Upgrade ([UDA.ServerStorage]::Read($Repository,'repo.manifest.json')-eq $beforeCatalog) ($operation+': unfinished workspace does not alter the published SQL catalog')
   $during=Upload-Request '/api/health' $null 'GET'
   Assert-Upgrade ($during.RepoLoaded -and -not $during.ImportRunning) ($operation+': ready clients are not told to pause for maintenance')
   for($probe=0;$probe-lt 3;$probe++){
    $scanA=Report-Model 'Model A' 19045;$scanB=Report-Model 'Unknown Model' 19045
    Assert-Upgrade ($scanA.Status-eq 200 -and $scanB.Status-eq 200) ($operation+': model and generic client scans continue during maintenance')
    Assert-Upgrade ((Download-CandidateHash $candidateA)-eq $beforeA -and (Download-CandidateHash $candidateB)-eq $beforeB -and (Download-CandidateHash $candidateC)-eq $beforeC) ($operation+': existing model, generic and older hardware-version archives remain byte-identical and downloadable')
   }
   $heldResponse=$null
   if($operation-eq 'update-version'){
    $large=([UDA.OfflineRepoGenerator]::TryLoadManifest($Repository)).Entries | Where-Object {$_.HardwareIds-contains 'PCI\VEN_FFFF&DEV_F100'} | Select-Object -First 1
    $largeRequest=[Net.HttpWebRequest]::Create('http://localhost:'+$port+'/api/download?path='+[Uri]::EscapeDataString($large.RelativePath));$largeRequest.Proxy=$null;$largeRequest.Timeout=5000;$largeRequest.ReadWriteTimeout=10000;$largeRequest.Headers['X-UDA-Protocol']=[string][UDA.LanProtocol]::Version
    $heldResponse=$largeRequest.GetResponse();$heldStream=$heldResponse.GetResponseStream();$firstByte=$heldStream.ReadByte()
   }
  }finally{[void]$gate.Release.Set();$hook.SetValue($server,$null)}
  if($operation-ne 'failed-append'){
   $completedMaintenance=Wait-Append
   if($operation-eq 'dedupe'){Assert-Upgrade ($completedMaintenance.duplicateInfCount-gt 0) 'online dedupe removes actual duplicate entries while preserving independent model packages'}
   if($heldResponse){
    try{$remaining=New-Object IO.MemoryStream;$heldStream.CopyTo($remaining);Assert-Upgrade ($firstByte-ge 0 -and $remaining.Length-gt 8MB) 'a real in-flight 16MiB ZIP completes across the atomic metadata switch'}finally{$heldResponse.Dispose();if($remaining){$remaining.Dispose()}}
   }
   Assert-Upgrade ((Download-CandidateHash $candidateA)-eq $beforeA -and (Download-CandidateHash $candidateB)-eq $beforeB -and (Download-CandidateHash $candidateC)-eq $beforeC) ($operation+': pre-maintenance scan results still download after the atomic switch')
  }else{
   for($spin=0;$spin-lt 100;$spin++){$failure=Upload-Request '/api/admin/import-status' $null 'GET';if(-not $failure.running){break};Start-Sleep -Milliseconds 50}
   Assert-Upgrade ($failure.stage-eq 'failed' -and [UDA.ServerStorage]::Read($Repository,'repo.manifest.json')-eq $beforeCatalog) ('failed workspace is discarded without changing the live catalog: '+$serializer.Serialize($failure))
   Assert-Upgrade ((Download-CandidateHash $candidateA)-eq $beforeA -and (Download-CandidateHash $candidateB)-eq $beforeB -and (Download-CandidateHash $candidateC)-eq $beforeC) 'failed workspace leaves previous client installations usable'
  }
 }
 Assert-Upgrade (([UDA.ServerStorage]::Documents($Repository,'repo.download-history.')).Count-gt 0) 'previous download tickets persist in MySQL for client continuity after restart'
 # Recreate the HTTP service to exercise SQL-backed ticket recovery, not just an in-memory cache.
 $server.Stop();$server.Dispose();$server=New-Object UDA.LanDriverServer($cfg,$null)
 [UDA.LanDriverServer].GetField('TestWatchDebounceMs',[Reflection.BindingFlags]'Instance,NonPublic').SetValue($server,600000)
 $server.Start()
 for($spin=0;$spin-lt 100 -and -not $server.RepoLoaded;$spin++){Start-Sleep -Milliseconds 50}
 Assert-Upgrade ((Download-CandidateHash $candidateA)-eq $beforeA -and (Download-CandidateHash $candidateB)-eq $beforeB -and (Download-CandidateHash $candidateC)-eq $beforeC) 'pre-switch download tickets remain usable after server restart'
 $startupRunning=[UDA.LanDriverServer].GetProperty('ImportRunningNow',[Reflection.BindingFlags]'Instance,NonPublic')
 for($spin=0;$spin-lt 100 -and $startupRunning.GetValue($server,$null);$spin++){Start-Sleep -Milliseconds 50}
 foreach($watcher in [UDA.LanDriverServer].GetField('_sourceWatchers',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($server)){$watcher.EnableRaisingEvents=$false}
 $cookies=New-Object Net.CookieContainer
 [void](Upload-Request '/api/admin/login' '{"user":"tls-admin","password":"Isolated-Admin-Password-123!"}')
 # Catalog deletion must neither expand nor rewrite any archive, including unrelated ones.
 $beforeDelete=[UDA.OfflineRepoGenerator]::TryLoadManifest($Repository)
 $victim=@($beforeDelete.Entries | Where-Object {$_.SourceName -eq 'generic'})
 if($victim.Count-eq 0){$victim=@($beforeDelete.Entries | Where-Object {-not $_.SourceName.StartsWith('Machine-')} | Select-Object -First 1)}
 $archiveState=@{};Get-ChildItem -LiteralPath (Join-Path $Repository '.uda-downloads') -File | ForEach-Object {$archiveState[$_.Name]=$_.Length.ToString()+':'+$_.LastWriteTimeUtc.Ticks}
 # Hold only the catalog row to make the deletion transaction wait deterministically.
 $openMethod=[UDA.ServerStorage].GetMethod('Open',[Reflection.BindingFlags]'NonPublic,Static')
 $holdDb=$openMethod.Invoke($null,@($Connection));$holdTx=$holdDb.BeginTransaction()
 try{
  $holdCmd=$holdDb.CreateCommand();$holdCmd.Transaction=$holdTx;$holdCmd.CommandText="SELECT document_key FROM uda_documents WHERE repository_id=@repo AND document_key='repo.manifest.json' FOR UPDATE"
  $holdParam=$holdCmd.CreateParameter();$holdParam.ParameterName='@repo';$holdParam.Value=([UDA.ServerStorage]::Config($Repository)).RepositoryId;[void]$holdCmd.Parameters.Add($holdParam);[void]$holdCmd.ExecuteScalar();$holdCmd.Dispose()
  $deleteScript=Join-Path $PSScriptRoot 'Verify-CatalogDeleteClient.ps1';$deleteInput=Join-Path $WorkDirectory 'delete-client.json';$deleteOutput=Join-Path $WorkDirectory 'delete-result.json'
  [IO.File]::WriteAllText($deleteInput,$serializer.Serialize(@{url=('http://localhost:'+$port+'/api/admin/drivers/delete');cookie=$cookies.GetCookieHeader([Uri]('http://localhost:'+$port));body=$serializer.Serialize(@{ids=[string[]]@($victim[0].RelativePath)});protocol=[UDA.LanProtocol]::Version;output=[string]$deleteOutput}))

  $deleteClient=Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$deleteScript+'"'),'-InputFile',('"'+$deleteInput+'"')) -PassThru -WindowStyle Hidden
  $runningProperty=[UDA.LanDriverServer].GetProperty('ImportRunningNow',[Reflection.BindingFlags]'Instance,NonPublic')
  for($spin=0;$spin-lt 100 -and -not $runningProperty.GetValue($server,$null) -and -not $deleteClient.HasExited;$spin++){Start-Sleep -Milliseconds 50}
  Assert-Upgrade (-not $deleteClient.HasExited -and $runningProperty.GetValue($server,$null)) 'catalog row lock holds deletion pending for concurrent client verification'
  $liveHealth=Upload-Request '/api/health' $null 'GET'
  Assert-Upgrade ($liveHealth.RepoLoaded -and -not $liveHealth.ImportRunning) 'pending catalog deletion does not advertise whole-repository maintenance'
  $concurrentReport=Report-Model 'Imported Model' 26100
  Assert-Upgrade ($concurrentReport.Status-eq 200 -and $concurrentReport.Data.Candidates.Count-gt 0 -and -not $deleteClient.HasExited) 'client matching continues while catalog deletion waits for database commit'
  $liveDownload=[Net.HttpWebRequest]::Create('http://localhost:'+$port+'/api/download?path='+[Uri]::EscapeDataString($concurrentReport.Data.Candidates[0].InfRelativePath));$liveDownload.Proxy=$null;$liveDownload.Timeout=5000;$liveDownload.Headers['X-UDA-Protocol']=[string][UDA.LanProtocol]::Version
  $downloadResponse=$liveDownload.GetResponse();try{Assert-Upgrade ([int]$downloadResponse.StatusCode-eq 200 -and -not $deleteClient.HasExited) 'unrelated archive download continues during pending catalog deletion'}finally{$downloadResponse.Dispose()}

 }finally{$holdTx.Rollback();$holdTx.Dispose();$holdDb.Dispose()}
 if(-not $deleteClient.WaitForExit(10000)){throw 'Delete HTTP child did not finish after lock release'}
 $delete=$serializer.DeserializeObject([IO.File]::ReadAllText($deleteOutput))
 Assert-Upgrade ($deleteClient.ExitCode-eq 0) ('background delete HTTP succeeds: '+[IO.File]::ReadAllText($deleteOutput))
 Assert-Upgrade ($delete.removed-eq 1) 'actual delete API removes only selected catalog entry'
 $deletedCatalog=[UDA.OfflineRepoGenerator]::TryLoadManifest($Repository)
 Assert-Upgrade ($deletedCatalog.Entries.Count-eq ($beforeDelete.Entries.Count-1) -and -not ($deletedCatalog.Entries.RelativePath -contains $victim[0].RelativePath)) 'SQL catalog excludes deleted driver and retains every other driver'
 Assert-Upgrade (-not (Test-Path -LiteralPath (Join-Path $Repository 'packages')) -and -not (Test-Path -LiteralPath (Join-Path $Repository 'repo.udadb'))) 'delete never expands raw packages or materializes a repository index'
 $changed=@(Get-ChildItem -LiteralPath (Join-Path $Repository '.uda-downloads') -File | Where-Object {$archiveState[$_.Name]-ne ($_.Length.ToString()+':'+$_.LastWriteTimeUtc.Ticks)})
 Assert-Upgrade ($changed.Count-eq 0) 'delete does not modify or recompress any archive'
 $stillServing=Report-Model 'Imported Model' 26100
 Assert-Upgrade ($stillServing.Status-eq 200 -and $stillServing.Data.Candidates.Count-gt 0) 'unrelated model report remains usable immediately after deletion'
 $deletedBlocked=$false
 try{[void](Download-CandidateHash $preOrganizationGeneric)}catch [Net.WebException]{if($_.Exception.Response){$deletedBlocked=([int]$_.Exception.Response.StatusCode-eq 404);$_.Exception.Response.Dispose()}else{throw}}
 Assert-Upgrade $deletedBlocked 'explicit unpublish cannot be bypassed through an older pre-organization download ticket'
 $server.LoadRepo()
 $reloaded=Report-Model 'Imported Model' 26100
 Assert-Upgrade ($reloaded.Status-eq 200 -and $reloaded.Data.Candidates.Count-gt 0) 'catalog-only deletion survives repository reload'
}finally{$server.Dispose()}
[UDA.DatabaseArchiveRepository]::ExpandForMaintenance($Repository)
Assert-Upgrade ((Test-Path -LiteralPath (Join-Path $Repository 'packages')) -and (Test-Path -LiteralPath (Join-Path $Repository 'repo.udadb'))) 'maintenance restores temporary driver files and original verified hardware index from SQL'
[void]$generator.Clean($Repository,[Threading.CancellationToken]::None,$null)
$afterClean=[UDA.OfflineRepoGenerator]::TryLoadManifest($Repository)
$machineEntries=@($afterClean.Entries | Where-Object {$_.SourceName.StartsWith('Machine-')})
Assert-Upgrade ($machineEntries.Count-ge 3) 'duplicate cleanup preserves independent model packages even when generic INF content is identical'
[void][UDA.DatabaseArchiveRepository]::Compact($Repository)
Assert-Upgrade (-not (Test-Path -LiteralPath (Join-Path $Repository 'packages'))) 'maintenance completion compacts temporary expanded files again'
[UDA.ServerStorage]::RestoreFileCompatibility($Repository)
Assert-Upgrade (-not ([UDA.ServerStorage]::Config($Repository)).DatabaseOnly -and (Test-Path -LiteralPath (Join-Path $Repository 'packages'))) 'upgrade rollback can restore old-service file compatibility from verified archives'
