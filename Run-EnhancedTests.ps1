<# .SYNOPSIS 测试机型整包、内存、磁盘健康、资源监控与导出校验。 #>
[CmdletBinding()]param([string]$OutputDirectory)
Set-StrictMode -Version 2.0;$ErrorActionPreference='Stop';$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $root 'enhanced-test-results'}
if(-not(Test-Path -LiteralPath $OutputDirectory)){New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null}
$provider=New-Object Microsoft.CSharp.CSharpCodeProvider;$p=New-Object System.CodeDom.Compiler.CompilerParameters;$p.GenerateExecutable=$true;$p.OutputAssembly=Join-Path $OutputDirectory 'EnhancedHarness.exe';$p.CompilerOptions='/target:exe /main:UDA.EnhancedHarness /platform:anycpu /optimize+'
foreach($ref in @('System.dll','System.Core.dll','System.Drawing.dll','System.Management.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xml.dll','System.Xml.Linq.dll')){[void]$p.ReferencedAssemblies.Add($ref)}
$sources=[string[]]@((Join-Path $root 'UniversalDriverAssistant.cs'),(Join-Path $root 'ResponsiveForm.cs'),(Join-Path $root 'EnhancedFeatures.cs'),(Join-Path $root 'DriverIndexCore.cs'),(Join-Path $root 'DriverIndexIntegration.cs'),(Join-Path $root 'EnhancedHarness.cs'))
$r=$provider.CompileAssemblyFromFile($p,$sources);if($r.Errors.Count){throw(($r.Errors|ForEach-Object{$_.ToString()})-join[Environment]::NewLine)}
$data=Join-Path $OutputDirectory 'data';$installed=Get-WmiObject Win32_PnPSignedDriver|Where-Object{$_.InfName-match'^oem\d+\.inf$'}|Select-Object -First 1;$arguments=@('"'+$data+'"');if($installed){$arguments+=('"'+[string]$installed.InfName+'"')};$proc=Start-Process -FilePath $p.OutputAssembly -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if($proc.ExitCode-ne 0){throw('Enhanced tests failed. Exit='+$proc.ExitCode)}
Write-Host ('PASS: mapping, hardware enrichment, resource sampling and '+$(if($installed){'real driver export.'}else{'export validation (no OEM driver available).'})) -ForegroundColor Green
