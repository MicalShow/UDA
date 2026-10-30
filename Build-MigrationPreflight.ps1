[CmdletBinding()]param([string]$OutputDirectory='release-migration-preflight')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if(-not [IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory=Join-Path $root $OutputDirectory}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$compiler=New-Object Microsoft.CSharp.CSharpCodeProvider
$p=New-Object CodeDom.Compiler.CompilerParameters
$p.GenerateExecutable=$true
$p.OutputAssembly=Join-Path $OutputDirectory 'UDA-Database-Migration.exe'
$p.CompilerOptions='/target:winexe /main:UDA.DatabaseMigrationProgram /platform:anycpu /optimize+ /win32manifest:"'+(Join-Path $root 'app.manifest')+'"'
foreach($r in @('System.dll','System.Data.dll','System.Security.dll','System.Core.dll','System.Drawing.dll','System.Management.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xml.dll','System.Xml.Linq.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.ServiceProcess.dll')){[void]$p.ReferencedAssemblies.Add($r)}
$names=@('UniversalDriverAssistant.cs','ResponsiveForm.cs','EnhancedFeatures.cs','DriverIndexCore.cs','InfApplicability.cs','DriverIndexIntegration.cs','OfflineRepo.cs','ServerStorage.cs','ClusterRouting.cs','LanDriverService.cs','LanEditionForm.cs','WebAdmin.cs','AiDriverTasks.cs','NicBootstrap.cs','Phase3Dispatch.cs','DatabaseMigrationManager.cs')
$result=$compiler.CompileAssemblyFromFile($p,[string[]]($names|ForEach-Object{Join-Path $root $_}))
if($result.Errors.HasErrors){throw (($result.Errors|ForEach-Object{$_.ToString()}) -join "`r`n")}
Copy-Item -LiteralPath (Join-Path $root 'DatabaseProvider') -Destination $OutputDirectory -Recurse -Force
[IO.File]::WriteAllText(($p.OutputAssembly+'.config'),'<configuration><startup useLegacyV2RuntimeActivationPolicy="true"><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.6.2"/></startup><runtime><gcServer enabled="true"/><GCCpuGroup enabled="true"/><Thread_UseAllCpuGroups enabled="true"/><AppContextSwitchOverrides value="Switch.System.IO.UseLegacyPathHandling=false;Switch.System.IO.BlockLongPaths=false"/></runtime></configuration>')
Copy-Item -LiteralPath (Join-Path $root 'MIGRATION-PREFLIGHT.md') -Destination $OutputDirectory -Force
Write-Output $p.OutputAssembly
