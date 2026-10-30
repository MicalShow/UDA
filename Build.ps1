<#
.SYNOPSIS
  构建万能驱动助手 / Build Universal Driver Assistant (Phase 3+4: dual-version product + NIC bootstrap).
.DESCRIPTION
  使用 Windows PowerShell 5.1 自带 C# 编译器生成无需额外运行时的 EXE。第三阶段明确生成两套目录：
    <Out>/Offline  - 离线版：配置工具、离线仓库生成器、NIC 引导管理器、离线主程序
    <Out>/Lan      - 局域网在线版：配置工具、离线仓库生成器、NIC 引导管理器、客户端主程序、服务端程序
  所有 EXE 由同一份源码集编译，仅入口点 (/main) 与目标类型不同，配置向后兼容。
  第四阶段：若 -BootstrapRepo 指向一个经许可且通过校验的 NIC 引导仓库（默认为脚本旁的 NicBootstrapRepo），
  Build.ps1 调用 NicBootstrapManager.exe package 将其复制进 Offline 与 Lan 两套产物的 NicBootstrap\ 子目录；
  否则跳过，客户端缺库时安全降级。Build.ps1 自身不下载任何第三方驱动二进制。
#>
[CmdletBinding()]
param([string]$OutputDirectory, [string]$BootstrapRepo)
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $scriptRoot 'Build-Icons.ps1')
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $scriptRoot 'bin' }
if ([string]::IsNullOrWhiteSpace($BootstrapRepo)) { $BootstrapRepo = Join-Path $scriptRoot 'NicBootstrapRepo' }
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath (Join-Path $scriptRoot 'DatabaseProvider\MySqlConnector.dll'))) { throw 'Missing database provider. Run: python Tools\fetch_mysql_provider.py' }

# Single source set; each EXE picks its entry point via /main. Order is stable for reproducible builds.
$sources = @(
  (Join-Path $scriptRoot 'UniversalDriverAssistant.cs'),
  (Join-Path $scriptRoot 'ResponsiveForm.cs'),
  (Join-Path $scriptRoot 'EnhancedFeatures.cs'),
  (Join-Path $scriptRoot 'DriverIndexCore.cs'),
  (Join-Path $scriptRoot 'InfApplicability.cs'),
  (Join-Path $scriptRoot 'DriverIndexIntegration.cs'),
  (Join-Path $scriptRoot 'DriverSelectionWorker.cs'),
  (Join-Path $scriptRoot 'OfflineRepo.cs'),
  (Join-Path $scriptRoot 'ServerStorage.cs'),
  (Join-Path $scriptRoot 'ClusterRouting.cs'),
  (Join-Path $scriptRoot 'LanDriverService.cs'),
  (Join-Path $scriptRoot 'LanEditionForm.cs'),
  (Join-Path $scriptRoot 'WebAdmin.cs'),
  (Join-Path $scriptRoot 'AiDriverTasks.cs'),
  (Join-Path $scriptRoot 'NicBootstrap.cs'),
  (Join-Path $scriptRoot 'Phase3Dispatch.cs'),
  (Join-Path $scriptRoot 'ConfigEditorForm.cs'),
  (Join-Path $scriptRoot 'ServerSetup.cs'),
  (Join-Path $scriptRoot 'DatabaseMigrationManager.cs'),
  (Join-Path $scriptRoot 'ServerManager.cs'),
  (Join-Path $scriptRoot 'ClientLauncher.cs')
) | Where-Object { Test-Path -LiteralPath $_ }
if ($sources.Count -eq 0) { throw "没有找到 C# 源文件: $scriptRoot" }

# Framework assemblies only (no third-party). System.IO.Compression is needed for the LAN ZIP download.
$refs = @('System.dll','System.Data.dll','System.Security.dll','System.Core.dll','System.Drawing.dll','System.Management.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xml.dll','System.Xml.Linq.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.ServiceProcess.dll')
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$winManifest = Join-Path $scriptRoot 'app.manifest'
$genManifest = Join-Path $scriptRoot 'config-generator.manifest'

