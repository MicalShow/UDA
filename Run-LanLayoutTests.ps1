<#
.SYNOPSIS
  编译并运行局域网络版布局渲染测试：多分辨率/多 DPI 下渲染 LAN 版页面并输出截图。
#>
[CmdletBinding()]param([string]$OutputDirectory)
Set-StrictMode -Version 2.0;$ErrorActionPreference='Stop';$scriptRoot=Split-Path -Parent $MyInvocation.MyCommand.Path
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $scriptRoot 'lan-layout-test-results'}
if(-not(Test-Path -LiteralPath $OutputDirectory)){New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null}
$provider=New-Object Microsoft.CSharp.CSharpCodeProvider
$p=New-Object System.CodeDom.Compiler.CompilerParameters
$p.GenerateExecutable=$true
$p.OutputAssembly=Join-Path $OutputDirectory 'LanLayoutHarness.exe'
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
  @{w=1024;h=768;page='home'},    @{w=1024;h=768;page='server'},
  @{w=1366;h=768;page='home'},    @{w=1366;h=768;page='server'},
  @{w=1920;h=1080;page='home'},   @{w=1920;h=1080;page='server'},
  @{w=1024;h=768;page='drivers';scale=1.25;name='dpi125'},
  @{w=1024;h=768;page='drivers';scale=1.5;name='dpi150'},
  @{w=1024;h=768;page='drivers';scale=2.0;name='dpi200'},
  @{w=1024;h=768;page='home';scale=1.5;name='dpi150'},
  @{w=1024;h=768;page='server';scale=1.5;name='dpi150'},
  @{w=1024;h=768;page='server';scale=2.0;name='dpi200'}
)
foreach($shot in $shots){
  $name="$($shot["w"])x$($shot["h"])-$($shot["page"])"
  if($shot["name"]){$name=$shot["name"]+'-'+$shot["page"]}
  $out=Join-Path $OutputDirectory ($name+'.png')
  $args=@('"'+$config+'"','"'+$out+'"',$shot["w"],$shot["h"],$shot["page"])
  if($shot["scale"]){$args+=[string]$shot["scale"]}else{$args+='1'}
  $args+='lan'
  $proc=Start-Process -FilePath $p.OutputAssembly -ArgumentList $args -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $OutputDirectory 'harness.stdout.txt') -RedirectStandardError (Join-Path $OutputDirectory 'harness.stderr.txt')
  if($proc.ExitCode -ne 0){throw('LAN layout render FAILED for '+$name+'. Exit='+$proc.ExitCode+' (see '+$OutputDirectory+')')}
  if(-not(Test-Path -LiteralPath $out)){throw('LAN layout render produced no screenshot: '+$out)}
  Write-Host ('  OK '+$name) -ForegroundColor Gray
}
Write-Host ('PASS: LAN layout render tests ('+$shots.Count+' screenshots)') -ForegroundColor Green
