param([string]$OutputDirectory = 'release-v5.1.0')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Web.Extensions
Add-Type -AssemblyName System.Windows.Forms
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory + '\Lan\LanDriverService.exe')))
$json = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$root = Join-Path $PSScriptRoot ($OutputDirectory + '\admin-test-' + [Guid]::NewGuid().ToString('N'))
$id = 'PCI\VEN_1234&DEV_5678'
$source = Join-Path $root 'source'
foreach ($fixture in @(@('old','1.0','NTamd64.6.1'),@('new','3.0','NTamd64.10.0'),@('eleven','4.0','NTamd64.10.0...22000'),@('future','5.0','NTamd64.10.0...30000'),@('x86','9.0','NTx86.10.0'),@('other','6.0','NTamd64.10.0'))) {
    $dir = Join-Path $source $fixture[0]; [void][IO.Directory]::CreateDirectory($dir)
    $deviceId = if ($fixture[0] -eq 'other') { 'PCI\VEN_1234&DEV_8888' } else { $id }
    $inf = @"
[Version]
Signature="`$Windows NT`$"
Class=Net
ClassGuid={4d36e972-e325-11ce-bfc1-08002be10318}
Provider=Test
DriverVer=10/01/2026,$($fixture[1])
[Manufacturer]
Test=Models,$($fixture[2])
[Models.$($fixture[2])]
Device=$($fixture[0]),$deviceId
[$($fixture[0])]
CopyFiles=Files
[Files]
driver.sys
"@
    [IO.File]::WriteAllText((Join-Path $dir 'driver.inf'),$inf)
    [IO.File]::WriteAllText((Join-Path $dir 'driver.sys'),'synthetic fixture')
}
$cfg = New-Object UDA.LanServerConfig
$cfg.RepoRoot = Join-Path $root 'repo'; $cfg.Host = 'localhost'; $cfg.Token = 'admin-improvement-test'
$tcp = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0); $tcp.Start(); $cfg.Port=$tcp.LocalEndpoint.Port; $tcp.Stop()
$logger = New-Object UDA.L((Join-Path $root 'logs'))
$generator = New-Object UDA.OfflineRepoGenerator((New-Object UDA.TrustingSignatureVerifier),$logger)
$target=New-Object UDA.OsTarget
[void]$generator.Generate($cfg.RepoRoot,[string[]]@($source),$target,$null,[DateTime]::UtcNow.ToString('o'),[Threading.CancellationToken]::None,$null)
$server = New-Object UDA.LanDriverServer($cfg,$logger)
$url='http://localhost:'+$cfg.Port
function Req($path,$body=$null,$authorized=$true) {
    $r=[Net.HttpWebRequest]::Create($url+$path);$r.Proxy=$null;$r.Timeout=10000;$r.Headers['X-UDA-Protocol']='1'
    if($authorized){$r.Headers['X-UDA-Token']=$cfg.Token}
    if($null-ne $body){$r.Method='POST';$bytes=[Text.Encoding]::UTF8.GetBytes($body);$r.ContentLength=$bytes.Length;$stream=$r.GetRequestStream();$stream.Write($bytes,0,$bytes.Length);$stream.Dispose()}
    try{$response=$r.GetResponse()}catch [Net.WebException]{if(-not $_.Exception.Response){throw};$response=$_.Exception.Response}
    try{$reader=New-Object IO.StreamReader($response.GetResponseStream());$text=$reader.ReadToEnd();return @{Status=[int]$response.StatusCode;Body=$text}}finally{$response.Dispose()}
}
function Obj($path){$response=Req $path;if($response.Status-ne 200){throw $response.Body};return $json.DeserializeObject($response.Body)}
function Check($ok,$name){if(-not $ok){throw $name};Write-Output ('PASS: '+$name)}
try {
    $server.Start()
    for($i=0;$i-lt 100;$i++){if((Obj '/api/health').RepoLoaded){break};Start-Sleep -Milliseconds 100}
    $entries=[UDA.OfflineRepoGenerator]::TryLoadManifest($cfg.RepoRoot).Entries
    $old=$entries|Where-Object DriverVerVersion -eq '1.0';$new=$entries|Where-Object DriverVerVersion -eq '3.0';$eleven=$entries|Where-Object DriverVerVersion -eq '4.0'
    $candidatePath='/api/admin/policy-candidates?id='+[Uri]::EscapeDataString($old.RelativePath)+'&hardwareId='+[Uri]::EscapeDataString($id)+'&arch=amd64&os='
    Check ((Req ($candidatePath+'windows10') $null $false).Status-eq 401) 'policy candidates require admin authentication'
    $ten=Obj ($candidatePath+'windows10');$win11=Obj ($candidatePath+'windows11');$seven=Obj ($candidatePath+'windows7')
    Check ($ten.items.Count-eq 2 -and $ten.items[0].version-eq '3.0') 'Windows 10 picker excludes Windows 11 and x86 packages'
    Check ($win11.items.Count-eq 4 -and $win11.items[0].version-eq '5.0') 'Windows 11 picker includes repository versions and shows build-specific choices'
    Check ($seven.items.Count-eq 1 -and $seven.items[0].version-eq '1.0') 'Windows 7 picker excludes Windows 10 packages'
    $report=@{Protocol=1;RequestId='same-request';OsArchitecture='amd64';OsMajorVersion=10;OsBuildNumber=22621;Devices=@(@{DeviceInstanceId='TEST';HardwareIds=@($id);CompatibleIds=@();Class='Net';FriendlyName='Test adapter';Bad=$true})}
    function Match { $r=Req '/api/report' ($json.Serialize($report));if($r.Status-ne 200){throw $r.Body};return $json.DeserializeObject($r.Body) }
    Check ((Match).Candidates[0].DriverVerVersion-eq '4.0') 'unconfigured client gets newest version for actual build, excluding future build'
    $policy=$win11.scope;$policy.StableHash=$old.ContentHash
    Check ((Req '/api/admin/release-policies' ($json.Serialize($policy))).Status-eq 200) 'stable picker saves without manual hashes'
    Check ((Match).Candidates[0].DriverVerVersion-eq '1.0') 'stable-only policy independently selects configured stable version'
    $policy.LatestHash=$new.ContentHash
    Check ((Req '/api/admin/release-policies' ($json.Serialize($policy))).Status-eq 200) 'complete policy saves'
    Check ((Match).Candidates[0].DriverVerVersion-eq '1.0') 'complete policy prefers stable and invalidates repeated-request cache'
    $report.Devices[0].HardwareIds=@('PCI\VEN_1234&DEV_8888');$report.RequestId='unconfigured-device'
    Check ((Match).Candidates[0].DriverVerVersion-eq '6.0') 'unconfigured driver independently keeps automatic newest selection'
    $report.Devices[0].HardwareIds=@($id);$report.RequestId='same-request'
    $policy.StableHash=''
    [void](Req '/api/admin/release-policies' ($json.Serialize($policy)))
    Check ((Match).Candidates[0].DriverVerVersion-eq '3.0') 'latest-only policy independently selects configured latest version'
    $policy.StableHash=$old.ContentHash;$policy.LatestHash=($entries|Where-Object DriverVerVersion -eq '5.0').ContentHash
    [void](Req '/api/admin/release-policies' ($json.Serialize($policy)))
    Check ((Match).Candidates[0].DriverVerVersion-eq '1.0') 'valid stable remains effective when configured latest needs a future build'
    $policy.StableHash=$policy.LatestHash;$policy.LatestHash=$new.ContentHash
    [void](Req '/api/admin/release-policies' ($json.Serialize($policy)))
    Check ((Match).Candidates[0].DriverVerVersion-eq '3.0') 'valid latest remains effective when configured stable is incompatible'
    $policy.LatestHash=''
    [void](Req '/api/admin/release-policies' ($json.Serialize($policy)))
    Check ((Match).Candidates[0].DriverVerVersion-eq '4.0') 'only configured but incompatible version falls back to newest applicable'
    $policy.StableHash=''
    [void](Req '/api/admin/release-policies' ($json.Serialize($policy)))
    Check ((Match).Candidates[0].DriverVerVersion-eq '4.0') 'clearing both restores automatic newest applicable version'
    $report.OsBuildNumber=19045
    $report.RequestId='different-os-scan'
    Check ((Match).Candidates[0].DriverVerVersion-eq '3.0') 'unconfigured different OS independently selects its newest applicable driver'
    $report.OsBuildNumber=22621;$report.RequestId='same-request'
    $bad=$ten.scope;$bad.StableHash=$eleven.ContentHash
    Check ((Req '/api/admin/release-policies' ($json.Serialize($bad))).Status-eq 400) 'server rejects cross-OS policy injection'
    $bad.StableHash=($entries|Where-Object DriverVerVersion -eq '9.0').ContentHash
    Check ((Req '/api/admin/release-policies' ($json.Serialize($bad))).Status-eq 400) 'server rejects cross-architecture policy injection'
    $eventDir=Join-Path $cfg.RepoRoot 'install-events';[void][IO.Directory]::CreateDirectory($eventDir)
    $dates=@('2026-10-14T15:59:59Z','2026-10-14T16:00:00Z','2026-10-15T15:59:59Z','2026-10-15T16:00:00Z')
    for($i=0;$i-lt $dates.Count;$i++){
        $event=@{EventId='test-'+$i;Machine='PC-'+$i;Device='D-'+$i;Utc=$dates[$i];Status='installed';Update=($i-eq 2)}
        [IO.File]::WriteAllText((Join-Path $eventDir ($i.ToString()+'.json')),$json.Serialize($event))
    }
    $logs=Obj '/api/admin/install-logs?from=2026-10-15T00%3A00%3A00%2B08%3A00&to=2026-10-16T00%3A00%3A00%2B08%3A00'
    Check ($logs.total-eq 2 -and $logs.items[0].Machine-eq 'PC-2') 'date range includes local start and final second, excludes next midnight'
    Check ($logs.machines-eq 4 -and $logs.hardware-eq 4 -and $logs.updates-eq 1) 'top usage totals remain global under date filtering'
    Check ((Req '/api/admin/install-logs?from=bad').Status-eq 400) 'invalid date rejected'
    Check ((Req '/api/admin/install-logs?from=2026-10-17&to=2026-10-16').Status-eq 400) 'reversed date range rejected'
    $page=Req '/admin';[IO.File]::WriteAllText((Join-Path $root 'web-console.html'),$page.Body,[Text.Encoding]::UTF8)
    $command='"C:\Program Files\UDA\service.exe" --service --repo "D:\驱动仓库\Repository" --port 9876 --token "never print"'
    Check ([UDA.ServerSetupDiscovery]::ReadArgument($command,'--repo')-eq 'D:\驱动仓库\Repository') 'upgrade reads quoted Unicode repository path'
    Check ([UDA.ServerSetupDiscovery]::ReadArgument($command,'--port')-eq '9876') 'upgrade reads existing service port'
    Check ([UDA.ServerSetupDiscovery]::ReadArgument($command,'--source')-eq '') 'missing service argument remains empty'
    foreach($address in [UDA.ServerSetupDiscovery]::NetworkAddresses()) { $ip=[Net.IPAddress]::Parse($address.Split(' ')[0]);Check ($ip.AddressFamily-eq [Net.Sockets.AddressFamily]::InterNetwork -and -not [Net.IPAddress]::IsLoopback($ip)) 'installer lists usable IPv4 addresses' }
    $type=[UDA.ServerSetupDiscovery].Assembly.GetType('UDA.ServerSetupForm')
    $form=[Activator]::CreateInstance($type,$true)
    try {
        $flags=[Reflection.BindingFlags]'Instance,NonPublic'
        $combo=$type.GetField('_networkAddress',$flags).GetValue($form)
        $port=$type.GetField('_port',$flags).GetValue($form)
        $urlBox=$type.GetField('_publicUrl',$flags).GetValue($form)
        if($combo.Items.Count-gt 1){$combo.SelectedIndex=1;$port.Value=9876;Check ($urlBox.Text.EndsWith(':9876')) 'NIC selection follows installer port';$urlBox.Text='http://example.invalid:1234';Check ($combo.SelectedIndex-eq 0) 'manual hostname switches to custom address'}
        $repoBox=$type.GetField('_repo',$flags).GetValue($form)
        Check ($repoBox.Text-eq [UDA.ServerSetupDiscovery]::ReadArgument([UDA.ServerSetupDiscovery]::ExistingServiceCommand(),'--repo')) 'installer prefills installed repository without changing service'
        $form.Show();[Windows.Forms.Application]::DoEvents()
        $bitmap=New-Object Drawing.Bitmap $form.Width,$form.Height
        try{$form.DrawToBitmap($bitmap,$form.ClientRectangle);$bitmap.Save((Join-Path $root 'installer-preview.png'))}finally{$bitmap.Dispose()}
    } finally {$form.Dispose()}
    Write-Output ('ARTIFACTS: '+$root)
} finally {$server.Dispose();$logger.Dispose()}
