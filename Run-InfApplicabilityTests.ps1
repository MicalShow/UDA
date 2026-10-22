<# .SYNOPSIS 编译并运行 INF 适用性引擎测试 / Compile & run the INF applicability engine tests. #>
[CmdletBinding()]param([string]$OutputDirectory)
Set-StrictMode -Version 2.0;$ErrorActionPreference='Stop';$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $root 'inf-applicability-test-results'}
if(-not(Test-Path -LiteralPath $OutputDirectory)){New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null}
$provider=New-Object Microsoft.CSharp.CSharpCodeProvider
$p=New-Object System.CodeDom.Compiler.CompilerParameters
$p.GenerateExecutable=$true
$p.OutputAssembly=Join-Path $OutputDirectory 'InfApplicabilityHarness.exe'
$p.CompilerOptions='/target:exe /main:UDA.InfApplicabilityHarness /platform:anycpu /optimize+'
# Core is self-contained: BCL + System.Management (OsTarget) + System.Web.Extensions (JavaScriptSerializer).
foreach($ref in @('System.dll','System.Core.dll','System.Management.dll','System.Web.Extensions.dll')){[void]$p.ReferencedAssemblies.Add($ref)}
$sources=[string[]]@((Join-Path $root 'DriverIndexCore.cs'),(Join-Path $root 'InfApplicability.cs'),(Join-Path $root 'InfApplicabilityHarness.cs'))
$r=$provider.CompileAssemblyFromFile($p,$sources)
if($r.Errors.Count){throw(($r.Errors|ForEach-Object{'{0}({1},{2}): {3}' -f $_.FileName,$_.Line,$_.Column,$_.ErrorText})-join[Environment]::NewLine)}
$work=Join-Path $OutputDirectory 'work'
$proc=Start-Process -FilePath $p.OutputAssembly -ArgumentList @('"'+$work+'"') -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $OutputDirectory 'harness.stdout.txt') -RedirectStandardError (Join-Path $OutputDirectory 'harness.stderr.txt')
Get-Content (Join-Path $OutputDirectory 'harness.stdout.txt') | ForEach-Object { Write-Host $_ }
if($proc.ExitCode -ne 0){throw('INF applicability tests FAILED. Exit='+$proc.ExitCode+' (see '+($OutputDirectory)+')')}
Write-Host ('PASS: INF applicability engine tests ('+(Get-Item $p.OutputAssembly).Length+' bytes)') -ForegroundColor Green