function Compile-Exe {
  param([string]$OutDir, [string]$ExeName, [string]$MainClass, [string]$Target, [string]$Manifest, [string]$Payload)
  $out = Join-Path $OutDir $ExeName
  if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
  $p = New-Object System.CodeDom.Compiler.CompilerParameters
  $p.GenerateExecutable = $true
  $p.GenerateInMemory = $false
  $p.OutputAssembly = $out
  $opts = '/target:' + $Target + ' /platform:anycpu /optimize+ /main:' + $MainClass
  $iconKind = if ($MainClass -match 'Server|Service|Migration') { 'Server' } else { 'Client' }
  $opts += ' /win32icon:"' + (Join-Path $scriptRoot ('Assets\UDA-' + $iconKind + '.ico')) + '"'
  if (![string]::IsNullOrWhiteSpace($Manifest)) { $opts += ' /win32manifest:"' + $Manifest + '"' }
  $p.CompilerOptions = $opts
  foreach ($ref in $refs) { [void]$p.ReferencedAssemblies.Add($ref) }
  if ($Payload) { [void]$p.EmbeddedResources.Add($Payload) }
  $r = $provider.CompileAssemblyFromFile($p, [string[]]$sources)
  if ($r.Errors.Count -gt 0) {
    $messages = $r.Errors | ForEach-Object { '{0}({1},{2}): {3} {4}' -f $_.FileName,$_.Line,$_.Column,$_.ErrorNumber,$_.ErrorText }
    throw ('编译失败 ' + $ExeName + ':' + [Environment]::NewLine + ($messages -join [Environment]::NewLine))
  }
  Write-Host ("  {0,-32} {1} bytes  SHA256 {2}" -f $ExeName,(Get-Item -LiteralPath $out).Length,(Get-FileHash -Algorithm SHA256 -LiteralPath $out).Hash) -ForegroundColor Gray
  Copy-Item -LiteralPath (Join-Path $scriptRoot 'UDA.Runtime.config') -Destination ($out + '.config') -Force
}

function Build-Directory {
  param([string]$Dir, [bool]$IncludeServer, [string]$Label)
  $resolvedBuildDir = [IO.Path]::GetFullPath($Dir)
  $workspacePrefix = [IO.Path]::GetFullPath($scriptRoot).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
  if (-not $resolvedBuildDir.StartsWith($workspacePrefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedBuildDir) -notin @('Offline','Lan')) { throw '构建清理目标必须是本项目下的 Offline 或 Lan 产物目录，已拒绝危险路径。' }
  $Dir = $resolvedBuildDir
  if (Test-Path -LiteralPath $Dir) { Remove-Item -LiteralPath $Dir -Recurse -Force }
  New-Item -ItemType Directory -Path $Dir -Force | Out-Null
  Write-Host "构建 $Label -> $Dir" -ForegroundColor Cyan
  # Main program (GUI + --repo offline + --lan client). requireAdministrator so pnputil install works.
  Compile-Exe $Dir 'UniversalDriverAssistant.exe' 'UDA.Program' 'winexe' $winManifest
  if ($IncludeServer) {
    Copy-Item -LiteralPath (Join-Path $Dir 'UniversalDriverAssistant.exe') -Destination (Join-Path $Dir '万能驱动助手-局域网络版.exe') -Force
    Copy-Item -LiteralPath (Join-Path $Dir 'UniversalDriverAssistant.exe.config') -Destination (Join-Path $Dir '万能驱动助手-局域网络版.exe.config') -Force
  }
  Compile-Exe $Dir 'UDA-Driver-Selection.exe' 'UDA.DriverSelectionProgram' 'exe' $genManifest
  # Configuration generator (GUI, asInvoker).
  Compile-Exe $Dir 'DriverConfigGenerator.exe' 'UDA.ConfigGeneratorProgram' 'winexe' $genManifest
  # Offline repository generator / config tool (console, asInvoker).
  Compile-Exe $Dir 'OfflineRepoGenerator.exe' 'UDA.OfflineRepoProgram' 'exe' $genManifest
  # NIC bootstrap manager: import/coverage/verify/package (console, asInvoker). Phase 4.
  Compile-Exe $Dir 'NicBootstrapManager.exe' 'UDA.NicBootstrapProgram' 'exe' $genManifest
  # LAN server (console, asInvoker) - only in the Lan directory.
  if ($IncludeServer) {
    Compile-Exe $Dir 'LanDriverService.exe' 'UDA.LanServerProgram' 'exe' $genManifest
    Compile-Exe $Dir 'UDA-Server-Manager.exe' 'UDA.ServerManagerProgram' 'winexe' $winManifest
    Compile-Exe $Dir 'UDA-Database-Migration.exe' 'UDA.DatabaseMigrationProgram' 'winexe' $winManifest
    Compile-Exe $Dir 'UDA-Client-Launcher.exe' 'UDA.ClientLauncherProgram' 'winexe' $genManifest
  }
  if ($IncludeServer) { [IO.File]::WriteAllText((Join-Path $Dir 'LanEdition.flag'), 'LAN', (New-Object Text.UTF8Encoding($false))) }
  # Ship the config, SCCM registration script, NIC sources catalog and docs.
  $files = @('DriverConfig.json','Collect-UdaDriverFailure.ps1','HP-AUDIO.md','EXTENSION-DRIVERS.md','MANUAL-DRIVER-VERSIONS.md','Register-SCCM.ps1','Invoke-UdaSccm.ps1','SCCM-AUTODEPLOY.md','AI-DRIVER-TASKS.md','ONLINE-MAINTENANCE.md','README.md','DEPLOY-LAN.md','DATABASE-MIGRATION.md','MIGRATION-PREFLIGHT.md','Get-UdaMachineInfo.ps1','MACHINE-PACKAGES.md','SCAN-PERFORMANCE.md','ARCHIVE-VERIFICATION.md','UPGRADE-SERVICE-STOP.md','INCREMENTAL-IMPORT.md','NicBootstrapSources.json','Verify-PhysicalNetworkClient.ps1','Verify-PhysicalClusterClient.ps1')
  foreach ($f in $files) { $src = Join-Path $scriptRoot $f; if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $Dir -Force } }
}

