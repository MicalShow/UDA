param([string]$OutputDirectory = 'release-v4.8.7')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Web.Extensions
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory + '\Lan\UDA-Server-Manager.exe')))
$record = New-Object UDA.InfRecord
$record.ContentHash = 'large-record-test'
for ($i = 0; $i -lt 40000; $i++) {
    $record.HardwareIds.Add(('PCI\VEN_1234&DEV_{0:X4}&SUBSYS_12345678&REV_01' -f $i))
    $record.CompatibleIds.Add(('PCI\VEN_1234&DEV_{0:X4}&CC_030000' -f $i))
}
$legacy = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$failed = $false
try { [void]$legacy.Serialize($record) } catch { $failed = $true }
if (-not $failed) { throw 'Fixture does not reproduce old JSON limit' }
$temp = Join-Path $PSScriptRoot ($OutputDirectory + '\large-index-test-' + [Guid]::NewGuid().ToString('N') + '.udadb')
$index = New-Object UDA.DriverIndex($temp)
$records = New-Object 'System.Collections.Generic.List[UDA.InfRecord]'
$records.Add($record)
$index.ReplaceAll($records)
$index.Commit()
$loaded = New-Object UDA.DriverIndex($temp)
$loaded.Open()
if ($loaded.NeedsRebuild -or $loaded.Count -ne 1) { throw 'Large index cannot reopen' }
if ($loaded.Records[0].HardwareIds.Count -ne 40000 -or $loaded.Records[0].CompatibleIds.Count -ne 40000) { throw 'IDs lost during round trip' }
$index.Commit()
$loaded.Open()
if ($loaded.Count -ne 1) { throw 'Atomic replacement cannot reopen' }
Write-Output ('PASS: old limit reproduced; large INF index commit/reopen/atomic replacement; 80000 IDs preserved; bytes=' + (Get-Item -LiteralPath $temp).Length)
$index.Dispose(); $loaded.Dispose()
Remove-Item -LiteralPath $temp
if (Test-Path -LiteralPath ($temp + '.bak')) { Remove-Item -LiteralPath ($temp + '.bak') }
