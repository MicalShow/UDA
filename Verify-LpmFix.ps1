param([string]$OutputDirectory = 'release-v4.8.4')
$ErrorActionPreference = 'Stop'
$buildRoot = Join-Path $PSScriptRoot $OutputDirectory
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $buildRoot 'Lan\UDA-Server-Manager.exe'))
$original = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'analysis-lpm\extracted') -Filter 'IBMPMDRV.INF' -Recurse | Select-Object -First 1
if ($null -eq $original) { throw 'Original extracted Lenovo INF missing' }
$hashBefore = (Get-FileHash -LiteralPath $original.FullName -Algorithm SHA256).Hash
$record = [UDA.InfParser]::Parse($original.FullName, 'Lenovo')
$target = New-Object UDA.OsTarget
$target.Architecture = 'amd64'; $target.MajorVersion = 10; $target.BuildNumber = 19045
if (-not [UDA.InfApplicability]::Evaluate($record, $target, [string[]]@('ACPI\LEN0268'), $null).Applicable) { throw 'Real Lenovo INF still rejected' }
if ([UDA.InfApplicability]::Evaluate($record, $target, [string[]]@('ACPI\LEN0068'), $null).Applicable) { throw 'Wrong hardware accepted' }
$sig = New-Object UDA.WinTrustSignatureVerifier
if (-not $sig.IsTrusted($original.FullName, $null)) { throw 'Original catalog signature rejected' }
Write-Output 'PASS: Real Lenovo INF matches LEN0268 on amd64; other hardware rejected; catalog trusted'
# Reproduce the legacy parser output entirely in memory.
$record.Path = $original.Name
for ($i = 0; $i -lt $record.Decorations.Count; $i++) { $record.Decorations[$i] = 'Mfg.' + $record.Decorations[$i] }
foreach ($model in $record.Models) { $model.Decoration = 'Mfg.' + $model.Decoration }
$entry = New-Object UDA.OfflineRepoEntry
$entry.RelativePath = $original.Name; $entry.SourceName = 'Lenovo'; $entry.ContentHash = $hashBefore
$manifest = New-Object UDA.OfflineRepoManifest
$manifest.Entries.Add($entry)
$records = New-Object 'System.Collections.Generic.List[UDA.InfRecord]'
$records.Add($record)
$method = $assembly.GetType('UDA.OfflineRepo').GetMethod('BackfillModels', [Reflection.BindingFlags]'Static,NonPublic')
$arguments = [object[]]@($original.DirectoryName, $manifest, $records, $null, 0)
for ($i = 0; $i -lt $arguments.Length; $i++) { if ($null -ne $arguments[$i]) { $arguments[$i] = $arguments[$i].PSObject.BaseObject } }
$repaired = $method.Invoke($null, $arguments)
if ($arguments[4] -ne 1 -or -not [UDA.InfApplicability]::Evaluate($repaired[0], $target, [string[]]@('ACPI\LEN0268'), $null).Applicable) { throw 'Legacy dotted-base index not repaired' }
if ($entry.Decorations -contains 'Mfg.NTamd64') { throw 'Manifest retains broken decoration' }
if ($entry.Models.Count -ne 2) { throw 'Manifest models not synchronized' }
$arguments[4] = 0
[void]$method.Invoke($null, $arguments)
if ($arguments[4] -ne 0) { throw 'Migration reparses already corrected index' }
if ((Get-FileHash -LiteralPath $original.FullName -Algorithm SHA256).Hash -ne $hashBefore) { throw 'Original INF modified' }
Write-Output 'PASS: Legacy index and manifest repaired once; original signed INF unchanged'
