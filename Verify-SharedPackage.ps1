param([string]$OutputDirectory='web-console-payload-path')
$ErrorActionPreference='Stop'
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory+'\Lan\UDA-Server-Manager.exe')))
$root=Join-Path $PSScriptRoot ($OutputDirectory+'\shared-test-'+[Guid]::NewGuid().ToString('N'));$repo=Join-Path $root 'repo'
$source=Join-Path $PSScriptRoot 'test-fixtures\shared-package'
$generator=New-Object UDA.OfflineRepoGenerator($null,$null);$token=[Threading.CancellationToken]::None
[void]$generator.AppendOrganizedSource($repo,$source,$token,$null)
$manifest=[UDA.OfflineRepoGenerator]::TryLoadManifest($repo)
if($manifest.Entries.Count -ne 2){throw 'Shared package fixture not imported'}
$entry=$manifest.Entries|Where-Object RelativePath -Like '*/driver.inf'
$package=Join-Path $repo $entry.PackageRoot
if($generator.DeleteEntries($repo,[string[]]@($entry.RelativePath),$token,$null) -ne 1){throw 'Unpublish failed'}
if(-not(Test-Path -LiteralPath (Join-Path $package 'lib\shared.dll')) -or -not(Test-Path -LiteralPath (Join-Path $repo $entry.RelativePath))){throw 'Shared package bytes were damaged'}
if($generator.AuditDuplicates($repo,$token).TotalCount -ne 1){throw 'Hidden INF was reintroduced as orphan'}
[void]$generator.AppendOrganizedSource($repo,$source,$token,$null)
if([UDA.OfflineRepoGenerator]::TryLoadManifest($repo).Entries.Count -ne 1){throw 'Watch reimport republished deleted INF'}
if($generator.Verify($repo,$token).Count -ne 0){throw 'Shared repository integrity failed'}
$remaining=[UDA.OfflineRepoGenerator]::TryLoadManifest($repo).Entries[0]
[void]$generator.DeleteEntries($repo,[string[]]@($remaining.RelativePath),$token,$null)
if(Test-Path -LiteralPath $package){throw 'Unreferenced full package was not reclaimed'}
[void]$generator.AppendOrganizedSource($repo,$source,$token,$null)
if(Test-Path -LiteralPath $package){throw 'Fully hidden package was needlessly recopied'}
$locate=$assembly.GetType('UDA.LanDriverClient').GetMethod('LocateInf',[Reflection.BindingFlags]'Static,NonPublic')
$exact=$locate.Invoke($null,[object[]]@($source.PSObject.BaseObject,'packages/test/sub/driver.inf','sub/driver.inf'))
if($exact -ne (Join-Path $source 'sub\driver.inf')){throw 'Exact nested INF path was not selected'}
if($null -ne $locate.Invoke($null,[object[]]@($source.PSObject.BaseObject,'packages/test/missing.inf','missing/missing.inf'))){throw 'Missing exact path selected unrelated INF'}
$normalized=[UDA.SmbSourceCredentials]::Normalize('smb://example.invalid/drivers/sub')
if($normalized -ne '\\example.invalid\drivers\sub'){throw 'SMB URI normalization failed'}
$secret=[UDA.AiSubscriptionStore]::ProtectSecret('synthetic-password')
if($secret.Contains('synthetic-password') -or [UDA.AiSubscriptionStore]::UnprotectSecret($secret) -ne 'synthetic-password'){throw 'SMB secret protection failed'}
Write-Output 'PASS: shared dependency preservation, durable unpublish, no orphan resurrection/reimport/recopy, whole-package reclamation, exact nested INF lookup, SMB normalization and encrypted secrets'
