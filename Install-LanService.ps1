<#
.SYNOPSIS
  安装/卸载 LAN 驱动服务（Windows Server）。
.DESCRIPTION
  将 LanDriverService.exe 注册为 Windows 服务并可选地开放防火墙端口。
  安全默认：不传 -Apply 时只打印将要执行的命令（dry-run），不会真正注册服务或开放防火墙，
  因此可在测试中安全运行。需要真正部署时加 -Apply。
.PARAMETER Apply
  真正执行注册/卸载/防火墙操作。默认 dry-run。
.PARAMETER Uninstall
  卸载服务并移除防火墙规则（与 -Apply 配合使用）。
.PARAMETER Port
  服务监听端口（默认 8731）。
.PARAMETER RepoDir
  驱动仓库目录。
.PARAMETER ServiceName
  服务名称（默认 UdaLanDriverService）。
.PARAMETER Token
  可选 LAN API token。
#>
[CmdletBinding()]
param(
  [switch]$Apply,
  [switch]$Uninstall,
  [int]$Port = 8731,
  [string]$BindAddress = '+',
  [string]$RepoDir,
  [string]$ServiceName = 'UdaLanDriverService',
  [string]$Token,
  [string]$AdminCredentials,
  [string]$ClientTemplate,
  [string]$InstallDir
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($InstallDir)) { $InstallDir = $scriptRoot }
$exe = Join-Path $InstallDir 'LanDriverService.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "未找到 LanDriverService.exe: $exe" }

function Invoke-Step {
  param([string]$Label, [scriptblock]$Action, [string]$DryRunText)
  if ($Apply) {
    Write-Host "[APPLY] $Label" -ForegroundColor Cyan
    & $Action
  } else {
    Write-Host "[DRY-RUN] $Label" -ForegroundColor Yellow
    Write-Host "  $DryRunText" -ForegroundColor DarkGray
  }
}

