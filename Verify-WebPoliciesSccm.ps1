param([string]$OutputDirectory = 'web-console-stage4')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Web.Extensions
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory + '\Lan\UDA-Server-Manager.exe')))
$root = Join-Path $PSScriptRoot ($OutputDirectory + '\policy-test-' + [Guid]::NewGuid().ToString('N'))
$policy = New-Object UDA.DriverReleasePolicy
$policy.HardwareId = 'PCI\VEN_1234&DEV_5678'; $policy.StableHash = 'stable'; $policy.LatestHash = 'latest'; $policy.MinBuild = 19041; $policy.MaxBuild = 19045
[UDA.DriverReleasePolicies]::Save($root,$policy)
$loaded = [UDA.DriverReleasePolicies]::Load($root)
if ($loaded.Count -ne 1 -or $loaded[0].StableHash -ne 'stable') { throw 'Policy persistence failed' }
$candidates = New-Object 'System.Collections.Generic.List[UDA.MatchCandidate]'
foreach ($hash in @('latest','stable')) {
    $candidate = New-Object UDA.MatchCandidate
    $candidate.Record = New-Object UDA.InfRecord
    $candidate.Record.ContentHash = $hash
    $candidate.Record.DriverVerVersion = $(if ($hash -eq 'latest') { '3.0' } else { '2.0' })
    $candidate.Score = New-Object UDA.MatchScore
    $candidate.Score.MatchTypeRank = 4
    $candidates.Add($candidate)
}
$target = New-Object UDA.OsTarget
$target.BuildNumber = 19045
$ordered = @([UDA.DriverReleasePolicies]::Order($candidates,$loaded,$target,[string[]]@($policy.HardwareId)))
if ($ordered[0].Record.ContentHash -ne 'stable') { throw 'Stable version not selected' }
$target.BuildNumber = 22621
$ordered = @([UDA.DriverReleasePolicies]::Order($candidates,$loaded,$target,[string[]]@($policy.HardwareId)))
if ($ordered[0].Record.ContentHash -ne 'latest') { throw 'Policy leaked to wrong OS scope' }
$ai = New-Object UDA.AiSubscription
$ai.Name = 'test'; $ai.BaseUrl = 'https://example.invalid/v1'; $ai.Model = 'test-model'
[UDA.AiSubscriptionStore]::Save($root,$ai,'TEST-KEY-DO-NOT-USE')
if ((Get-Content -LiteralPath (Join-Path $root 'ai-subscription.json') -Raw).Contains('TEST-KEY-DO-NOT-USE')) { throw 'API key saved in plaintext' }
$result = New-Object UDA.LanDriverClient+LanClosedLoopResult
if ([UDA.Phase3Dispatch]::SccmExitCode($result) -ne 0) { throw 'SCCM success code' }
$result.Reboot = $true
if ([UDA.Phase3Dispatch]::SccmExitCode($result) -ne 3010) { throw 'SCCM reboot code' }
$result.InstalledUnverified = 1
if ([UDA.Phase3Dispatch]::SccmExitCode($result) -ne 1) { throw 'SCCM unverified code' }
$result.Error = 'network failure'
if ([UDA.Phase3Dispatch]::SccmExitCode($result) -ne 2) { throw 'SCCM error code' }
$options = [UDA.O]::Parse([string[]]@('--sccm'))
if (-not $options.Sccm -or -not $options.Deploy -or -not $options.NoReboot) { throw 'SCCM flags' }
Write-Output 'PASS: policy persistence, stable priority, OS-scoped latest fallback, encrypted AI key, SCCM flags and exit codes'
