<# .SYNOPSIS 验证已存在连接优先复用，避免 Windows 1219 凭据冲突。 #>
[CmdletBinding()]param([string]$OutputDirectory)
Set-StrictMode -Version 2.0;$ErrorActionPreference='Stop';$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $root 'network-test-results'}
if(-not(Test-Path -LiteralPath $OutputDirectory)){New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null}
$provider=New-Object Microsoft.CSharp.CSharpCodeProvider;$p=New-Object System.CodeDom.Compiler.CompilerParameters;$p.GenerateExecutable=$true;$p.OutputAssembly=Join-Path $OutputDirectory 'NetConnectionHarness.exe';$p.CompilerOptions='/target:exe /main:UDA.NetConnectionHarness /platform:anycpu /optimize+'
foreach($ref in @('System.dll','System.Core.dll','System.Drawing.dll','System.Management.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xml.dll','System.Xml.Linq.dll')){[void]$p.ReferencedAssemblies.Add($ref)}
$sources=[string[]]@((Join-Path $root 'UniversalDriverAssistant.cs'),(Join-Path $root 'ResponsiveForm.cs'),(Join-Path $root 'EnhancedFeatures.cs'),(Join-Path $root 'DriverIndexCore.cs'),(Join-Path $root 'DriverIndexIntegration.cs'),(Join-Path $root 'NetConnectionHarness.cs'))
$r=$provider.CompileAssemblyFromFile($p,$sources);if($r.Errors.Count){throw(($r.Errors|ForEach-Object{$_.ToString()})-join[Environment]::NewLine)}
$testPath=Join-Path $OutputDirectory 'already-accessible';if(-not(Test-Path $testPath)){New-Item -ItemType Directory -Path $testPath|Out-Null}
$proc=Start-Process -FilePath $p.OutputAssembly -ArgumentList @('"'+$testPath+'"') -Wait -PassThru -WindowStyle Hidden
if($proc.ExitCode-ne 0){throw('Network reuse test failed. Exit='+$proc.ExitCode)}
Write-Host 'PASS: accessible source reused without credential remapping (maintenance + deployment).' -ForegroundColor Green
