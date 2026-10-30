$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$p = New-Object System.CodeDom.Compiler.CompilerParameters
$p.GenerateExecutable = $true
$p.OutputAssembly = Join-Path $root 'lan-v46-verify\Probe.exe'
$p.CompilerOptions = '/target:exe /main:Probe /platform:anycpu /optimize+'
foreach ($ref in @('System.dll','System.Data.dll','System.Security.dll','System.Core.dll','System.Drawing.dll','System.Management.dll','System.Web.Extensions.dll','System.Xml.dll','System.Xml.Linq.dll','System.Windows.Forms.dll','System.ServiceProcess.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')) { [void]$p.ReferencedAssemblies.Add($ref) }
$p.CompilerOptions = '/target:exe /main:Probe /platform:anycpu /optimize+ /r:System.Net.Http.dll'
$sources = [string[]]@(
  (Join-Path $root 'UniversalDriverAssistant.cs'),
  (Join-Path $root 'ResponsiveForm.cs'),
  (Join-Path $root 'EnhancedFeatures.cs'),
  (Join-Path $root 'DriverIndexCore.cs'),
  (Join-Path $root 'InfApplicability.cs'),
  (Join-Path $root 'DriverIndexIntegration.cs'),
  (Join-Path $root 'OfflineRepo.cs'),
  (Join-Path $root 'ServerStorage.cs'),
  (Join-Path $root 'ClusterRouting.cs'),
  (Join-Path $root 'LanDriverService.cs'),
  (Join-Path $root 'WebAdmin.cs'),
  (Join-Path $root 'AiDriverTasks.cs'),
  (Join-Path $root 'NicBootstrap.cs'),
  (Join-Path $root 'Phase3Dispatch.cs'),
  (Join-Path $root 'LanEditionForm.cs'),
  (Join-Path $root 'Probe.cs')
)
$r = $provider.CompileAssemblyFromFile($p, $sources)
if ($r.Errors.Count) { $r.Errors | ForEach-Object { '{0}({1},{2}): {3}' -f $_.FileName, $_.Line, $_.Column, $_.ErrorText } ; exit 1 }
& $p.OutputAssembly
exit $LASTEXITCODE
