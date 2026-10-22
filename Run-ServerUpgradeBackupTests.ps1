[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$AssemblyPath
)
$ErrorActionPreference = 'Stop'
$assemblyFull = [IO.Path]::GetFullPath($AssemblyPath)
if (-not (Test-Path -LiteralPath $assemblyFull -PathType Leaf)) { throw "Assembly not found: $assemblyFull" }
$workRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('uda-upg-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))))
$app = Join-Path $workRoot 'app'
$data = Join-Path $workRoot 'data'
$repo = Join-Path $workRoot 'repo'
$restore = Join-Path $workRoot 'restore'
$backup = $null
try {
  foreach ($dir in @((Join-Path $app 'ClientTemplate\nested'), (Join-Path $app 'DatabaseProvider'), (Join-Path $app 'database'), (Join-Path $data 'Logs'), (Join-Path $data 'preserve-me'), (Join-Path $repo 'install-events'))) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
  }
  $deepData = Join-Path (Join-Path (Join-Path $data ('d' * 48)) ('e' * 48)) ('f' * 48)
  New-Item -ItemType Directory -Path $deepData -Force | Out-Null
  if ($deepData.Length -ge 248) { throw 'Deep-state fixture itself exceeds the legacy directory limit.' }
  $legacyBackup = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'UniversalDriverAssistant\UpgradeBackups\20261017-1234567890abcdef0123456789abcdef\Data'
  $legacyDeep = Join-Path $legacyBackup (Join-Path (Join-Path ('d' * 48) ('e' * 48)) ('f' * 48))
  if ([IO.Path]::GetFullPath($legacyDeep).Length -lt 248) { throw 'Deep-state fixture does not reproduce the legacy backup path limit.' }
  [IO.File]::WriteAllText((Join-Path $deepData 'sentinel.txt'), 'keep-deep-state')
  [IO.File]::WriteAllText((Join-Path $app 'LanDriverService.exe'), 'old-service')
  [IO.File]::WriteAllText((Join-Path $app 'ClientTemplate\nested\client.txt'), 'old-client')
  [IO.File]::WriteAllText((Join-Path $app 'DatabaseProvider\provider.dll'), 'old-provider')
  [IO.File]::WriteAllText((Join-Path $data 'Logs\service-events.log'), 'keep-log')
  [IO.File]::WriteAllText((Join-Path $data 'preserve-me\sentinel.txt'), 'keep-state')
  [IO.File]::WriteAllText((Join-Path $repo 'install-events\event.json'), 'keep-event')
  [Reflection.Assembly]::LoadFrom($assemblyFull) | Out-Null

  $backup = [UDA.ServerUpgrade]::Backup($app, $data, $repo, 'service command fixture')
  if ([IO.File]::ReadAllText((Join-Path $backup 'Application\LanDriverService.exe')) -ne 'old-service') { throw 'Application binary was not backed up.' }
  if ([IO.File]::ReadAllText((Join-Path $backup 'Application\ClientTemplate\nested\client.txt')) -ne 'old-client') { throw 'Nested client template file was not backed up.' }
  if (Test-Path -LiteralPath (Join-Path $backup 'Data')) { throw 'Persistent service data was copied into the rollback backup.' }
  if (Test-Path -LiteralPath (Join-Path $backup 'RepositoryState')) { throw 'Repository state was copied into the rollback backup.' }

  [UDA.ServerUpgrade]::RestoreApplication($backup, $restore)
  if ([IO.File]::ReadAllText((Join-Path $restore 'LanDriverService.exe')) -ne 'old-service') { throw 'Application restore failed.' }
  if ([IO.File]::ReadAllText((Join-Path $data 'Logs\service-events.log')) -ne 'keep-log' -or
      [IO.File]::ReadAllText((Join-Path $data 'preserve-me\sentinel.txt')) -ne 'keep-state' -or
      [IO.File]::ReadAllText((Join-Path $repo 'install-events\event.json')) -ne 'keep-event' -or
      [IO.File]::ReadAllText((Join-Path $deepData 'sentinel.txt')) -ne 'keep-deep-state') { throw 'Persistent service or repository data changed.' }
  'PASS: application backup and restore work; service data and repository logs remain untouched.'
}
finally {
  $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
  if ($workRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $workRoot) -match '^uda-upg-[0-9a-f]{8}$') {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
  }
  if ($backup) {
    $backupRoot = [IO.Path]::Combine([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData), 'UDA-UpgradeBackups')
    $backupRoot = [IO.Path]::GetFullPath($backupRoot).TrimEnd('\') + '\'
    $backupFull = [IO.Path]::GetFullPath($backup)
    if ($backupFull.StartsWith($backupRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $backupFull) -match '^\d{8}-\d{6}-[0-9a-f]{32}$') {
      Remove-Item -LiteralPath $backupFull -Recurse -Force -ErrorAction SilentlyContinue
    }
  }
}
