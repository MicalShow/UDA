param([string]$OutputDirectory='release-v5.1.0')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Web.Extensions
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory+'\Lan\LanDriverService.exe')))
$root=Join-Path $PSScriptRoot ($OutputDirectory+'\db-api-test-'+[Guid]::NewGuid().ToString('N'))
$source=Join-Path $root 'source';[void][IO.Directory]::CreateDirectory($source)
Copy-Item (Join-Path $PSScriptRoot 'test-fixtures\organized-v1\*') $source
$cfg=New-Object UDA.LanServerConfig;$cfg.RepoRoot=Join-Path $root 'repo';$cfg.Host='localhost';$cfg.Token='api-test-token'
$tcp=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0);$tcp.Start();$cfg.Port=$tcp.LocalEndpoint.Port;$tcp.Stop()
$logger=New-Object UDA.L((Join-Path $root 'logs'))
$gen=New-Object UDA.OfflineRepoGenerator((New-Object UDA.TrustingSignatureVerifier),$logger)
[void]$gen.Generate($cfg.RepoRoot,[string[]]@($source),(New-Object UDA.OsTarget),$null,'test',[Threading.CancellationToken]::None,$null)
$server=New-Object UDA.LanDriverServer($cfg,$logger)
$json=New-Object System.Web.Script.Serialization.JavaScriptSerializer
$instance=Get-Content (Join-Path $PSScriptRoot ($OutputDirectory+'\mysql-test\instance.json')) -Raw|ConvertFrom-Json
$connection='Server=127.0.0.1;Port='+$instance.port+';Database=uda_test;User ID=root;SslMode=None;Connection Timeout=2'
function Request($path,$body=$null,$auth=$true,$proto=$true){
 $r=[Net.HttpWebRequest]::Create('http://localhost:'+$cfg.Port+$path);$r.Proxy=$null;$r.Timeout=30000
 if($auth){$r.Headers['X-UDA-Token']=$cfg.Token};if($proto){$r.Headers['X-UDA-Protocol']='1'}
 if($null-ne $body){$r.Method='POST';$b=[Text.Encoding]::UTF8.GetBytes($body);$r.ContentLength=$b.Length;$s=$r.GetRequestStream();$s.Write($b,0,$b.Length);$s.Dispose()}
 try{$response=$r.GetResponse()}catch [Net.WebException]{if(-not $_.Exception.Response){throw};$response=$_.Exception.Response}
 try{$reader=New-Object IO.StreamReader($response.GetResponseStream());return @{Status=[int]$response.StatusCode;Body=$reader.ReadToEnd()}}finally{$response.Dispose()}
}
function Check($ok,$message){if(-not $ok){throw $message};Write-Output ('PASS: '+$message)}
function Operation($action){return Request '/api/admin/database' ($json.Serialize(@{action=$action;connection=$connection}))}
try{
 $server.Start()
 for($i=0;$i-lt 100;$i++){try{$h=$json.DeserializeObject((Request '/api/health').Body);if($h.RepoLoaded-and -not $h.ImportRunning){break}}catch{};Start-Sleep -Milliseconds 100}
 Check ((Request '/api/admin/database' $null $false).Status-eq 401) 'database status is admin-only'
 Check ((Request '/api/admin/database' '{}' $false).Status-eq 401) 'anonymous database migration rejected'
 Check ((Request '/api/admin/database' '{}' $true $false).Status-eq 400) 'database mutations require protocol header'
 $event=@{EventId=[Guid]::NewGuid().ToString('N').ToUpperInvariant();RunId=[Guid]::NewGuid().ToString('N');Machine='LEGACY';Device='D';Mac='001122334455';Status='installed'}
 Check ((Request '/api/client/install-event' ($json.Serialize($event))).Status-eq 200) 'legacy event accepted before migration'
 Check ((Operation 'test').Status-eq 200) 'admin UI can test actual MySQL connection'
 $migrated=Operation 'migrate';Check ($migrated.Status-eq 200) 'admin API migrates and activates database under repository maintenance lock'
 Check (-not $migrated.Body.Contains('Server=') -and -not $migrated.Body.Contains('ProtectedConnection')) 'migration response never returns connection credentials'
 $status=$json.DeserializeObject((Request '/api/admin/database').Body)
 Check ($status.storage.backend-eq 'mysql') 'database status reflects active external backend'
 Check ((Request '/api/client/install-event' ($json.Serialize($event))).Status-eq 200) 'same legacy event ID remains idempotent after cutover'
 $event.EventId=$event.EventId.ToLowerInvariant()
 Check ((Request '/api/client/install-event' ($json.Serialize($event))).Status-eq 200) 'case-normalized legacy event retry accepted'
 $logs=$json.DeserializeObject((Request '/api/admin/install-logs').Body)
 Check ($logs.total-eq 1 -and $logs.machines-eq 1) 'logs/counts/date API preserves existing semantics after migration'
 Check ((Operation 'sync').Status-eq 200) 'admin can replay database queue'
 Check ((Operation 'local').Status-eq 200) 'admin can synchronize and return to local backend'
 Check (($json.DeserializeObject((Request '/api/admin/database').Body)).storage.backend-eq 'local') 'local backend status restored'
 Check (($json.DeserializeObject((Request '/api/admin/install-logs').Body)).total-eq 1) 'reverse migration keeps historical log accessible through unchanged API'
 Write-Output ('ARTIFACTS: '+$root)
}finally{$server.Dispose();$logger.Dispose()}
