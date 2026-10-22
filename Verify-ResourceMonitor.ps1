param([string]$OutputDirectory = 'release-v4.8.8')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory + '\Lan\UDA-Server-Manager.exe')))
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$base = $assembly.GetType('UDA.MainForm')
if ($null -eq $base) { $base = $assembly.GetType('UDA.LanEditionForm').BaseType }
$ctor = $assembly.GetType('UDA.LanEditionForm').GetConstructor($flags, $null, [type[]]@([UDA.C],[UDA.O],[UDA.L],[bool]), $null)
$logger = New-Object UDA.L((Join-Path $PSScriptRoot ($OutputDirectory + '\monitor-test')))
$config = New-Object UDA.C
$options = New-Object UDA.O
$form = $ctor.Invoke([object[]]@($config.PSObject.BaseObject,$options.PSObject.BaseObject,$logger.PSObject.BaseObject,$true))
try {
    $start = $base.GetMethod('StartResourceMonitor', $flags)
    [void]$start.Invoke($form, $null)
    $timer = $base.GetField('resourceTimer', $flags).GetValue($form)
    [void]$start.Invoke($form, $null)
    if (-not [object]::ReferenceEquals($timer,$base.GetField('resourceTimer', $flags).GetValue($form))) { throw 'Duplicate timer created' }
    $until = [DateTime]::UtcNow.AddSeconds(2)
    while ([DateTime]::UtcNow -lt $until) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 20 }
    $label = $base.GetField('resourceStatus', $flags).GetValue($form)
    if ($label.Text -notmatch 'CPU \d+%.*\d+%') { throw ('Monitor did not sample: ' + $label.Text) }
    $form.UseWaitCursor = $true
    $base.GetMethod('ResetPointer',$flags).Invoke($form,$null)
    if ($form.UseWaitCursor) { throw 'Wait cursor retained' }
    Write-Output ('PASS: continuous resource timer, idempotent start, native samples, cursor reset: ' + $label.Text)
    $logger.add_E([Action[string]]{ throw 'synthetic subscriber failure' })
    $logger.W('logger resilience test')
    Write-Output 'PASS: log subscriber exception cannot terminate service'
} finally { $form.Dispose(); $logger.Dispose() }
if ($timer.Enabled) { throw 'Timer remains active after disposal' }
Write-Output 'PASS: timer released on close'
$setup = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ServerSetup.cs') -Raw
foreach ($expected in @('start= auto', 'failure UdaLanDriverService reset= 86400 actions= restart/5000/restart/15000/restart/60000', 'failureflag UdaLanDriverService 1", false')) {
    if (-not $setup.Contains($expected)) { throw ('Missing service configuration: ' + $expected) }
}
Write-Output 'PASS: installer requires automatic startup and checked restart-on-failure configuration (static verification only)'
