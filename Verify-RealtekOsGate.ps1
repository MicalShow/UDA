param([string]$OutputDirectory = 'release-v4.8.5')
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory + '\Lan\UDA-Server-Manager.exe')))
$record = [UDA.InfParser]::Parse('C:\Users\Administrator\Desktop\oem214.inf', 'Realtek')
foreach ($build in @(0,19041,19045,22000,22621)) {
    $target = New-Object UDA.OsTarget
    $target.Architecture = 'amd64'; $target.MajorVersion = 10; $target.BuildNumber = $build
    $verdict = [UDA.InfApplicability]::Evaluate($record, $target, [string[]]@('SWC\VEN_10EC&AID_0001'), $null)
    if ($verdict.Applicable -ne ($build -ge 22000)) { throw ('Incorrect real Realtek applicability: build=' + $build) }
    if ($build -eq 0 -and $verdict.ReasonCode -ne 'BuildUnknown') { throw 'Unknown build has no explicit reason' }
    Write-Output ('PASS: Realtek build=' + $build + ' applicable=' + $verdict.Applicable + ' reason=' + $verdict.ReasonCode)
}
$config = New-Object UDA.C
$config.General.TargetArchitecture = 'amd64'; $config.General.TargetOsMajorVersion = 10
$environment = [UDA.OsTarget]::FromEnvironment()
if ($environment.BuildNumber -le 0) { throw 'Test host build unavailable' }
foreach ($typeName in @('UDA.LanEditionForm','UDA.Phase3Dispatch')) {
    $method = $assembly.GetType($typeName).GetMethod('ResolveOsTarget', [Reflection.BindingFlags]'NonPublic,Static')
    $actual = $method.Invoke($null, [object[]]@($config.PSObject.BaseObject))
    if ($actual.BuildNumber -ne $environment.BuildNumber) { throw ($typeName + ' loses OS build') }
    Write-Output ('PASS: ' + $typeName + ' retains environment build ' + $actual.BuildNumber)
}
