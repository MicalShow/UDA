# Read-only driver failure evidence collection. Does not install/remove drivers or reboot.
[CmdletBinding()]
param(
    [string]$InfName = 'gna.inf',
    [string]$OutputDirectory,
    [string]$SetupApiLog,
    [switch]$SkipSystemInventory,
    [switch]$Audio
)
$ErrorActionPreference = 'Stop'
if ($InfName -notmatch '^[A-Za-z0-9_.-]+\.inf$') { throw 'InfName must be an INF file name, for example gna.inf.' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $reportParent = if ($PSScriptRoot) { $PSScriptRoot } else { [IO.Path]::GetTempPath() }
    $OutputDirectory = Join-Path $reportParent ('UDA-Driver-Diagnostics-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6))
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory already exists; choose a new report directory.' }
[void][IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory))
function Save-Evidence([string]$Name, [scriptblock]$Read) {
    try { & $Read | Out-File -LiteralPath (Join-Path $OutputDirectory $Name) -Encoding utf8 -Width 4096 }
    catch { $_.Exception.Message | Out-File -LiteralPath (Join-Path $OutputDirectory ($Name + '.error.txt')) -Encoding utf8 }
}
if ([string]::IsNullOrWhiteSpace($SetupApiLog)) { $SetupApiLog = Join-Path $env:SystemRoot 'INF\setupapi.dev.log' }
Save-Evidence 'setupapi-driver-context.txt' {
    $pattern = if ($Audio) { 'HDXSST|Realtek|IntcAudio|intcoed|csaudio|CSC3551|103C8971|AudioEndpoint|Fortemedia|Sonitude' } else { [regex]::Escape($InfName) }
    Select-String -LiteralPath $SetupApiLog -Pattern $pattern -Context 25,150 | Select-Object -Last 40 | ForEach-Object { $_.ToString() }
}
Save-Evidence 'setupapi-recent-tail.txt' { Get-Content -LiteralPath $SetupApiLog -Tail 5000 }
Save-Evidence 'report.json' { @{ capturedUtc=[DateTime]::UtcNow.ToString('o'); computer=$env:COMPUTERNAME; inf=$(if ($Audio) { 'audio-components' } else { $InfName }); audioMode=[bool]$Audio; setupApiLog=$SetupApiLog; os=[Environment]::OSVersion.VersionString; is64Bit=[Environment]::Is64BitOperatingSystem; readOnly=$true } | ConvertTo-Json }
if (-not $SkipSystemInventory) {
    $devicePattern = if ($Audio) { 'Audio|Sound|Realtek|Cirrus|CSC3551|INTELAUDIO|HDAUDIO|Fortemedia|Sonitude|声|扬声器|麦克风' } else { 'GNA|Gaussian|Neural|Base System|基本系统' }
    Save-Evidence 'devices.json' {
        @(Get-CimInstance Win32_PnPEntity -OperationTimeoutSec 15 | Where-Object { $_.ConfigManagerErrorCode -ne 0 -or $_.Name -match $devicePattern -or $_.PNPDeviceID -match $devicePattern } | Select-Object Name,PNPDeviceID,HardwareID,CompatibleID,ConfigManagerErrorCode,Status,Service) | ConvertTo-Json -Depth 6
    }
    Save-Evidence 'signed-drivers.json' {
        @(Get-CimInstance Win32_PnPSignedDriver -OperationTimeoutSec 15 | Where-Object { $_.DeviceName -match $devicePattern -or $_.DeviceID -match $devicePattern -or ($Audio -and $_.DeviceClass -match 'MEDIA|Extension|SoftwareComponent|AudioEndpoint') } | Select-Object DeviceName,DeviceID,DeviceClass,InfName,DriverVersion,DriverProviderName,IsSigned) | ConvertTo-Json -Depth 5
    }
    if ($Audio) {
        Save-Evidence 'computer-model.json' { Get-CimInstance Win32_ComputerSystem -OperationTimeoutSec 15 | Select-Object Manufacturer,Model,SystemSKUNumber | ConvertTo-Json }
        Save-Evidence 'windows.json' { Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' | Select-Object ProductName,DisplayVersion,CurrentBuild,UBR | ConvertTo-Json }
        Save-Evidence 'audio-services.json' { Get-Service Audiosrv,AudioEndpointBuilder | Select-Object Name,Status,StartType | ConvertTo-Json }
        Save-Evidence 'audio-endpoints.json' { Get-PnpDevice -Class AudioEndpoint | Select-Object FriendlyName,InstanceId,Status,Problem | ConvertTo-Json -Depth 4 }
        Save-Evidence 'audio-applied-extensions.json' {
            @(Get-PnpDevice -PresentOnly | Where-Object { $_.Class -eq 'MEDIA' -or $_.FriendlyName -match $devicePattern -or $_.InstanceId -match $devicePattern } | ForEach-Object {
                $extensionProperty = Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_ExtendedConfigurationIds' -ErrorAction SilentlyContinue
                [pscustomobject]@{ InstanceId=$_.InstanceId; FriendlyName=$_.FriendlyName; AppliedExtensions=@($extensionProperty.Data) }
            }) | ConvertTo-Json -Depth 5
        }
        Save-Evidence 'audio-package-infs.txt' {
            Get-ChildItem -LiteralPath (Join-Path $env:SystemRoot 'INF') -Filter 'oem*.inf' | ForEach-Object {
                $body = Get-Content -LiteralPath $_.FullName -Raw
                if ($body -match '(?im)^\s*Class\s*=\s*(MEDIA|Extension|SoftwareComponent)\s*(;.*)?$' -and $body -match 'Realtek|Cirrus|CSC3551|103C8971|Intel.*(Audio|Sound)|Fortemedia|Sonitude') {
                    '===== ' + $_.Name + ' ====='; $body
                }
            }
        }
    }
    Save-Evidence 'driver-store.txt' { & "$env:SystemRoot\System32\pnputil.exe" /enum-drivers }
    Save-Evidence 'reboot-status.json' {
        $pendingRename = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue).PendingFileRenameOperations
        @{ componentServicingReboot=(Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'); windowsUpdateReboot=(Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'); pendingFileRename=([bool]$pendingRename) } | ConvertTo-Json
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archivePath = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\') + '.zip'
[IO.Compression.ZipFile]::CreateFromDirectory([IO.Path]::GetFullPath($OutputDirectory), $archivePath)
Write-Output ('Read-only driver diagnostic report: ' + $archivePath)
