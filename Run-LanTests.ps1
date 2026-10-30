<# .SYNOPSIS 编译并运行第三阶段 LAN 协议端到端测试 / Compile & run the Phase 3 LAN protocol tests.
.DESCRIPTION
  使用真实 LanDriverServer (HttpListener) 在本机回环临时端口与真实 LanDriverClient 进行端到端验证：
  健康检查、上报匹配、下载校验安装、下载哈希篡改、token 鉴权、路径穿越、幂等重复请求、取消、
  断网重试退避、网卡引导（有/无）、拒绝任意客户端/导入路径。全程使用合成 INF，不安装真实驱动、
  不注册服务、不开防火墙。
#>
[CmdletBinding()]param([string]$OutputDirectory)
Set-StrictMode -Version 2.0;$ErrorActionPreference='Stop';$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $root 'lan-test-results'}
if(-not(Test-Path -LiteralPath $OutputDirectory)){New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null}
$provider=New-Object Microsoft.CSharp.CSharpCodeProvider
$p=New-Object System.CodeDom.Compiler.CompilerParameters
$p.GenerateExecutable=$true
$p.OutputAssembly=Join-Path $OutputDirectory 'LanHarness.exe'
$p.CompilerOptions='/target:exe /main:UDA.LanHarness /platform:anycpu /optimize+'
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
  (Join-Path $root 'LanHarness.cs')
)
$r=$provider.CompileAssemblyFromFile($p,$sources)
if($r.Errors.Count){throw(($r.Errors|ForEach-Object{'{0}({1},{2}): {3}' -f $_.FileName,$_.Line,$_.Column,$_.ErrorText})-join[Environment]::NewLine)}
# 安装脚本必须包含服务失败自动恢复配置（第一次/第二次/后续失败自动重启，重置失败计数），
# 升级时先停止并移除已有服务，卸载时清理服务与防火墙——全部通过 dry-run 检查，不真实注册服务。
$installScript=Get-Content (Join-Path $root 'Install-LanService.ps1') -Raw
foreach($needle in @('sc.exe failure','reset= 86400','restart/5000/restart/5000/restart/5000','sc.exe delete','netsh advfirewall firewall delete rule')){
  if($installScript.IndexOf($needle,[System.StringComparison]::Ordinal) -lt 0){throw "Install-LanService.ps1 缺少服务恢复/清理配置: $needle"}
}
$work=Join-Path $OutputDirectory 'work'
$proc=Start-Process -FilePath $p.OutputAssembly -ArgumentList @('"'+$work+'"') -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $OutputDirectory 'harness.stdout.txt') -RedirectStandardError (Join-Path $OutputDirectory 'harness.stderr.txt')
Get-Content (Join-Path $OutputDirectory 'harness.stdout.txt') | ForEach-Object { Write-Host $_ }
if($proc.ExitCode -ne 0){throw('LAN protocol tests FAILED. Exit='+$proc.ExitCode+' (see '+($OutputDirectory)+')')}
Write-Host ('PASS: Phase 3 LAN protocol tests ('+(Get-Item $p.OutputAssembly).Length+' bytes)') -ForegroundColor Green
