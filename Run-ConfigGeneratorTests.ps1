<# .SYNOPSIS 配置生成器布局与 JSON 往返测试 / Config editor layout and JSON round-trip tests. #>
[CmdletBinding()]param([string]$OutputDirectory)
Set-StrictMode -Version 2.0;$ErrorActionPreference='Stop';$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $root 'config-generator-test-results'}
if(-not(Test-Path -LiteralPath $OutputDirectory)){New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null}
$exe=Join-Path $OutputDirectory 'ConfigEditorHarness.exe';$provider=New-Object Microsoft.CSharp.CSharpCodeProvider;$p=New-Object System.CodeDom.Compiler.CompilerParameters
$p.GenerateExecutable=$true;$p.OutputAssembly=$exe;$p.CompilerOptions='/target:exe /main:UDA.ConfigEditorHarness /platform:anycpu /optimize+'
foreach($ref in @('System.dll','System.Core.dll','System.Drawing.dll','System.Management.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xml.dll','System.Xml.Linq.dll')){[void]$p.ReferencedAssemblies.Add($ref)}
$src=[string[]]@((Join-Path $root 'UniversalDriverAssistant.cs'),(Join-Path $root 'ResponsiveForm.cs'),(Join-Path $root 'EnhancedFeatures.cs'),(Join-Path $root 'DriverIndexCore.cs'),(Join-Path $root 'DriverIndexIntegration.cs'),(Join-Path $root 'ConfigEditorForm.cs'),(Join-Path $root 'ConfigEditorHarness.cs'))
$r=$provider.CompileAssemblyFromFile($p,$src);if($r.Errors.Count){throw(($r.Errors|ForEach-Object{$_.ToString()})-join[Environment]::NewLine)}
$cases=@(
  @{N='1024x700';W=1024;H=700;S=1.0},@{N='800x600';W=800;H=600;S=1.0},@{N='683x512-dpi150';W=683;H=512;S=1.5},@{N='512x384-dpi200';W=512;H=384;S=2.0}
);$failed=@();foreach($c in $cases){foreach($tab in 0..4){$png=Join-Path $OutputDirectory ($c.N+'-tab'+$tab+'.png');$a=@('"'+(Join-Path $root 'DriverConfig.json')+'"','"'+$png+'"',$c.W,$c.H,$tab,$c.S);$x=Start-Process $exe -ArgumentList $a -Wait -PassThru -WindowStyle Hidden;$lines=@($(if(Test-Path($png+'.txt')){Get-Content($png+'.txt')}else{'missing report'}));if($x.ExitCode-ne 0-or $lines.Count-ne 0-or-not(Test-Path $png)-or-not(Test-Path($png+'.json'))){$failed+=($c.N+'-tab'+$tab)}}}
if($failed.Count){throw('Config generator tests failed: '+($failed-join', '))};Write-Host ('All config generator tests passed. Cases: '+($cases.Count*5)) -ForegroundColor Green
