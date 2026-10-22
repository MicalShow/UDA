param([string]$OutputDirectory='release-v5.0.7', [string]$EvidenceDirectory='analysis-win7')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Web.Extensions
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory+'\Lan\LanDriverService.exe')))
$json=New-Object System.Web.Script.Serialization.JavaScriptSerializer
$root=Join-Path $PSScriptRoot ($OutputDirectory+'\win7-bluetooth-'+[Guid]::NewGuid().ToString('N'))
$source=Join-Path $root 'source'
foreach($name in @('ibtusb.inf','ibtpci.inf')){
    $evidence=Get-ChildItem (Join-Path $PSScriptRoot $EvidenceDirectory) -Filter ('*-'+$name) | Select-Object -First 1
    if(-not $evidence){throw ('Missing actual INF evidence: '+$name)}
    $directory=Join-Path $source $name;[void][IO.Directory]::CreateDirectory($directory)
    Copy-Item -LiteralPath $evidence.FullName -Destination (Join-Path $directory $name)
}
$cfg=New-Object UDA.LanServerConfig
$cfg.RepoRoot=Join-Path $root 'repo';$cfg.Host='localhost';$cfg.Token='win7-fixture-token'
$tcp=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0);$tcp.Start();$cfg.Port=$tcp.LocalEndpoint.Port;$tcp.Stop()
$logger=New-Object UDA.L((Join-Path $root 'logs'))
$generator=New-Object UDA.OfflineRepoGenerator((New-Object UDA.TrustingSignatureVerifier),$logger)
[void]$generator.Generate($cfg.RepoRoot,[string[]]@($source),(New-Object UDA.OsTarget),$null,[DateTime]::UtcNow.ToString('o'),[Threading.CancellationToken]::None,$null)
$server=New-Object UDA.LanDriverServer($cfg,$logger)
function Request($path,$body=$null){
    $request=[Net.HttpWebRequest]::Create('http://localhost:'+$cfg.Port+$path);$request.Proxy=$null;$request.Timeout=10000
    $request.Headers['X-UDA-Protocol']='1';$request.Headers['X-UDA-Token']=$cfg.Token
    if($null-ne $body){$request.Method='POST';$bytes=[Text.Encoding]::UTF8.GetBytes($body);$request.ContentLength=$bytes.Length;$stream=$request.GetRequestStream();$stream.Write($bytes,0,$bytes.Length);$stream.Dispose()}
    try{$response=$request.GetResponse()}catch [Net.WebException]{if(-not $_.Exception.Response){throw};$response=$_.Exception.Response}
    try{$reader=New-Object IO.StreamReader($response.GetResponseStream());return @{Status=[int]$response.StatusCode;Body=$reader.ReadToEnd()}}finally{$response.Dispose()}
}
function ObjectResponse($path){$r=Request $path;if($r.Status-ne 200){throw $r.Body};return $json.DeserializeObject($r.Body)}
function Check($condition,$name){if(-not $condition){throw $name};Write-Output ('PASS: '+$name)}
try{
    $server.Start()
    for($i=0;$i-lt 100;$i++){if((ObjectResponse '/api/health').RepoLoaded){break};Start-Sleep -Milliseconds 100}
    $repo=$server.GetType().GetField('_repo',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($server)
    foreach($entry in $repo.Manifest.Entries){
        $name=[IO.Path]::GetFileName($entry.RelativePath);$id=$entry.HardwareIds[0]
        $base='/api/admin/policy-candidates?id='+[Uri]::EscapeDataString($entry.RelativePath)+'&hardwareId='+[Uri]::EscapeDataString($id)+'&arch=amd64&os='
        foreach($os in @('windows7','windows8','windows81')){Check ((ObjectResponse ($base+$os)).items.Count-eq 0) ($name+' has no selectable '+$os+' driver')}
        Check ((ObjectResponse ($base+'windows10')).items.Count-gt 0) ($name+' remains available for applicable Windows10')
        Check ((ObjectResponse ($base+'windows11')).items.Count-gt 0) ($name+' remains available for applicable Windows11')
        $body=@{Protocol=1;RequestId=[Guid]::NewGuid().ToString('N');OsArchitecture='amd64';OsMajorVersion=6;OsBuildNumber=7601;Devices=@(@{DeviceInstanceId='FIXTURE';HardwareIds=@($id);Bad=$true;Class='Bluetooth'})}
        $report=$json.DeserializeObject((Request '/api/report' ($json.Serialize($body))).Body)
        Check ($report.Candidates.Count-eq 0) ($name+' client scan rejects Windows7')
        $body.RequestId=[Guid]::NewGuid().ToString('N');$body.OsMajorVersion=10;$body.OsBuildNumber=15063
        $report=$json.DeserializeObject((Request '/api/report' ($json.Serialize($body))).Body)
        Check ($report.Candidates.Count-eq 0) ($name+' client scan rejects Windows10 below 16299')
        $body.RequestId=[Guid]::NewGuid().ToString('N');$body.OsBuildNumber=16299
        $report=$json.DeserializeObject((Request '/api/report' ($json.Serialize($body))).Body)
        Check ($report.Candidates.Count-gt 0) ($name+' client scan accepts minimum build 16299')
        # Simulate stale metadata that claims Windows7 while disk bytes still require Windows10.
        $savedDecorations=@($entry.Decorations);$savedModels=@($entry.Models|ForEach-Object Decoration)
        $entry.Decorations.Clear();$entry.Decorations.Add('NTamd64.6.1')
        foreach($model in $entry.Models){$model.Decoration='NTamd64.6.1'}
        Check ((ObjectResponse ($base+'windows7')).items.Count-eq 0) ($name+' actual INF overrides stale Windows7 metadata')
        $policy=[UDA.DriverReleasePolicies]::Scope('windows7','amd64',$id,$entry.Class);$policy.StableHash=$entry.ContentHash
        Check ((Request '/api/admin/release-policies' ($json.Serialize($policy))).Status-eq 400) ($name+' save rejects forged Windows7 policy even with stale metadata')
        $entry.Decorations.Clear();foreach($d in $savedDecorations){$entry.Decorations.Add($d)}
        for($i=0;$i-lt $savedModels.Count;$i++){$entry.Models[$i].Decoration=$savedModels[$i]}
        $path=Join-Path $cfg.RepoRoot $entry.RelativePath;$bytes=[IO.File]::ReadAllBytes($path)
        try{
            [IO.File]::AppendAllText($path,"`r`n; tampered fixture")
            Check ((Request ($base+'windows10')).Status-eq 400) ($name+' refuses changed INF bytes instead of trusting stale hash')
        }finally{[IO.File]::WriteAllBytes($path,$bytes)}
    }
    Check ((Request (($base+'windows7').Replace('arch=amd64','arch=arm64'))).Status-eq 400) 'Windows7 ARM64 scope explicitly rejected'
    Write-Output ('ARTIFACTS: '+$root)
}finally{$server.Dispose();$logger.Dispose()}
