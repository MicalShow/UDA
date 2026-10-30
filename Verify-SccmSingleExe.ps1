param([string]$OutputDirectory = 'web-console-sccm-verified')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root=Join-Path $PSScriptRoot ($OutputDirectory+'\sccm-launcher-'+[Guid]::NewGuid().ToString('N'))
$payloadDir=Join-Path $root 'payload';New-Item -ItemType Directory -Path $payloadDir -Force|Out-Null
$provider=New-Object Microsoft.CSharp.CSharpCodeProvider
$parameters=New-Object CodeDom.Compiler.CompilerParameters
$parameters.GenerateExecutable=$true;$parameters.OutputAssembly=Join-Path $payloadDir 'UniversalDriverAssistant.exe'
$parameters.CompilerOptions='/target:exe /platform:anycpu'
[void]$parameters.ReferencedAssemblies.Add('System.dll')
$source=@'
using System;using System.IO;
class FakeDeployment {static int Main(string[] args){
  if(args.Length<6||args[0]!="--sccm")return 9;
  File.WriteAllLines(args[1],args);File.WriteAllText(args[1]+".cwd",Environment.CurrentDirectory);
  return int.Parse(args[args.Length-1]);
}}
'@
$compiled=$provider.CompileAssemblyFromSource($parameters,$source)
if($compiled.Errors.Count){throw ($compiled.Errors|Out-String)}
$zip=Join-Path $root 'embedded.zip';[IO.Compression.ZipFile]::CreateFromDirectory($payloadDir,$zip)
$single=Join-Path $root 'UDA-Client.exe'
[IO.File]::Copy((Join-Path $PSScriptRoot ($OutputDirectory+'\Lan\UDA-Client-Launcher.exe')),$single)
$zipBytes=[IO.File]::ReadAllBytes($zip)
$stream=New-Object IO.FileStream($single,[IO.FileMode]::Append,[IO.FileAccess]::Write)
try{$stream.Write($zipBytes,0,$zipBytes.Length);$magic=[Text.Encoding]::ASCII.GetBytes('UDAZIP01');$stream.Write($magic,0,8);$size=[BitConverter]::GetBytes([long]$zipBytes.Length);$stream.Write($size,0,8)}finally{$stream.Dispose()}
# An unrelated adjacent ZIP must not hijack the executable embedded inside the single EXE.
[IO.File]::WriteAllBytes((Join-Path $root 'ClientPayload.zip'),[byte[]]@(1,2,3))
foreach($expected in @(0,1,2,3010)){
  $report=Join-Path $root ('args with spaces-'+$expected+'.txt')
  $info=New-Object Diagnostics.ProcessStartInfo
  $info.FileName=$single;$info.UseShellExecute=$false;$info.CreateNoWindow=$true
  $info.Arguments='--sccm "'+$report+'" --lan "http://192.0.2.1:8731" "value with spaces" --exit '+$expected
  $process=[Diagnostics.Process]::Start($info)
  if(-not $process.WaitForExit(20000)){$process.Kill();throw 'Unattended single EXE timed out'}
  if($process.ExitCode -ne $expected){throw ('Exit code not forwarded: expected '+$expected+' actual '+$process.ExitCode)}
  $received=[IO.File]::ReadAllLines($report)
  if($received[1] -ne $report -or $received[3] -ne 'http://192.0.2.1:8731' -or $received[4] -ne 'value with spaces'){throw 'Arguments with spaces were not preserved'}
  $working=[IO.File]::ReadAllText($report+'.cwd')
  if(Test-Path -LiteralPath $working){throw 'Extracted client directory was not cleaned'}
}
Write-Output 'PASS: real single EXE extraction, unattended argument forwarding, 0/1/2/3010 exit propagation, adjacent ZIP rejection and cleanup (fake child; no driver installation)'