# Copy a licensed+verified NIC bootstrap repo into both products (Phase 4). Safe downgrade: if the source
# repo is absent or fails packaging verification (license/signature/integrity), nothing is copied and the
# build still succeeds. Build.ps1 never downloads drivers; the operator prepares the repo with NicBootstrapManager.exe.
function Copy-BootstrapRepo {
  param([string]$Src, [string]$OfflineDir, [string]$LanDir)
  $manager = Join-Path $OfflineDir 'NicBootstrapManager.exe'
  if (-not (Test-Path -LiteralPath $Src)) {
    Write-Host ("无 NIC bootstrap 仓库（" + $Src + "），跳过打包 / No NicBootstrapRepo prepared; clients degrade gracefully") -ForegroundColor DarkGray
    return
  }
  if (-not (Test-Path -LiteralPath $manager)) { Write-Host "NicBootstrapManager.exe 未构建，跳过 bootstrap 打包 / manager missing, skipping" -ForegroundColor DarkGray; return }
  $tmpOut = Join-Path $env:TEMP ('uda-nic-package-' + [Guid]::NewGuid().ToString('N') + '.txt')
  $argList = @('package','--repo',$Src,'--offline',$OfflineDir,'--lan',$LanDir)
  Write-Host ("打包 NIC bootstrap 仓库 / Packaging bootstrap repo: " + $Src) -ForegroundColor Cyan
  $proc = Start-Process -FilePath $manager -ArgumentList $argList -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput $tmpOut -RedirectStandardError ($tmpOut + '.err')
  if (Test-Path -LiteralPath $tmpOut) { Get-Content -LiteralPath $tmpOut -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ } }
  if (Test-Path -LiteralPath ($tmpOut + '.err')) { Get-Content -LiteralPath ($tmpOut + '.err') -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ } }
  Remove-Item -LiteralPath $tmpOut, ($tmpOut + '.err') -Force -ErrorAction SilentlyContinue
  if ($proc.ExitCode -eq 0) { Write-Host "NIC bootstrap 仓库已复制进 Offline 与 Lan / Packaged bootstrap repo into both products" -ForegroundColor Green }
  else { Write-Host "NIC bootstrap 仓库未通过许可/校验，跳过复制（构建继续）/ Bootstrap repo not licensed/verified; skipped (build continues)" -ForegroundColor Yellow }
}

