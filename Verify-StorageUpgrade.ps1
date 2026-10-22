param([string]$OutputDirectory='release-v5.1.0',[string]$Connection)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Data
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.Web.Extensions
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory+'\Lan\LanDriverService.exe')))
if(-not $Connection){$instance=Get-Content (Join-Path $PSScriptRoot ($OutputDirectory+'\mysql-test\instance.json')) -Raw|ConvertFrom-Json;$Connection='Server=127.0.0.1;Port='+$instance.port+';Database=uda_test;User ID=root;SslMode=None;Connection Timeout=1'}
$json=New-Object System.Web.Script.Serialization.JavaScriptSerializer
$root=Join-Path $PSScriptRoot ($OutputDirectory+'\storage-test-'+[Guid]::NewGuid().ToString('N'))
$source=Join-Path $root 'source';[void][IO.Directory]::CreateDirectory($source)
Copy-Item (Join-Path $PSScriptRoot 'test-fixtures\organized-v1\*') $source
$repo=Join-Path $root 'repo';$ct=[Threading.CancellationToken]::None
$generator=New-Object UDA.OfflineRepoGenerator((New-Object UDA.TrustingSignatureVerifier),$null)
[void]$generator.Generate($repo,[string[]]@($source),(New-Object UDA.OsTarget),$null,'test',$ct,$null)
function Check($ok,$message){if(-not $ok){throw $message};Write-Output ('PASS: '+$message)}
function Event($id,$machine){return $json.Serialize(@{EventId=$id;RunId=[Guid]::NewGuid().ToString('N');Machine=$machine;Mac='001122334455';Device='DEV';Status='installed';Utc='2026-10-16T00:00:00Z';Update=$false})}
$eventId=[Guid]::NewGuid().ToString('N');$eventKey='install-events/'+$eventId+'.json';$event=Event $eventId '旧客户端电脑'
[UDA.ServerStorage]::Write($repo,$eventKey,$event,$true)
$manifest=[UDA.OfflineRepoGenerator]::TryLoadManifest($repo)
$policy=New-Object UDA.DriverReleasePolicy;$policy.HardwareId=$manifest.Entries[0].HardwareIds[0];$policy.DriverClass='Display';$policy.StableHash=$manifest.Entries[0].ContentHash
[UDA.DriverReleasePolicies]::Save($repo,$policy)
[IO.File]::WriteAllText((Join-Path $repo 'repo.category-overrides.json'),'{}')
[IO.File]::WriteAllText((Join-Path $repo 'ai-subscription.json'),'LOCAL-ENCRYPTED-AI-CONFIG')
[void][IO.Directory]::CreateDirectory((Join-Path $repo 'custom-state'))
[IO.File]::WriteAllText((Join-Path $repo 'custom-state\keep.txt'),'CUSTOM DATA')
[UDA.ServerStorage]::TestConnection($Connection)
Write-Output 'PASS: real MySQL connection using bundled Framework provider'
$migrated=[UDA.ServerStorage]::Migrate($repo,$Connection)
Check ($migrated-ge 4) 'transactional migration copies catalog, policies, category settings and historical event'
Check ([UDA.ServerStorage]::Config($repo).Enabled) 'external backend enabled only after verified migration'
Check (-not [IO.File]::ReadAllText((Join-Path $repo '.uda-storage.json')).Contains('Server=127.0.0.1')) 'connection string encrypted at rest'
$dataset=[UDA.ServerStorage]::Config($repo).RepositoryId
[UDA.ServerStorage]::Reconnect($repo,$Connection)
Check ([UDA.ServerStorage]::Config($repo).RepositoryId-eq $dataset) 'updating database credentials preserves existing dataset identity'
[IO.File]::Delete((Join-Path $repo $eventKey))
Check ([UDA.ServerStorage]::Events($repo).Count-eq 1) 'historical log reads from actual SQL after local copy is removed'
Check ([UDA.ServerStorage]::Read($repo,$eventKey)-eq $event) 'SQL preserves Chinese names and exact event body'
[IO.File]::Delete((Join-Path $repo 'repo.release-policies.json'))
Check ([UDA.DriverReleasePolicies]::Load($repo)[0].StableHash-eq $policy.StableHash) 'version policy reads from actual SQL without local JSON'
$savedConfig=[IO.File]::ReadAllText((Join-Path $repo '.uda-storage.json'))
$cfg=[UDA.ServerStorage]::Config($repo)
$bad='Server=127.0.0.1;Port=1;Database=uda_test;User ID=root;SslMode=None;Connection Timeout=1'
$cfg.ProtectedConnection=[Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($bad),$null,[Security.Cryptography.DataProtectionScope]::LocalMachine))
[IO.File]::WriteAllText((Join-Path $repo '.uda-storage.json'),$json.Serialize($cfg))
$offlineId=[Guid]::NewGuid().ToString('N');$offlineKey='install-events/'+$offlineId+'.json';$offlineEvent=Event $offlineId '断网记录'
[UDA.ServerStorage]::Write($repo,$offlineKey,$offlineEvent,$true)
Check (([UDA.ServerStorage]::Status($repo)).pending-eq 1) 'database outage keeps a durable pending event instead of losing ACKed logs'
Check ([UDA.ServerStorage]::Read($repo,$offlineKey)-eq $offlineEvent) 'pending durable value is readable while database is down'
[IO.File]::WriteAllText((Join-Path $repo '.uda-storage.json'),$savedConfig)
$replayScript=Join-Path $root 'replay.ps1'
[IO.File]::WriteAllText($replayScript,'param($Assembly,$Repo);$ErrorActionPreference="Stop";Add-Type -AssemblyName System.Data;Add-Type -AssemblyName System.Security;[void][Reflection.Assembly]::LoadFrom($Assembly);[Console]::WriteLine([UDA.ServerStorage]::Sync($Repo))')
$replayed=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $replayScript -Assembly (Join-Path $PSScriptRoot ($OutputDirectory+'\Lan\LanDriverService.exe')) -Repo $repo
Check ($LASTEXITCODE-eq 0 -and [int]$replayed-eq 1) 'new process recovers pending event from durable queue into real MySQL'
[IO.File]::Delete((Join-Path $repo $offlineKey))
Check ([UDA.ServerStorage]::Read($repo,$offlineKey)-eq $offlineEvent) 'recovered event remains durable in actual SQL'
[UDA.ServerStorage]::Write($repo,$offlineKey,'{"wrong":"duplicate"}',$true)
Check ([UDA.ServerStorage]::Read($repo,$offlineKey)-eq $offlineEvent) 'retry event ID cannot replace original SQL event'
[void]$generator.Generate($repo,[string[]]@($source),(New-Object UDA.OsTarget),$null,'rebuilt',$ct,$null)
Check ([UDA.ServerStorage]::Config($repo).Enabled) 'full catalog rebuild preserves external database configuration'
Check ([IO.File]::ReadAllText((Join-Path $repo 'ai-subscription.json'))-eq 'LOCAL-ENCRYPTED-AI-CONFIG') 'full rebuild preserves local AI settings'
Check ([IO.File]::ReadAllText((Join-Path $repo 'custom-state\keep.txt'))-eq 'CUSTOM DATA') 'full rebuild preserves other state directories'
Check ([UDA.ServerStorage]::Events($repo).Count-eq 2) 'full rebuild retains all SQL installation logs'
Check ([UDA.OfflineRepoGenerator]::TryLoadManifest($repo).CreatedAt-eq 'rebuilt') 'new catalog is committed to external database'
$beforeOrganize=[UDA.OfflineRepoGenerator]::TryLoadManifest($repo)
[void]$generator.Organize($repo,$ct,$null)
$afterOrganize=[UDA.OfflineRepoGenerator]::TryLoadManifest($repo)
Check ($generator.Verify($repo,$ct).Count-eq 0) 'external catalog remains consistent after physical package organization'
[IO.File]::Delete((Join-Path $repo 'repo.path-aliases.json'))
Check ([UDA.OfflineRepoGenerator]::ResolvePathAlias($repo,$beforeOrganize.Entries[0].RelativePath)-eq $afterOrganize.Entries[0].RelativePath) 'organized path aliases recover from SQL when local cache is missing'
$package=$afterOrganize.Entries[0].PackageRoot;$interrupted='packages/recovery-'+[Guid]::NewGuid().ToString('N')
foreach($name in @('repo.udadb','repo.manifest.json','repo.path-aliases.json')){Copy-Item (Join-Path $repo $name) (Join-Path $repo ($name+'.organization-backup'))}
$journal=New-Object UDA.OfflineRepoGenerator+OrganizationJournal;$journal.HadAliases=$true;$journal.Moves.Add([string[]]@($package,$interrupted))
[IO.File]::WriteAllText((Join-Path $repo 'repo.organization-journal.json'),$json.Serialize($journal))
Move-Item -LiteralPath (Join-Path $repo $package) -Destination (Join-Path $repo $interrupted)
[UDA.OfflineRepoGenerator]::RecoverOrganization($repo)
Check ($generator.Verify($repo,$ct).Count-eq 0 -and (Test-Path (Join-Path $repo $package))) 'interrupted organization restores physical files and SQL catalog together'
[UDA.ServerStorage]::UseLocal($repo)
Check (-not [UDA.ServerStorage]::Config($repo).Enabled) 'verified reverse migration returns to local backend'
Check ([IO.File]::ReadAllText((Join-Path $repo $eventKey))-eq $event) 'reverse migration restores missing historical local log'
Check ([UDA.DriverReleasePolicies]::Load($repo)[0].StableHash-eq $policy.StableHash) 'reverse migration preserves policy configuration'
Check ($generator.Verify($repo,$ct).Count-eq 0 -and -not (Test-Path (Join-Path $repo 'repo.organization-journal.json'))) 'catalog integrity remains valid and transient journal is not resurrected after reverse migration'
$before=(Get-FileHash (Join-Path $repo 'repo.manifest.json')).Hash
try{[void][UDA.ServerStorage]::Migrate($repo,$bad);throw 'Bad database unexpectedly accepted'}catch{if($_.Exception.Message-eq 'Bad database unexpectedly accepted'){throw}}
Check (-not [UDA.ServerStorage]::Config($repo).Enabled -and (Get-FileHash (Join-Path $repo 'repo.manifest.json')).Hash-eq $before) 'failed migration keeps active local database and original bytes'
$previous='"C:\UDA\LanDriverService.exe" --service --port 8731 --repo "D:\旧驱动仓库" --token "old-client-token" --admin-credentials "D:\Private\admin.json" --logdir "E:\CustomLogs" --bind "+" --source "D:\Source A" --import-allow "D:\Source A"'
$updated=[UDA.ServerUpgrade]::UpdatedCommand($previous,'C:\UDA\LanDriverService.exe','D:\旧驱动仓库',9876,'C:\UDA\ClientTemplate')
foreach($argument in @('--token','--admin-credentials','--logdir','--bind','--source','--import-allow')){Check ([UDA.ServerSetupDiscovery]::ReadArgument($updated,$argument)-eq [UDA.ServerSetupDiscovery]::ReadArgument($previous,$argument)) ('upgrade retains '+$argument)}
Check ([UDA.ServerSetupDiscovery]::ReadArgument($updated,'--port')-eq '9876') 'upgrade can explicitly update port without changing other parameters'
$anonymous='"C:\UDA\LanDriverService.exe" --service --port 8731 --repo "D:\Repo"'
Check ([UDA.ServerSetupDiscovery]::ReadArgument([UDA.ServerUpgrade]::UpdatedCommand($anonymous,'C:\UDA\LanDriverService.exe','D:\Repo',8731,'C:\UDA\ClientTemplate'),'--token')-eq '') 'previous anonymous clients are not forced onto a newly invented Token'
$app=Join-Path $root 'app';$data=Join-Path $root 'data';[void][IO.Directory]::CreateDirectory($app);[void][IO.Directory]::CreateDirectory((Join-Path $data 'Logs'));[void][IO.Directory]::CreateDirectory((Join-Path $data 'TelemetryQueue'))
[IO.File]::WriteAllText((Join-Path $app 'LanDriverService.exe'),'OLD PROGRAM');[IO.File]::WriteAllText((Join-Path $data 'machine-token.txt'),'old-client-token');[IO.File]::WriteAllText((Join-Path $data 'Logs\service.log'),'HISTORICAL LOG');[IO.File]::WriteAllText((Join-Path $data 'TelemetryQueue\pending.json'),'PENDING EVENT')
$backup=[UDA.ServerUpgrade]::Backup($app,$data,$repo,$previous)
Check ([IO.File]::ReadAllText((Join-Path $backup 'Data\Logs\service.log'))-eq 'HISTORICAL LOG') 'upgrade backs up historical runtime logs'
Check ([IO.File]::ReadAllText((Join-Path $backup 'Data\TelemetryQueue\pending.json'))-eq 'PENDING EVENT') 'upgrade backs up pending telemetry'
Check (-not (Test-Path (Join-Path $backup 'RepositoryState\packages'))) 'upgrade backup avoids duplicating driver binaries'
[IO.File]::WriteAllText((Join-Path $app 'LanDriverService.exe'),'NEW BROKEN PROGRAM')
[IO.File]::AppendAllText((Join-Path $data 'Logs\service.log'),' NEW EVENT')
[UDA.ServerUpgrade]::RestoreApplication($backup,$app)
Check ([IO.File]::ReadAllText((Join-Path $app 'LanDriverService.exe'))-eq 'OLD PROGRAM') 'failed update can restore old program files'
Check ([IO.File]::ReadAllText((Join-Path $data 'Logs\service.log')).EndsWith(' NEW EVENT')) 'program rollback does not overwrite newer logs'
Check ([IO.File]::ReadAllText((Join-Path $data 'machine-token.txt'))-eq 'old-client-token') 'upgrade backup and rollback preserve client authentication token'
Write-Output ('ARTIFACTS: '+$root)
