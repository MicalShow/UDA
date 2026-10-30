$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $MyInvocation.MyCommand.Path
[void][Reflection.Assembly]::LoadFrom((Join-Path $taskRoot 'release-v5.3.45\Lan\OfflineRepoGenerator.exe'))
$testRoot=Join-Path $taskRoot ('validation-missing-archives-'+[Guid]::NewGuid().ToString('N'))
$source=Join-Path $testRoot 'source';[void][IO.Directory]::CreateDirectory($source)
Copy-Item -Path (Join-Path $taskRoot 'test-fixtures\organized-v1\*') -Destination $source
$origin=Join-Path $testRoot 'origin';$target=Join-Path $testRoot 'target'
$gen=New-Object UDA.OfflineRepoGenerator((New-Object UDA.TrustingSignatureVerifier),$null)
[void]$gen.Generate($origin,[string[]]@($source),(New-Object UDA.OsTarget),$null,'repair-test',[Threading.CancellationToken]::None,$null)
[void][UDA.OfflineRepoGenerator]::PrepareDownloadPackages($origin,$null)
Copy-Item -LiteralPath $origin -Destination $target -Recurse
$archive=Get-ChildItem -LiteralPath (Join-Path $target '.uda-downloads') -Filter '*.zip' | Select-Object -First 1
[IO.File]::Delete($archive.FullName)
& (Join-Path $taskRoot 'Repair-UdaMissingArchives.ps1') -Repository $target -SourceRepository $origin -ServerDirectory (Join-Path $taskRoot 'release-v5.3.45\Lan') -ReportPath (Join-Path $testRoot 'missing.csv')
if(!(Test-Path -LiteralPath $archive.FullName)){throw 'Missing archive was not restored'}
if(@(Import-Csv (Join-Path $testRoot 'missing.csv')).Count -ne 1){throw 'Expected exactly one missing archive'}
$stamp=(Get-Item -LiteralPath $archive.FullName).LastWriteTimeUtc
& (Join-Path $taskRoot 'Repair-UdaMissingArchives.ps1') -Repository $target -ScanOnly -ServerDirectory (Join-Path $taskRoot 'release-v5.3.45\Lan') -ReportPath (Join-Path $testRoot 'none.csv')
if((Get-Item -LiteralPath $archive.FullName).LastWriteTimeUtc -ne $stamp){throw 'Existing archive was modified'}
[IO.File]::Delete($archive.FullName)
[IO.File]::WriteAllText((Join-Path (Join-Path $origin '.uda-downloads') $archive.Name),'invalid zip')
$refused=$false
try { & (Join-Path $taskRoot 'Repair-UdaMissingArchives.ps1') -Repository $target -SourceRepository $origin -ServerDirectory (Join-Path $taskRoot 'release-v5.3.45\Lan') -ReportPath (Join-Path $testRoot 'bad.csv') } catch { $refused=$true }
if(!$refused -or (Test-Path -LiteralPath $archive.FullName)){throw 'Invalid source archive was published'}
Write-Host 'PASS: missing-only copy, catalog validation, unchanged existing files, idempotent second run, invalid archive refusal.'
