<# .SYNOPSIS 编译并运行第二阶段集成测试 / Compile & run the Phase 2 integration tests.
.DESCRIPTION
  端到端验证驱动索引核心接入主引擎：硬件采集可测试抽象、包作用域过滤、从索引扫描到安装计划的
  全流程、退出码分类、白名单与去重、签名/管理员闸门、取消、配置向后兼容。全程离线，不调用
  真实 pnputil、不触发设备扫描、不依赖 WMI。
#>
[CmdletBinding()]param([string]$OutputDirectory)
Set-StrictMode -Version 2.0;$ErrorActionPreference='Stop';$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $root 'integration-test-results'}
if(-not(Test-Path -LiteralPath $OutputDirectory)){New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null}
$provider=New-Object Microsoft.CSharp.CSharpCodeProvider
$p=New-Object System.CodeDom.Compiler.CompilerParameters
$p.GenerateExecutable=$true
$p.OutputAssembly=Join-Path $OutputDirectory 'IntegrationHarness.exe'
$p.CompilerOptions='/target:exe /main:UDA.IntegrationHarness /platform:anycpu /optimize+'
# Same reference set as the other engine harnesses (Forms/Drawing/Management/Web.Extensions for the compiled sources).
foreach($ref in @('System.dll','System.Core.dll','System.Drawing.dll','System.Management.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xml.dll','System.Xml.Linq.dll')){[void]$p.ReferencedAssemblies.Add($ref)}
$sources=[string[]]@(
  (Join-Path $root 'UniversalDriverAssistant.cs'),
  (Join-Path $root 'ResponsiveForm.cs'),
  (Join-Path $root 'EnhancedFeatures.cs'),
  (Join-Path $root 'DriverIndexCore.cs'),
  (Join-Path $root 'DriverIndexIntegration.cs'),
  (Join-Path $root 'IntegrationHarness.cs')
)
$r=$provider.CompileAssemblyFromFile($p,$sources)
if($r.Errors.Count){throw(($r.Errors|ForEach-Object{'{0}({1},{2}): {3}' -f $_.FileName,$_.Line,$_.Column,$_.ErrorText})-join[Environment]::NewLine)}
$work=Join-Path $OutputDirectory 'work'
$proc=Start-Process -FilePath $p.OutputAssembly -ArgumentList @('"'+$work+'"') -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $OutputDirectory 'harness.stdout.txt') -RedirectStandardError (Join-Path $OutputDirectory 'harness.stderr.txt')
Get-Content (Join-Path $OutputDirectory 'harness.stdout.txt') | ForEach-Object { Write-Host $_ }
if($proc.ExitCode -ne 0){throw('Integration tests FAILED. Exit='+$proc.ExitCode+' (see '+$OutputDirectory+')')}
Write-Host ('PASS: Phase 2 integration tests ('+(Get-Item $p.OutputAssembly).Length+' bytes)') -ForegroundColor Green
