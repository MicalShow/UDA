<# Run in full Windows as LocalSystem or administrator, not WinPE. No reboot is performed. #>
[CmdletBinding()]
param([string]$ClientExe = (Join-Path $PSScriptRoot 'UDA-Client.exe'), [string]$ServerUrl)
$ErrorActionPreference = 'Stop'
try {
    $clientPath = (Resolve-Path -LiteralPath $ClientExe).Path
    $arguments = '--sccm'
    if ($ServerUrl) {
        $uri = $null
        if (-not [Uri]::TryCreate($ServerUrl,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -notin @('http','https') -or $uri.UserInfo) { throw 'Invalid server URL' }
        $arguments += ' --lan "' + $uri.AbsoluteUri.TrimEnd('/') + '"'
    }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $clientPath; $start.Arguments = $arguments; $start.WorkingDirectory = [IO.Path]::GetDirectoryName($clientPath)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Client process did not start' }
    try { $process.WaitForExit(); exit $process.ExitCode } finally { $process.Dispose() }
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 2 }
