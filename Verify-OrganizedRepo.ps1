param([string]$OutputDirectory = 'web-console-organized')
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory + '\Lan\UDA-Server-Manager.exe')))
$root = Join-Path $PSScriptRoot ($OutputDirectory + '\organized-test-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'; New-Item -ItemType Directory -Path $source -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'test-fixtures\organized-v1\driver.inf') -Destination $source
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'test-fixtures\organized-v1\driver.sys') -Destination $source
$repo = Join-Path $root 'repo'
$generator = New-Object UDA.OfflineRepoGenerator($null,$null)
$token = [Threading.CancellationToken]::None
$first = $generator.AppendOrganizedSource($repo,$source,$token,$null)
if ($first.Added -ne 1) { throw 'First append failed' }
$manifest = [UDA.OfflineRepoGenerator]::TryLoadManifest($repo)
$oldInf = Join-Path $repo $manifest.Entries[0].RelativePath
$oldTime = (Get-Item -LiteralPath $oldInf).LastWriteTimeUtc
if ($manifest.Entries[0].RelativePath -notmatch '^packages/' -or -not $manifest.Entries[0].PackageHash) { throw 'Not categorized/content addressed' }
$repeat = $generator.AppendOrganizedSource($repo,$source,$token,$null)
if ($repeat.Added -ne 0 -or $repeat.Unchanged -ne 1) { throw 'Reimport is not idempotent' }
if ((Get-Item -LiteralPath $oldInf).LastWriteTimeUtc -ne $oldTime) { throw 'Existing package was recopied' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'test-fixtures\organized-v2\driver.inf') -Destination (Join-Path $source 'driver.inf') -Force
$second = $generator.AppendOrganizedSource($repo,$source,$token,$null)
$manifest = [UDA.OfflineRepoGenerator]::TryLoadManifest($repo)
if ($manifest.Entries.Count -ne 2 -or -not (Test-Path -LiteralPath $oldInf)) { throw 'Prior version lost' }
if ($generator.Verify($repo,$token).Count -ne 0) { throw 'Repository integrity failed' }
$opened = [UDA.OfflineRepo]::Open($repo,$null)
if ($opened.Records.Count -ne 2) { throw 'Historical versions not indexed' }
$opened.Dispose()
$newEntry=$manifest.Entries|Where-Object DriverVerVersion -eq '2.0.0.0'|Select-Object -First 1
$oldBinary=Join-Path ([IO.Path]::GetDirectoryName($oldInf)) 'driver.sys'
$newBinary=Join-Path (Join-Path $repo $newEntry.PackageRoot) 'driver.sys'
node (Join-Path $PSScriptRoot 'Verify-PackageHardlinks.js') $oldBinary $newBinary
if($LASTEXITCODE -ne 0){throw 'Incremental update duplicated an unchanged binary'}
$sourceBinary=Join-Path $source 'driver.sys';$time=[IO.File]::GetLastWriteTimeUtc($sourceBinary);$bytes=[IO.File]::ReadAllBytes($sourceBinary);$beforeHash=(Get-FileHash -LiteralPath $newBinary).Hash
$bytes[0]=$bytes[0] -bxor 1;[IO.File]::WriteAllBytes($sourceBinary,$bytes);[IO.File]::SetLastWriteTimeUtc($sourceBinary,$time)
[void]$generator.AppendOrganizedSource($repo,$source,$token,$null)
if([UDA.OfflineRepoGenerator]::TryLoadManifest($repo).Entries.Count -ne 3 -or (Get-FileHash -LiteralPath $newBinary).Hash -ne $beforeHash){throw 'Same-metadata changed content was linked over a historical version'}
if($generator.AuditDuplicates($repo,$token).DuplicateCount -ne 0){throw 'Different binary revisions were classified as duplicates'}
Write-Output 'PASS: physical incremental reuse, immutable historical bytes and same-size/timestamp content-change detection'
$moves = $generator.Organize($repo,$token,$null)
if ($moves -ne 0 -or $generator.Verify($repo,$token).Count -ne 0) { throw 'Organize not idempotent on classified packages' }
$legacy = Join-Path $root 'legacy'
[void]$generator.Generate($legacy,[string[]]@($source),(New-Object UDA.OsTarget),(New-Object UDA.OfflineRepoMachineHints),'test',$token,$null)
$before = [UDA.OfflineRepoGenerator]::TryLoadManifest($legacy)
$hash = $before.Entries[0].ContentHash
if ($generator.Organize($legacy,$token,$null) -ne 1) { throw 'Legacy package not moved' }
$after = [UDA.OfflineRepoGenerator]::TryLoadManifest($legacy)
if ([UDA.OfflineRepoGenerator]::ResolvePathAlias($legacy,$before.Entries[0].RelativePath) -ne $after.Entries[0].RelativePath) { throw 'Pre-organization download path not redirected' }
if ($after.Entries[0].ContentHash -ne $hash -or -not $after.Entries[0].PackageRoot -or $generator.Verify($legacy,$token).Count -ne 0) { throw 'Legacy organization lost bytes or metadata' }
Write-Output 'PASS: content-addressed append, repeat import without recopy, same-source historical versions, classified directory layout, legacy in-place organization and integrity'
Add-Type -AssemblyName System.Web.Extensions
$json = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$original = $after.Entries[0].PackageRoot
$interrupted = 'packages/recovery-test-' + [Guid]::NewGuid().ToString('N')
foreach ($file in @('repo.udadb','repo.manifest.json')) { Copy-Item -LiteralPath (Join-Path $legacy $file) -Destination (Join-Path $legacy ($file + '.organization-backup')) }
$journal = New-Object UDA.OfflineRepoGenerator+OrganizationJournal
$journal.HadAliases = Test-Path -LiteralPath (Join-Path $legacy 'repo.path-aliases.json')
if ($journal.HadAliases) { Copy-Item -LiteralPath (Join-Path $legacy 'repo.path-aliases.json') -Destination (Join-Path $legacy 'repo.path-aliases.json.organization-backup') }
$journal.Moves.Add([string[]]@($original,$interrupted))
[IO.File]::WriteAllText((Join-Path $legacy 'repo.organization-journal.json'),$json.Serialize($journal),[Text.Encoding]::UTF8)
Move-Item -LiteralPath (Join-Path $legacy $original) -Destination (Join-Path $legacy $interrupted)
[UDA.OfflineRepoGenerator]::RecoverOrganization($legacy)
if (-not (Test-Path -LiteralPath (Join-Path $legacy $original)) -or (Test-Path -LiteralPath (Join-Path $legacy $interrupted)) -or $generator.Verify($legacy,$token).Count -ne 0) { throw 'Interrupted organization recovery failed' }
Write-Output 'PASS: simulated mid-move interruption restores package path, manifest and index'
