<#
.SYNOPSIS
  编译并运行布局渲染测试：多分辨率/多 DPI 下渲染主界面页面并输出截图（离线版）。
#>
[CmdletBinding()]param([string]$OutputDirectory)
Set-StrictMode -Version 2.0;$ErrorActionPreference='Stop';$scriptRoot=Split-Path -Parent $MyInvocation.MyCommand.Path
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $scriptRoot 'layout-test-results'}
if(-not(Test-Path -LiteralPath $OutputDirectory)){New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null}
$provider=New-Object Microsoft.CSharp.CSharpCodeProvider
$p=New-Object System.CodeDom.Compiler.CompilerParameters
$p.GenerateExecutable=$true
$p.OutputAssembly=Join-Path $OutputDirectory 'LayoutHarness.exe'
$p.CompilerOptions='/target:exe /main:UDA.LayoutHarness /platform:anycpu /optimize+'
foreach($ref in @('System.dll','System.Data.dll','System.Security.dll','System.Core.dll','System.Drawing.dll','System.Management.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xml.dll','System.Xml.Linq.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.ServiceProcess.dll')){[void]$p.ReferencedAssemblies.Add($ref)}
$sources=[string[]]@(
  (Join-Path $scriptRoot 'UniversalDriverAssistant.cs'),
  (Join-Path $scriptRoot 'ResponsiveForm.cs'),
  (Join-Path $scriptRoot 'EnhancedFeatures.cs'),
  (Join-Path $scriptRoot 'DriverIndexCore.cs'),
  (Join-Path $scriptRoot 'InfApplicability.cs'),
  (Join-Path $scriptRoot 'DriverIndexIntegration.cs'),
  (Join-Path $scriptRoot 'OfflineRepo.cs'),
  (Join-Path $scriptRoot 'ServerStorage.cs'),
  (Join-Path $scriptRoot 'ClusterRouting.cs'),
  (Join-Path $scriptRoot 'LanDriverService.cs'),
  (Join-Path $scriptRoot 'LanEditionForm.cs'),
  (Join-Path $scriptRoot 'WebAdmin.cs'),
  (Join-Path $scriptRoot 'AiDriverTasks.cs'),
  (Join-Path $scriptRoot 'NicBootstrap.cs'),
  (Join-Path $scriptRoot 'Phase3Dispatch.cs'),
  (Join-Path $scriptRoot 'LayoutHarness.cs')
)
$r=$provider.CompileAssemblyFromFile($p,$sources)
if($r.Errors.Count){throw(($r.Errors|ForEach-Object{'{0}({1},{2}): {3}' -f $_.FileName,$_.Line,$_.Column,$_.ErrorText})-join[Environment]::NewLine)}
$config=Join-Path $scriptRoot 'DriverConfig.json'
$shots=@(
  @{w=800;h=600;page='home'},   @{w=800;h=600;page='hardware'}, @{w=800;h=600;page='drivers'}, @{w=800;h=600;page='log'},
  @{w=1024;h=768;page='home'},  @{w=1024;h=768;page='home';scale=1.5}, @{w=1024;h=768;page='home';scale=2.0},
  @{w=1024;h=768;page='hardware'}, @{w=1024;h=768;page='hardware';scale=2.0},
  @{w=1024;h=768;page='drivers'},  @{w=1024;h=768;page='drivers';scale=1.5},
  @{w=1366;h=768;page='home'},  @{w=1366;h=768;page='drivers'},
  @{w=1600;h=600;page='home'},
  @{w=1920;h=1080;page='home'}
)
foreach($shot in $shots){
  $suffix=''
  if($shot["scale"]){$suffix='-'+[int]($shot["scale"]*100)}
  $name="$($shot["w"])x$($shot["h"])-$($shot["page"])$suffix.png"
  $out=Join-Path $OutputDirectory $name
  $hargs=@('"'+$config+'"','"'+$out+'"',$shot["w"],$shot["h"],$shot["page"])
  if($shot["scale"]){$hargs+=[string]$shot["scale"]}
  $proc=Start-Process -FilePath $p.OutputAssembly -ArgumentList $hargs -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $OutputDirectory 'harness.stdout.txt') -RedirectStandardError (Join-Path $OutputDirectory 'harness.stderr.txt')
  if($proc.ExitCode -ne 0){throw('Layout render FAILED for '+$name+'. Exit='+$proc.ExitCode+' (see '+$OutputDirectory+')')}
  if(-not(Test-Path -LiteralPath $out)){throw('Layout render produced no screenshot: '+$out)}
  Write-Host ('  OK '+$name) -ForegroundColor Gray
}
Write-Host ('PASS: Layout render tests ('+$shots.Count+' screenshots)') -ForegroundColor Green
