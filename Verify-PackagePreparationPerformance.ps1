[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$GeneratorPath,
  [string]$OutputDirectory = '',
  [ValidateRange(2,20000)][int]$Drivers = 2320,
  [ValidateRange(1,4096)][int]$PayloadKiB = 32,
  [ValidateRange(1,64)][int]$Workers = 8
)
$ErrorActionPreference = 'Stop'
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $scriptDirectory 'release-v5.2.6\tests\package-benchmark' }
$generator = [IO.Path]::GetFullPath($GeneratorPath)
if (-not (Test-Path -LiteralPath $generator -PathType Leaf)) { throw "OfflineRepoGenerator not found: $generator" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$runRoot = Join-Path $output ('run-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $runRoot 'source'
$baseRepo = Join-Path $runRoot 'base-repo'
$serialRepo = Join-Path $runRoot 'serial-repo'
$parallelRepo = Join-Path $runRoot 'parallel-repo'
New-Item -ItemType Directory -Path $source -Force | Out-Null

function Quote-Arg([string]$value) { '"' + $value.Replace('"', '\"') + '"' }
function Invoke-Generator([string[]]$arguments) {
  $psi = New-Object Diagnostics.ProcessStartInfo
  $psi.FileName = $generator
  $psi.Arguments = (($arguments | ForEach-Object { Quote-Arg $_ }) -join ' ')
  $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
  $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
  $process = [Diagnostics.Process]::Start($psi)
  $stdoutTask = $process.StandardOutput.ReadToEndAsync()
  $stderrTask = $process.StandardError.ReadToEndAsync()
  $timer = [Diagnostics.Stopwatch]::StartNew()
  $process.WaitForExit(); $timer.Stop()
  $stdout = $stdoutTask.Result; $stderr = $stderrTask.Result
  $cpu = $process.TotalProcessorTime.TotalSeconds
  $exit = $process.ExitCode; $process.Dispose()
  if ($exit -ne 0) { throw "Generator exited $exit.`n$stdout`n$stderr" }
  [pscustomobject]@{ Seconds=$timer.Elapsed.TotalSeconds; CpuSeconds=$cpu; Output=$stdout }
}

try {
  $rng = New-Object Security.Cryptography.RNGCryptoServiceProvider
  $payload = New-Object byte[] ($PayloadKiB * 1024)
  try {
    for ($i=0; $i -lt $Drivers; $i++) {
      $package = Join-Path $source ('driver-{0:D5}' -f $i)
      New-Item -ItemType Directory -Path $package -Force | Out-Null
      $rng.GetBytes($payload)
      [IO.File]::WriteAllBytes((Join-Path $package 'payload.sys'), $payload)
      $device = $i.ToString('X4')
      $inf = @(
        '[Version]','Signature="$Windows NT$"','Class=Net','ClassGuid={4d36e972-e325-11ce-bfc1-08002be10318}',
        'Provider=%Vendor%','DriverVer=03/14/2026,2.0.0.0','', '[Manufacturer]','%Vendor%=Models,NTamd64.10','',
        '[Models.NTamd64.10]',('%Device%=Install,PCI\VEN_11AA&DEV_' + $device),'', '[Install]','CopyFiles=DriverCopy','',
        '[DriverCopy]','payload.sys','', '[Strings]','Vendor="UDA performance test"',('%Device%="Test device ' + $device + '"')
      ) -join "`r`n"
      [IO.File]::WriteAllText((Join-Path $package 'device.inf'), $inf, (New-Object Text.UTF8Encoding($false)))
    }
  } finally { $rng.Dispose() }

  $create = Invoke-Generator @('--repo',$baseRepo,'--source',$source,'--create')
  Copy-Item -LiteralPath $baseRepo -Destination $serialRepo -Recurse
  Copy-Item -LiteralPath $baseRepo -Destination $parallelRepo -Recurse
  $serial = Invoke-Generator @('--repo',$serialRepo,'--prepare-packages','--package-workers','1')
  $parallel = Invoke-Generator @('--repo',$parallelRepo,'--prepare-packages','--package-workers',$Workers.ToString())

  $speedup = $serial.Seconds / [Math]::Max(0.001, $parallel.Seconds)
  $result = [pscustomobject]@{
    Drivers=$Drivers; PayloadKiB=$PayloadKiB; Workers=$Workers; ProcessorCount=[Environment]::ProcessorCount
    SerialSeconds=[Math]::Round($serial.Seconds,2); SerialCpuSeconds=[Math]::Round($serial.CpuSeconds,2)
    ParallelSeconds=[Math]::Round($parallel.Seconds,2); ParallelCpuSeconds=[Math]::Round($parallel.CpuSeconds,2)
    Speedup=[Math]::Round($speedup,2)
    SerialCpuCoresUsed=[Math]::Round($serial.CpuSeconds / [Math]::Max(0.001,$serial.Seconds),2)
    ParallelCpuCoresUsed=[Math]::Round($parallel.CpuSeconds / [Math]::Max(0.001,$parallel.Seconds),2)
    Created=@{Serial=([regex]::Match($serial.Output,'PACKAGES: READY \(created (\d+)\)').Groups[1].Value); Parallel=([regex]::Match($parallel.Output,'PACKAGES: READY \(created (\d+)\)').Groups[1].Value)}
  }
  $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'latest-result.json') -Encoding UTF8
  $result | Format-List
}
finally {
  $rootPrefix = [IO.Path]::GetFullPath($output).TrimEnd('\') + '\'
  $runFull = [IO.Path]::GetFullPath($runRoot)
  if ($runFull.StartsWith($rootPrefix,[StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $runFull) -match '^run-[0-9a-f]{32}$') {
    Remove-Item -LiteralPath $runFull -Recurse -Force -ErrorAction SilentlyContinue
  }
}