if ($Uninstall) {
  Invoke-Step -Label '停止服务 / Stop service' -DryRunText "sc.exe stop $ServiceName" -Action { try { & sc.exe stop $ServiceName | Out-Null } catch { } }
  Invoke-Step -Label '删除服务 / Delete service' -DryRunText "sc.exe delete $ServiceName" -Action { & sc.exe delete $ServiceName | Out-Null }
  # Deleting the service also removes its failure-recovery configuration; the firewall rule is the
  # only machine-level leftover that must be cleaned explicitly.
  Invoke-Step -Label '移除防火墙规则 / Remove firewall rule' -DryRunText "netsh advfirewall firewall delete rule name=`"$ServiceName`"" -Action { try { & netsh advfirewall firewall delete rule name="$ServiceName" | Out-Null } catch { } }
  Write-Host "卸载完成（Apply=$Apply）" -ForegroundColor Green
  return
}

# Preserve every unspecified argument and the existing service identity on upgrades.
$previousCommand = ''
try { $previousCommand = [string](Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$ServiceName) -Name ImagePath -ErrorAction Stop).ImagePath } catch { }
if ($previousCommand) {
  [void][Reflection.Assembly]::LoadFrom($exe)
  if (-not $PSBoundParameters.ContainsKey('RepoDir')) { $RepoDir = [UDA.ServerSetupDiscovery]::ReadArgument($previousCommand,'--repo') }
  if (-not $PSBoundParameters.ContainsKey('Port')) { $existingPort=[UDA.ServerSetupDiscovery]::ReadArgument($previousCommand,'--port');if($existingPort){$Port=[int]$existingPort} }
}
# Build the service binPath. LanDriverService supports --service to run as a ServiceBase.
$binArgs = "--service --port $Port --bind `"$BindAddress`""
if (-not [string]::IsNullOrWhiteSpace($RepoDir)) { $binArgs += " --repo `"$RepoDir`"" }
if (-not [string]::IsNullOrWhiteSpace($Token)) { $binArgs += " --token `"$Token`"" }
if (-not [string]::IsNullOrWhiteSpace($AdminCredentials)) { $binArgs += " --admin-credentials `"$AdminCredentials`"" }
if ([string]::IsNullOrWhiteSpace($ClientTemplate)) { $ClientTemplate = Join-Path $InstallDir 'ClientTemplate' }
if (Test-Path -LiteralPath $ClientTemplate) { $binArgs += " --client-template `"$ClientTemplate`"" }
$binPath = "`"$exe`" $binArgs"

Invoke-Step -Label '验证原数据已迁移 MySQL / Require migrated database before stopping service' -DryRunText 'Verify enabled MySQL dataset, integrity and transaction permissions; reject file-backed deployment' -Action {
  if ([string]::IsNullOrWhiteSpace($RepoDir)) { throw '5.3.0 requires an existing MySQL-migrated repository' }
  [void][Reflection.Assembly]::LoadFrom($exe)
  [UDA.ServerStorage]::UseProviderDirectory((Join-Path $InstallDir 'DatabaseProvider'))
  if ($previousCommand -and -not [string]::Equals([IO.Path]::GetFullPath([UDA.ServerSetupDiscovery]::ReadArgument($previousCommand,'--repo')).TrimEnd('\'),[IO.Path]::GetFullPath($RepoDir).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'Upgrade must use the existing migrated repository' }
  [UDA.ServerStorage]::RequireMigratedDatabase($RepoDir)
}

$upgradeBackup = $null
if ($previousCommand) {
  $binPath = [UDA.ServerUpgrade]::UpdatedCommand($previousCommand,$exe,$RepoDir,$Port,$ClientTemplate)
  foreach($entry in @(@('Token','--token',$Token),@('AdminCredentials','--admin-credentials',$AdminCredentials),@('BindAddress','--bind',$BindAddress))) {
    if($PSBoundParameters.ContainsKey($entry[0])) {
      $pattern='(?:^|\s)'+[regex]::Escape($entry[1])+'\s+(?:"[^"]*"|\S+)'
      $replacement=' '+$entry[1]+' "'+([string]$entry[2]).Replace('"','')+'"'
      if([regex]::IsMatch($binPath,$pattern)){$binPath=[regex]::Replace($binPath,$pattern,[Text.RegularExpressions.MatchEvaluator]{param($m)$replacement})}else{$binPath+=$replacement}
    }
  }
  Invoke-Step -Label '停止旧服务并保留运行身份 / Stop existing service; preserve identity' -DryRunText "sc.exe stop $ServiceName (wait for STOPPED and original process exit; no delete)" -Action {
    [UDA.ServerUpgrade]::StopServiceForUpdate($ServiceName,[UDA.ServerUpgrade]::ServiceExecutable($previousCommand),(Split-Path ([UDA.ServerUpgrade]::ServiceExecutable($previousCommand))),[TimeSpan]::FromSeconds(120),$null,$false)
    $data=Join-Path $env:ProgramData 'UniversalDriverAssistant'
    $script:upgradeBackup=[UDA.ServerUpgrade]::Backup((Split-Path ([UDA.ServerUpgrade]::ServiceExecutable($previousCommand))),$data,$RepoDir,$previousCommand)
  }
}
try {
Invoke-Step -Label '校验压缩包、业务数据入库与清理散装副本 / Verify archives and compact SQL-backed repository' -DryRunText 'Verify ZIP members and hashes, migrate remaining metadata/index, then remove redundant repository copies' -Action {
  [void][UDA.DatabaseArchiveRepository]::Compact($RepoDir)
}
$registration = if($previousCommand){'config'}else{'create'}
Invoke-Step -Label '注册/更新服务 / Register or configure service' -DryRunText "sc.exe $registration $ServiceName binPath=<configured arguments; Token redacted> start= auto" -Action {
  & sc.exe $registration $ServiceName binPath= $binPath start= auto | Out-Null
  if($LASTEXITCODE-ne 0){throw 'Service configuration failed'}
  & sc.exe description $ServiceName 'Universal Driver Assistant - LAN driver service (self-hosted)' | Out-Null
}
# Failure recovery: 1st and 2nd failure restart the service after 5 seconds, subsequent failures also
# restart after 5 seconds; the failure counter resets after 24h so a transient fault can never leave
# the service permanently stopped and waiting for a manual PowerShell fix.
Invoke-Step -Label '配置失败自动恢复 / Configure failure recovery (restart after 5s, reset 24h)' -DryRunText "sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000" -Action {
  & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "sc.exe failure failed with exit code $LASTEXITCODE" }
  & sc.exe failureflag $ServiceName 1 | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "sc.exe failureflag failed with exit code $LASTEXITCODE" }
}
Invoke-Step -Label '开放防火墙端口 / Open firewall (TCP)' -DryRunText "netsh advfirewall firewall add rule name=`"$ServiceName`" dir=in action=allow protocol=TCP localport=$Port" -Action {
  & netsh advfirewall firewall add rule name="$ServiceName" dir=in action=allow protocol=TCP localport=$Port | Out-Null
}
Invoke-Step -Label '启动服务 / Start service' -DryRunText "sc.exe start $ServiceName" -Action { & sc.exe start $ServiceName | Out-Null; if($LASTEXITCODE-ne 0){throw 'Service start failed'} }

if (-not $Apply) {
  Write-Host ""
  Write-Host "DRY-RUN 完成：未注册服务、未开放防火墙。加 -Apply 真正部署。" -ForegroundColor Green
} else {
  Write-Host "部署完成。健康检查: http://localhost:$Port/api/health" -ForegroundColor Green
}

} catch {
  if($Apply -and $previousCommand -and $upgradeBackup){
    $previousExe=[UDA.ServerUpgrade]::ServiceExecutable($previousCommand)
    $previousVersion=if(Test-Path -LiteralPath $previousExe){[Diagnostics.FileVersionInfo]::GetVersionInfo($previousExe)}else{$null}
    if($null-eq $previousVersion -or $previousVersion.FileMajorPart-lt 5 -or ($previousVersion.FileMajorPart-eq 5 -and $previousVersion.FileMinorPart-lt 3)){[UDA.ServerStorage]::RestoreFileCompatibility($RepoDir)}
    & sc.exe config $ServiceName binPath= $previousCommand | Out-Null
    & sc.exe start $ServiceName | Out-Null
    Write-Host ('更新失败，已尝试恢复旧服务参数；备份：'+$upgradeBackup)
  }
  throw
}
