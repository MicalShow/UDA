param([string]$OutputDirectory = 'web-console-stage1')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Web.Extensions
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory + '\Lan\UDA-Server-Manager.exe')))
$json = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$root = Join-Path $PSScriptRoot ($OutputDirectory + '\upload-test-' + [Guid]::NewGuid().ToString('N'))
$cfg = New-Object UDA.LanServerConfig
$cfg.RepoRoot = Join-Path $root 'repo'; $cfg.Host = 'localhost'; $cfg.Token = 'upload-test-token'
$port = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
$port.Start(); $cfg.Port = $port.LocalEndpoint.Port; $port.Stop()
$testLogger = New-Object UDA.L((Join-Path $root 'logs'))
$server = New-Object UDA.LanDriverServer($cfg,$testLogger)
$baseUrl = 'http://localhost:' + $cfg.Port
function Request($path,$body,$token) {
    $request = [Net.HttpWebRequest]::Create($baseUrl + $path)
    $request.Proxy = $null; $request.Timeout = 10000
    $request.Headers['X-UDA-Protocol'] = '1'
    if ($token) { $request.Headers['X-UDA-Token'] = $cfg.Token }
    if ($null -ne $body) { $request.Method = 'POST'; $bytes = [Text.Encoding]::UTF8.GetBytes($body); $request.ContentLength = $bytes.Length; $stream = $request.GetRequestStream(); $stream.Write($bytes,0,$bytes.Length); $stream.Dispose() }
    try { $response = $request.GetResponse() } catch [Net.WebException] { if (-not $_.Exception.Response) { throw }; $response = $_.Exception.Response }
    try { $reader = New-Object IO.StreamReader($response.GetResponseStream()); return @{ Status = [int]$response.StatusCode; Body = $reader.ReadToEnd() } } finally { $response.Dispose() }
}
try {
    $server.Start()
    $page = Request '/admin' $null $false
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'web-console.html'), $page.Body, [Text.Encoding]::UTF8)
    if ($page.Status -ne 200 -or -not $page.Body.Contains('loginView') -or -not $page.Body.Contains('webkitdirectory')) { throw 'Web console not available' }
    if ((Request '/api/admin/upload/start' '' $false).Status -ne 401) { throw 'Anonymous upload accepted' }
    $started = Request '/api/admin/upload/start' '' $true
    if ($started.Status -ne 200) { throw $started.Body }
    $batch = $json.DeserializeObject($started.Body)
    $path = '/api/admin/upload/file?batch=' + $batch.batch + '&path='
    if ((Request ($path + 'drivers%2Fsample.inf') '[Version]' $true).Status -ne 200) { throw 'Upload failed' }
    if ((Get-Content -LiteralPath (Join-Path $batch.source 'drivers\sample.inf') -Raw) -ne '[Version]') { throw 'Uploaded bytes differ' }
    if ((Request ($path + '..%2Fescape.inf') 'bad' $true).Status -ne 400) { throw 'Traversal accepted' }
    if ((Request ($path + 'drivers%2Fsample.inf') 'overwrite' $true).Status -ne 400) { throw 'Overwrite accepted' }
    if (-not $batch.source.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) { throw 'Upload on wrong volume' }
    Write-Output 'PASS: Web login page, folder upload, authentication, byte preservation, traversal/overwrite rejection, repository-volume storage'
    $chunkPath = '/api/admin/upload/chunk?batch=' + $batch.batch + '&path=drivers%2Fresumed.inf&total=6&stamp=123'
    if ((Request ($chunkPath + '&offset=0') 'abc' $true).Status -ne 200) { throw 'Initial upload chunk failed' }
    $statusPath = '/api/admin/upload/status?batch=' + $batch.batch + '&path=drivers%2Fresumed.inf&total=6&stamp=123'
    $progress = $json.DeserializeObject((Request $statusPath $null $true).Body)
    if ($progress.received -ne 3 -or $progress.complete) { throw 'Partial upload progress incorrect' }
    if ((Request ($chunkPath + '&offset=0') 'def' $true).Status -ne 400) { throw 'Stale offset overwrote uploaded bytes' }
    $server.Dispose()
    $server = New-Object UDA.LanDriverServer($cfg,$testLogger)
    $server.Start()
    $progress = $json.DeserializeObject((Request $statusPath $null $true).Body)
    if ($progress.received -ne 3) { throw 'Restart lost partial upload progress' }
    if ((Request ($chunkPath + '&offset=3') 'def' $true).Status -ne 200) { throw 'Resume failed after restart' }
    if ([IO.File]::ReadAllText((Join-Path $batch.source 'drivers\resumed.inf')) -ne 'abcdef') { throw 'Resumed file bytes differ' }
    if ((Request ($statusPath.Replace('stamp=123','stamp=456')) $null $true).Status -ne 400) { throw 'Changed source identity reused stale partial upload' }
    Write-Output 'PASS: chunk offsets, byte progress, restart persistence, resume integrity and changed-file rejection'
    $discarded = $json.DeserializeObject((Request '/api/admin/upload/start' '' $true).Body)
    if ((Request ('/api/admin/upload/discard?batch='+$discarded.batch) '' $true).Status -ne 200 -or (Test-Path -LiteralPath $discarded.source)) { throw 'Discard did not reclaim unused upload session' }
    $infBody = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'test-fixtures\organized-v1\driver.inf'))
    $sysBody = 'synthetic driver'
    $inventory = $json.Serialize(@{FileCount=2;TotalBytes=[Text.Encoding]::UTF8.GetByteCount($infBody)+[Text.Encoding]::UTF8.GetByteCount($sysBody)})
    $importBatch = $json.DeserializeObject((Request '/api/admin/upload/start' $inventory $true).Body)
    $cfg.ImportAllowRoots.Add($importBatch.source)
    $server.ImportSignatureVerifier = New-Object UDA.TrustingSignatureVerifier
    $importPath = '/api/admin/import?src='+[Uri]::EscapeDataString($importBatch.source)
    if ((Request $importPath '' $true).Status -ne 400) { throw 'Incomplete upload inventory was imported' }
    if ((Request ('/api/admin/upload/file?batch='+$importBatch.batch+'&path=drivers%2Fdriver.inf') $infBody $true).Status -ne 200) { throw 'INF fixture upload failed' }
    if ((Request $importPath '' $true).Status -ne 400) { throw 'Missing companion file inventory was imported' }
    if ((Request ('/api/admin/upload/file?batch='+$importBatch.batch+'&path=drivers%2Fdriver.sys') $sysBody $true).Status -ne 200) { throw 'SYS fixture upload failed' }
    if ((Request $importPath '' $true).Status -ne 200) { throw 'Complete uploaded source was not imported' }
    if (Test-Path -LiteralPath $importBatch.source) { throw 'Imported upload staging was not reclaimed' }
    if ([UDA.OfflineRepoGenerator]::TryLoadManifest($cfg.RepoRoot).Entries.Count -eq 0) { throw 'Upload cleanup lost published repository content' }
    Write-Output 'PASS: incomplete inventory rejection, complete upload import, disposable source cleanup and published package preservation'
    $event = $json.Serialize(@{ EventId = [Guid]::NewGuid().ToString('N'); RunId = [Guid]::NewGuid().ToString('N'); Machine = 'UDA-TEST-PC'; Mac = '001122334455'; Device = 'TEST-DEVICE'; Status = 'installed'; Update = $true })
    if ((Request '/api/client/install-event' $event $false).Status -ne 401) { throw 'Anonymous telemetry accepted' }
    if ((Request '/api/client/install-event' $event $true).Status -ne 200) { throw 'Telemetry write failed' }
    if ((Request '/api/client/install-event' $event $true).Status -ne 200) { throw 'Telemetry retry failed' }
    $logs = Request '/api/admin/install-logs?q=001122334455' $null $true
    $events = $json.DeserializeObject($logs.Body)
    if ($events.total -ne 1 -or $events.machines -ne 1 -or $events.hardware -ne 1 -or $events.updates -ne 1) { throw 'Telemetry dedup/search/count failed' }
    Write-Output 'PASS: authenticated event persistence, retry deduplication, MAC query and unique machine/hardware/update counts'
    if ($json.DeserializeObject((Request '/api/admin/install-logs?q=00%3A11%3A22%3A33%3A44%3A55' $null $true).Body).total -ne 1) { throw 'Formatted MAC search failed' }
    $cfg.AdminCredentialsFile = Join-Path $root 'missing-admin.json'
    if ((Request '/api/admin/drivers' $null $true).Status -ne 401) { throw 'Missing admin file promoted a client token to admin' }
    $cfg.AdminCredentialsFile = ''
    Write-Output 'PASS: formatted MAC lookup and fail-closed admin authentication when configured credentials are missing'
    for($i=0;$i-lt 43;$i++){
      $row=New-Object UDA.DriverInstallEvent;$row.EventId=[Guid]::NewGuid().ToString('N');$row.RunId='pagination-test';$row.Utc=[DateTime]::UtcNow.AddSeconds($i).ToString('o');$row.Machine='page-test';$row.Status='log'
      [IO.File]::WriteAllText((Join-Path $cfg.RepoRoot ('install-events\'+$row.EventId+'.json')),$json.Serialize($row))
    }
    $p1=$json.DeserializeObject((Request '/api/admin/install-logs?run=pagination-test&page=1&pageSize=20' $null $true).Body)
    $p2=$json.DeserializeObject((Request '/api/admin/install-logs?run=pagination-test&page=2&pageSize=20' $null $true).Body)
    $p3=$json.DeserializeObject((Request '/api/admin/install-logs?run=pagination-test&page=99&pageSize=20' $null $true).Body)
    if($p1.pages-ne 3-or $p1.items.Count-ne 20-or $p2.items.Count-ne 20-or $p3.page-ne 3-or $p3.items.Count-ne 3){throw 'Install log page count/boundaries failed'}
    if(@($p1.items|Where-Object EventId -in $p2.items.EventId).Count){throw 'Install log pages overlap'}
    Write-Output 'PASS: installation logs multiple pages, page size, unique records and last-page clamping'
} finally { $server.Dispose(); $testLogger.Dispose() }
