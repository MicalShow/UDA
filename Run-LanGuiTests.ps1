<#
.SYNOPSIS
  编译并运行局域网络版 GUI 状态测试：多轮安装入口、busy 恢复、三种 DPI 底部边界。
#>
[CmdletBinding()]param([string]$OutputDirectory)
Set-StrictMode -Version 2.0;$ErrorActionPreference='Stop';$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $root 'lan-gui-test-results'}
# Normalize to an absolute path relative to the script root so relative -OutputDirectory
# values cannot be resolved against an unpredictable current working directory.
if(-not([IO.Path]::IsPathRooted($OutputDirectory))){$OutputDirectory=Join-Path $root $OutputDirectory}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$provider=New-Object Microsoft.CSharp.CSharpCodeProvider
$p=New-Object System.CodeDom.Compiler.CompilerParameters
$p.GenerateExecutable=$true
$p.OutputAssembly=Join-Path $OutputDirectory 'LanGuiHarness.exe'
$p.CompilerOptions='/target:exe /main:UDA.LanGuiHarness /platform:anycpu /optimize+'
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
  (Join-Path $root 'LanEditionForm.cs'),
  (Join-Path $root 'WebAdmin.cs'),
  (Join-Path $root 'NicBootstrap.cs'),
  (Join-Path $root 'Phase3Dispatch.cs'),
  (Join-Path $root 'LanGuiHarness.cs')
)
$r=$provider.CompileAssemblyFromFile($p,$sources)
if($r.Errors.Count){throw(($r.Errors|ForEach-Object{'{0}({1},{2}): {3}' -f $_.FileName,$_.Line,$_.Column,$_.ErrorText})-join[Environment]::NewLine)}
$work=Join-Path $OutputDirectory 'work'
[void][IO.Directory]::CreateDirectory($work)
$stdoutPath=Join-Path $OutputDirectory 'harness.stdout.txt'
$stderrPath=Join-Path $OutputDirectory 'harness.stderr.txt'
$psi=New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName=$p.OutputAssembly
$psi.Arguments='"'+$work+'"'
$psi.UseShellExecute=$false
$psi.CreateNoWindow=$true
$psi.RedirectStandardOutput=$true
$psi.RedirectStandardError=$true
$proc=New-Object System.Diagnostics.Process
$proc.StartInfo=$psi
[void]$proc.Start()
# Drain redirected streams asynchronously so a full pipe buffer cannot deadlock the
# harness, then persist them to files that exist even when the harness prints nothing.
$stdoutTask=$proc.StandardOutput.ReadToEndAsync()
$stderrTask=$proc.StandardError.ReadToEndAsync()
# Hard overall timeout: if the harness does not exit on its own, kill it and FAIL (never hang).
if(-not $proc.WaitForExit(900000)){
  try{$proc.Kill()}catch{}
  try{$stdoutTask.Wait(5000)|Out-Null;$stderrTask.Wait(5000)|Out-Null}catch{}
  [IO.File]::WriteAllText($stdoutPath,$stdoutTask.Result,[Text.Encoding]::UTF8)
  [IO.File]::WriteAllText($stderrPath,$stderrTask.Result,[Text.Encoding]::UTF8)
  throw('LAN GUI state tests TIMED OUT after 900s and the harness process was killed (PID='+$proc.Id+'). See '+$OutputDirectory)
}
$exitCode=$proc.ExitCode
$stdoutTask.Wait(10000)|Out-Null
$stderrTask.Wait(10000)|Out-Null
[IO.File]::WriteAllText($stdoutPath,$stdoutTask.Result,[Text.Encoding]::UTF8)
[IO.File]::WriteAllText($stderrPath,$stderrTask.Result,[Text.Encoding]::UTF8)
$stdoutText=$stdoutTask.Result;$stderrText=$stderrTask.Result
if($stdoutText){$stdoutText.TrimEnd()|ForEach-Object{Write-Host $_}}
if($stderrText){$stderrText.TrimEnd()|ForEach-Object{Write-Host $_}}
if($exitCode -ne 0){throw('LAN GUI state tests FAILED. Exit='+$exitCode+' (see '+$OutputDirectory+')')}
Write-Host ('PASS: LAN GUI state tests ('+(Get-Item $p.OutputAssembly).Length+' bytes)') -ForegroundColor Green