$offlineDir = Join-Path $OutputDirectory 'Offline'
$lanDir = Join-Path $OutputDirectory 'Lan'
Build-Directory $offlineDir $false '离线版 / Offline'
Build-Directory $lanDir $true '局域网在线版 / LAN'
Copy-Item -LiteralPath (Join-Path $scriptRoot 'DatabaseProvider') -Destination $lanDir -Recurse -Force
Copy-Item -LiteralPath (Join-Path $scriptRoot 'database') -Destination $lanDir -Recurse -Force
# LAN-only service install script (does not register a service or open firewall during tests).
$installScript = Join-Path $scriptRoot 'Install-LanService.ps1'
if (Test-Path -LiteralPath $installScript) { Copy-Item -LiteralPath $installScript -Destination $lanDir -Force }
# Phase 4: copy a licensed+verified NIC bootstrap repo into both products (skipped if none prepared).
Copy-BootstrapRepo -Src $BootstrapRepo -OfflineDir $offlineDir -LanDir $lanDir

# The service clones this template and injects its public URL/token when an administrator
# requests a client package from the web console.
$clientTemplate = Join-Path $lanDir 'ClientTemplate'
New-Item -ItemType Directory -Path $clientTemplate -Force | Out-Null
$clientFiles = @('UDA-Driver-Selection.exe','UDA-Driver-Selection.exe.config','UniversalDriverAssistant.exe','UniversalDriverAssistant.exe.config','万能驱动助手-局域网络版.exe','万能驱动助手-局域网络版.exe.config','UDA-Client-Launcher.exe','UDA-Client-Launcher.exe.config','DriverConfig.json','LanEdition.flag','README.md','DEPLOY-LAN.md','Collect-UdaDriverFailure.ps1','HP-AUDIO.md','EXTENSION-DRIVERS.md','MANUAL-DRIVER-VERSIONS.md')
foreach ($f in $clientFiles) {
  $src = Join-Path $lanDir $f
  if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $clientTemplate -Force }
}
$lanBootstrap = Join-Path $lanDir 'NicBootstrap'
if (Test-Path -LiteralPath $lanBootstrap) { Copy-Item -LiteralPath $lanBootstrap -Destination $clientTemplate -Recurse -Force }

# One-click server installer registers the service and opens the Web console.
$packagesDir = Join-Path $OutputDirectory 'Packages'
$packagesDir = [IO.Path]::GetFullPath($packagesDir)
$packageWorkspacePrefix = [IO.Path]::GetFullPath($scriptRoot).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
if (-not $packagesDir.StartsWith($packageWorkspacePrefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($packagesDir) -ne 'Packages') { throw 'Package cleanup must stay in this project output directory' }
if (Test-Path -LiteralPath $packagesDir) { Remove-Item -LiteralPath $packagesDir -Recurse -Force }
New-Item -ItemType Directory -Path $packagesDir -Force | Out-Null
$serverStage = Join-Path $packagesDir 'UDA-Server-Setup'
New-Item -ItemType Directory -Path $serverStage -Force | Out-Null
$setupExe = Join-Path $serverStage '万能驱动助手-服务端安装程序.exe'
# The self-extracting IExpress wrapper cannot CreateProcess an executable whose manifest directly
# requires elevation (Win32 error 740). Start asInvoker, then the installer explicitly relaunches
# itself with the runas verb and waits, keeping IExpress' temporary payload alive.
Compile-Exe $serverStage '万能驱动助手-服务端安装程序.exe' 'UDA.ServerSetupProgram' 'winexe' $genManifest
$serverFiles = @('LanDriverService.exe','LanDriverService.exe.config','UDA-Server-Manager.exe','UDA-Server-Manager.exe.config','UDA-Database-Migration.exe','UDA-Database-Migration.exe.config','Install-LanService.ps1','OfflineRepoGenerator.exe','OfflineRepoGenerator.exe.config','README.md','Collect-UdaDriverFailure.ps1','HP-AUDIO.md','EXTENSION-DRIVERS.md','MANUAL-DRIVER-VERSIONS.md','ONLINE-MAINTENANCE.md','AI-DRIVER-TASKS.md','DEPLOY-LAN.md','DATABASE-MIGRATION.md','Verify-PhysicalNetworkClient.ps1','Verify-PhysicalClusterClient.ps1','Invoke-UdaSccm.ps1','SCCM-AUTODEPLOY.md')
foreach ($f in $serverFiles) {
  $src = Join-Path $lanDir $f
  if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $serverStage -Force }
}
Copy-Item -LiteralPath $clientTemplate -Destination $serverStage -Recurse -Force
Copy-Item -LiteralPath (Join-Path $lanDir 'DatabaseProvider') -Destination $serverStage -Recurse -Force
Copy-Item -LiteralPath (Join-Path $lanDir 'database') -Destination $serverStage -Recurse -Force
$serverZip = Join-Path $packagesDir 'UDA-Server-Setup.zip'
Compress-Archive -Path (Join-Path $serverStage '*') -DestinationPath $serverZip -CompressionLevel Optimal -Force

