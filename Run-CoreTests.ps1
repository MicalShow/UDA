[CmdletBinding()]
param([string]$OutputDirectory)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $scriptRoot 'core-test-results' }
if (-not (Test-Path -LiteralPath $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null }

$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$parameters = New-Object System.CodeDom.Compiler.CompilerParameters
$parameters.GenerateExecutable = $true
$parameters.OutputAssembly = Join-Path $OutputDirectory 'CoreHarness.exe'
$parameters.CompilerOptions = '/target:exe /main:UDA.CoreHarness /platform:anycpu /optimize+'
foreach ($reference in @('System.dll','System.Core.dll','System.Drawing.dll','System.Management.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xml.dll','System.Xml.Linq.dll')) { [void]$parameters.ReferencedAssemblies.Add($reference) }
$sources = [string[]]@((Join-Path $scriptRoot 'UniversalDriverAssistant.cs'),(Join-Path $scriptRoot 'ResponsiveForm.cs'),(Join-Path $scriptRoot 'EnhancedFeatures.cs'),(Join-Path $scriptRoot 'DriverIndexCore.cs'),(Join-Path $scriptRoot 'DriverIndexIntegration.cs'),(Join-Path $scriptRoot 'CoreHarness.cs'))
$compiled = $provider.CompileAssemblyFromFile($parameters,$sources)
if ($compiled.Errors.Count -gt 0) { throw (($compiled.Errors | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine) }

$device = Get-WmiObject Win32_PnPEntity | Where-Object { $_.HardwareID -and $_.HardwareID[0] -match '^(PCI|USB|HDAUDIO|ACPI|ROOT|SWD)\\' } | Select-Object -First 1
if (-not $device) { throw 'No supported hardware ID found for integration test.' }
$hardwareId = [string]$device.HardwareID[0]
$driverRoot = Join-Path $OutputDirectory 'drivers'
New-Item -ItemType Directory -Path $driverRoot -Force | Out-Null
$inf = @(
  '[Version]', 'Signature="$Windows NT$"', 'Class=System', 'ClassGuid={4d36e97d-e325-11ce-bfc1-08002be10318}',
  'Provider=%Provider%', 'DriverVer=07/19/2026,99.0.0.1', '', '[Manufacturer]', '%Provider%=Models,NTamd64', '',
  '[Models.NTamd64]', ('%Device%=Install,' + $hardwareId), '', '[Install]', 'CopyFiles=Nothing', '',
  '[Strings]', 'Provider="UDA Core Test"', ('Device="' + $device.Name + '"')
) -join [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $driverRoot 'core-test.inf'),$inf,(New-Object Text.UnicodeEncoding($false,$true)))

function New-TestConfig([bool]$Mapped,[string]$Name) {
  $base = Join-Path $OutputDirectory $Name
  $mappings = @()
  if($Mapped){
    $mappings = @([pscustomobject][ordered]@{Name='TestPackage';ManufacturerPattern='*';ModelPattern='*';SystemSKUPattern='*';DriverPath='.';Priority=1;Enabled=$true})
  }
  $config = [ordered]@{
    General = [ordered]@{ LogDirectory=(Join-Path $base 'logs'); ReportDirectory=(Join-Path $base 'reports'); InstallTimeoutSeconds=10; DeploymentCountdownSeconds=0; AutoReboot=$false; CreateRestorePoint=$false; ScanAllSourcesWhenModelPackageMissing=$true; ScanAllSourcesAfterModelPackage=$false }
    Sources = @([ordered]@{Name='LocalTest';Path=$driverRoot;Priority=1;Enabled=$true;Username='';Password=''})
    ModelMappings = $mappings
    Categories = [ordered]@{Other=@();Chipset=@('System');Network=@('Net');Display=@('Display');Audio=@('MEDIA')}
    Filters = [ordered]@{IncludeCategories=@();ExcludeHardwareIds=@();OnlyMissingOrProblemDevicesInMaintenance=$false}
  }
  New-Item -ItemType Directory -Path $base -Force | Out-Null
  $path = Join-Path $base 'config.json'
  [IO.File]::WriteAllText($path,($config|ConvertTo-Json -Depth 10),(New-Object Text.UTF8Encoding($true)))
  return $path
}

$cases = @(
  @{Mode='fallback';Config=(New-TestConfig $false 'fallback')},
  @{Mode='package';Config=(New-TestConfig $true 'package')}
)
foreach($case in $cases){
  $process=Start-Process -FilePath $parameters.OutputAssembly -ArgumentList @(('"'+$case.Config+'"'),$case.Mode) -Wait -PassThru -WindowStyle Hidden
  if($process.ExitCode -ne 0){throw ('Core test failed: '+$case.Mode+' exit='+$process.ExitCode)}
  $reports=Get-ChildItem (Join-Path (Split-Path $case.Config -Parent) 'reports') -File -ErrorAction Stop
  if(-not ($reports.Name -match '\.xml$') -or -not ($reports.Name -match '\.json$')){throw ('Missing reports: '+$case.Mode)}
  Write-Host ('PASS '+$case.Mode+' using '+$hardwareId) -ForegroundColor Green
}
