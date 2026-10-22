<#
.SYNOPSIS
  复制万能驱动助手并输出 SCCM 任务序列命令。
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$Destination = "$env:ProgramData\Company\UniversalDriverAssistant",
  [switch]$CreateRunOnce
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'UniversalDriverAssistant.exe'
$config = Join-Path $PSScriptRoot 'DriverConfig.json'
if (-not (Test-Path -LiteralPath $exe)) { throw "缺少文件: $exe" }
if (-not (Test-Path -LiteralPath $config)) { throw "缺少文件: $config" }
if ($PSCmdlet.ShouldProcess($Destination, '复制程序和配置')) {
  New-Item -ItemType Directory -Path $Destination -Force | Out-Null
  Copy-Item -LiteralPath $exe,$config -Destination $Destination -Force
}
$cmd = ('"{0}" --deployment --config "{1}"' -f (Join-Path $Destination 'UniversalDriverAssistant.exe'),(Join-Path $Destination 'DriverConfig.json'))
if ($CreateRunOnce -and $PSCmdlet.ShouldProcess('HKLM RunOnce','注册部署启动项')) {
  New-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce' -Name UniversalDriverAssistant -Value $cmd -PropertyType String -Force | Out-Null
}
Write-Host 'SCCM 运行命令行：' -ForegroundColor Cyan
Write-Host $cmd
Write-Host '成功代码: 0 3010；失败代码: 1 2'