# Build a single self-extracting EXE with Windows' built-in IExpress. The elevated GUI installer
# extracts ServerPayload.zip internally; users only need to double-click one file.
$payloadZip = Join-Path $serverStage 'ServerPayload.zip'
$payloadItems = Get-ChildItem -LiteralPath $serverStage | Where-Object { $_.Name -ne '万能驱动助手-服务端安装程序.exe' -and $_.Name -ne 'ServerPayload.zip' }
Compress-Archive -LiteralPath $payloadItems.FullName -DestinationPath $payloadZip -CompressionLevel Optimal -Force
$sedPath = Join-Path $packagesDir 'server-setup.sed'
$singleSetup = Join-Path $packagesDir 'UDA-Server-Setup-v5.3.45.exe'
$setupSource = $serverStage.TrimEnd('\') + '\'
$sed = @(
  '[Version]','Class=IEXPRESS','SEDVersion=3','[Options]','PackagePurpose=InstallApp','ShowInstallProgramWindow=1',
  'HideExtractAnimation=0','UseLongFileName=1','InsideCompressed=0','CAB_FixedSize=0','CAB_ResvCodeSigning=0',
  'RebootMode=N','InstallPrompt=','DisplayLicense=','FinishMessage=',('TargetName=' + $singleSetup),
  'FriendlyName=Universal Driver Assistant Server Setup','AppLaunched=UDA-Server-Setup-GUI.exe','PostInstallCmd=<None>',
  'AdminQuietInstCmd=','UserQuietInstCmd=','SourceFiles=SourceFiles','[SourceFiles]',('SourceFiles0=' + $setupSource),
  '[SourceFiles0]','%FILE0%=','%FILE1%=','[Strings]','FILE0="UDA-Server-Setup-GUI.exe"','FILE1="ServerPayload.zip"'
)
Copy-Item -LiteralPath $setupExe -Destination (Join-Path $serverStage 'UDA-Server-Setup-GUI.exe') -Force
[IO.File]::WriteAllLines($sedPath, $sed, [Text.Encoding]::Default)
Compile-Exe $packagesDir 'UDA-Server-Setup-v5.3.45.exe' 'UDA.ServerSetupProgram' 'winexe' $winManifest $payloadZip
if (-not (Test-Path -LiteralPath $singleSetup)) { throw "IExpress 未能生成单文件安装程序: $singleSetup" }
Remove-Item -LiteralPath $sedPath -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "构建成功 / Build succeeded:" -ForegroundColor Green
Write-Host "  Offline: $offlineDir"
Write-Host "  Lan:     $lanDir"
Write-Host "  Server installer package: $serverZip"
Write-Host "  One-click server installer: $singleSetup"
Write-Host "配置向后兼容：旧 DriverConfig.json 不含第三阶段字段时按默认加载（见 Run-IntegrationTests 的 Config.BackwardCompat）。" -ForegroundColor DarkGray
Write-Host "NIC bootstrap：客户端缺库时安全降级（见 Run-NicBootstrapTests 的 Nic.MissingRepoSafeDowngrade）。" -ForegroundColor DarkGray

