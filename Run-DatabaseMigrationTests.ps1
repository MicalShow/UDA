[CmdletBinding()]param([string]$OutputDirectory = 'validation-database-tls')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $taskRoot $OutputDirectory }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$parameters = New-Object System.CodeDom.Compiler.CompilerParameters
$parameters.GenerateExecutable = $true
$parameters.OutputAssembly = Join-Path $OutputDirectory 'DatabaseMigrationHarness.exe'
$parameters.CompilerOptions = '/target:exe /main:UDA.DatabaseMigrationHarness /platform:anycpu /optimize+'
foreach ($reference in @('System.dll','System.Data.dll','System.Security.dll','System.Core.dll','System.Drawing.dll','System.Management.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xml.dll','System.Xml.Linq.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.ServiceProcess.dll')) { [void]$parameters.ReferencedAssemblies.Add($reference) }
$names = @('UniversalDriverAssistant.cs','ResponsiveForm.cs','EnhancedFeatures.cs','DriverIndexCore.cs','InfApplicability.cs','DriverIndexIntegration.cs','OfflineRepo.cs','ServerStorage.cs','ClusterRouting.cs','LanDriverService.cs','LanEditionForm.cs','WebAdmin.cs','NicBootstrap.cs','Phase3Dispatch.cs','DatabaseMigrationManager.cs','DatabaseMigrationHarness.cs')
$result = $provider.CompileAssemblyFromFile($parameters, [string[]]($names | ForEach-Object { Join-Path $taskRoot $_ }))
if ($result.Errors.Count) { throw (($result.Errors | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine) }
& $parameters.OutputAssembly (Join-Path $OutputDirectory 'work')
if ($LASTEXITCODE -ne 0) { throw 'Database migration TLS/diagnostic tests failed' }
