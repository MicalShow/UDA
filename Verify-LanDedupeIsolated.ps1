$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'verify-v483-complete\LanHarness.exe'))
$type = $assembly.GetType('UDA.LanHarness')
$method = $type.GetMethod('TestAdminDedupe', [Reflection.BindingFlags]'Static,NonPublic')
$work = Join-Path $PSScriptRoot ('verify-v483-dedupe-' + [Guid]::NewGuid().ToString('N'))
[void]$method.Invoke($null, [object[]]@($work.PSObject.BaseObject))
if ($type.GetField('_failed', [Reflection.BindingFlags]'Static,NonPublic').GetValue($null) -ne 0) { throw 'Isolated duplicate test failed' }
Write-Output 'PASS: isolated AdminDedupe retest'
