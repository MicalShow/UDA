<#
.SYNOPSIS
  编译并运行离线版端到端测试：仓库生成/加载/校验、增量更新、冲突去重、扫描安装、OS/架构装饰过滤、旧配置兼容。
#>
[CmdletBinding()]param([string]$OutputDirectory)
Set-StrictMode -Version 2.0;$ErrorActionPreference='Stop';$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $root 'offline-test-results'}
if(-not(Test-Path -LiteralPath $OutputDirectory)){New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null}
$provider=New-Object Microsoft.CSharp.CSharpCodeProvider
$p=New-Object System.CodeDom.Compiler.CompilerParameters
$p.GenerateExecutable=$true
$p.OutputAssembly=Join-Path $OutputDirectory 'OfflineHarness.exe'
$p.CompilerOptions='/target:exe /main:UDA.OfflineHarness /platform:anycpu /optimize+'
foreach($ref in @('System.dll','System.Data.dll','System.Security.dll','System.Core.dll','System.Drawing.dll','System.Management.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xml.dll','System.Xml.Linq.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.ServiceProcess.dll')){[void]$p.ReferencedAssemblies.Add($ref)}
$sources=[string[]]@(
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
  (Join-Path $root 'OfflineHarness.cs')
)
$r=$provider.CompileAssemblyFromFile($p,$sources)
if($r.Errors.Count){throw(($r.Errors|ForEach-Object{'{0}({1},{2}): {3}' -f $_.FileName,$_.Line,$_.Column,$_.ErrorText})-join[Environment]::NewLine)}
$work=Join-Path $OutputDirectory 'work'
$proc=Start-Process -FilePath $p.OutputAssembly -ArgumentList @('"'+$work+'"') -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $OutputDirectory 'harness.stdout.txt') -RedirectStandardError (Join-Path $OutputDirectory 'harness.stderr.txt')
Get-Content (Join-Path $OutputDirectory 'harness.stdout.txt') | ForEach-Object { Write-Host $_ }
if($proc.ExitCode -ne 0){throw('Offline repository tests FAILED. Exit='+$proc.ExitCode+' (see '+($OutputDirectory)+')')}
Write-Host ('PASS: Offline repository end-to-end tests ('+(Get-Item $p.OutputAssembly).Length+' bytes)') -ForegroundColor Green
