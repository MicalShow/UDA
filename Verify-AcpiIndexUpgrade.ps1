param([string]$OutputDirectory = 'release-v4.8.3-complete')
$ErrorActionPreference = 'Stop'
$buildRoot = Join-Path $PSScriptRoot $OutputDirectory
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $buildRoot 'Lan\UDA-Server-Manager.exe'))
$work = Join-Path $buildRoot ('acpi-upgrade-test-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$infPath = Join-Path $work 'power.inf'
[IO.File]::WriteAllText($infPath, "[Version]`nSignature=`"`$Windows NT`$`"`nClass=System`n[Manufacturer]`nLenovo=Models,NTamd64`n[Models.NTamd64]`nPower=Install,%PowerId%`n[Strings]`nPowerId=ACPI\LEN0068`n")
$record = [UDA.InfParser]::Parse($infPath, 'Lenovo')
$record.Path = 'power.inf'
# Reproduce the legacy index with an unresolved Strings variable.
$record.Models[0].HardwareIds.Clear()
$record.Models[0].CompatibleIds.Clear()
$record.Models[0].CompatibleIds.Add('%POWERID%')
$record.HardwareIds.Clear()
$record.CompatibleIds.Clear()
$record.CompatibleIds.Add('%POWERID%')
$manifest = New-Object UDA.OfflineRepoManifest
$entry = New-Object UDA.OfflineRepoEntry
$entry.RelativePath = 'power.inf'
$entry.SourceName = 'Lenovo'
$entry.ContentHash = (Get-FileHash -LiteralPath $infPath -Algorithm SHA256).Hash
$manifest.Entries.Add($entry)
$records = New-Object 'System.Collections.Generic.List[UDA.InfRecord]'
$records.Add($record)
$method = $assembly.GetType('UDA.OfflineRepo').GetMethod('BackfillModels', [Reflection.BindingFlags]'NonPublic,Static')
$arguments = [object[]]@($work, $manifest, $records, $null, 0)
for ($i = 0; $i -lt $arguments.Length; $i++) { if ($null -ne $arguments[$i]) { $arguments[$i] = $arguments[$i].PSObject.BaseObject } }
$result = $method.Invoke($null, $arguments)
if ($arguments[4] -ne 1) { throw 'Legacy unresolved-ID index was not migrated' }
$target = New-Object UDA.OsTarget
$target.Architecture = 'amd64'; $target.MajorVersion = 10; $target.BuildNumber = 19045
$verdict = [UDA.InfApplicability]::Evaluate($result[0], $target, [string[]]@('ACPI\LEN0068'), $null)
if (-not $verdict.Applicable) { throw 'Migrated index still cannot match Lenovo ACPI ID' }
Write-Output 'PASS: Existing unresolved ACPI index repaired in place without copying driver packages'
