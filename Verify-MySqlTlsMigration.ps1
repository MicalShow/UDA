[CmdletBinding()]param(
    [string]$OutputDirectory = 'validation-mysql8-tls',
    [string]$MySqlBin = 'C:\Program Files\MySQL\MySQL Server 8.0\bin',
    [string]$OpenSsl = 'C:\Program Files\Git\usr\bin\openssl.exe',
    [string]$TestAssembly = 'validation-database-tls\DatabaseMigrationHarness.exe',
    [switch]$VerifyDatabaseOnly
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$work = Join-Path $taskRoot ($OutputDirectory + '-' + [Guid]::NewGuid().ToString('N'))
$data = Join-Path $work 'data'; $tls = Join-Path $work 'tls'
[void][IO.Directory]::CreateDirectory($data); [void][IO.Directory]::CreateDirectory($tls)
$mysqld = Join-Path $MySqlBin 'mysqld.exe'
if (-not (Test-Path -LiteralPath $mysqld) -or -not (Test-Path -LiteralPath $OpenSsl)) { throw 'MySQL server or OpenSSL test binary missing' }
$assemblyPath = Join-Path $taskRoot $TestAssembly
if (-not (Test-Path -LiteralPath $assemblyPath)) { throw 'Run Run-DatabaseMigrationTests.ps1 first' }
$providerTarget = Join-Path (Split-Path -Parent $assemblyPath) 'DatabaseProvider'
if (-not (Test-Path -LiteralPath $providerTarget)) { Copy-Item -LiteralPath (Join-Path $taskRoot 'DatabaseProvider') -Destination $providerTarget -Recurse }
[void][Reflection.Assembly]::LoadFrom($assemblyPath)
[UDA.ServiceEventLog]::Initialize((Join-Path $work 'events'))
$auditDirectory = Join-Path $work 'audit'
[void][IO.Directory]::CreateDirectory($auditDirectory)
$auditField = [UDA.WebAdminController].GetField('TestAuditDirectory',[Reflection.BindingFlags]'Static,NonPublic')
$auditField.SetValue($null,$auditDirectory)
$logField = [UDA.ServerStorage].GetField('TestLogDirectory',[Reflection.BindingFlags]'Static,NonPublic')
$logField.SetValue($null,(Join-Path $work 'events'))
$auditFile = Join-Path $auditDirectory ('admin-'+[DateTime]::UtcNow.ToString('yyyyMMdd')+'.jsonl')
$historicalAudit = '{"Utc":"2026-10-01T00:00:00Z","User":"history-admin","Operation":"历史登录测试","Status":200}'
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($auditFile,$historicalAudit+[Environment]::NewLine,$utf8)
$serviceLog = Join-Path $work 'events\history.log'
$serviceText = ('多行异常与中文🙂' * 10000)+[Environment]::NewLine+'stack trace'+[Environment]::NewLine
[IO.File]::WriteAllText($serviceLog,$serviceText,$utf8)
function Check($condition, $message) { if (-not $condition) { throw $message }; Write-Output ('PASS: ' + $message) }
function StartHidden($exe, [string[]]$arguments, $name) {
    $line = ($arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' '
    $child = Start-Process -FilePath $exe -ArgumentList $line -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $work ($name+'.stdout.txt')) -RedirectStandardError (Join-Path $work ($name+'.stderr.txt'))
    $null = $child.Handle
    return $child
}
function OpenSslCommand([string[]]$arguments, $name) {
    $p = StartHidden $OpenSsl $arguments $name
    if (-not $p.WaitForExit(30000)) { $p.Kill(); throw 'OpenSSL timeout' }
    if ($p.ExitCode -ne 0) { throw ('OpenSSL failed: ' + $name) }
}
$process = $null
try {
    $init = StartHidden $mysqld @('--no-defaults','--initialize-insecure',('--basedir='+(Split-Path -Parent $MySqlBin)),('--datadir='+$data)) 'initialize'
    if (-not $init.WaitForExit(60000)) { $init.Kill(); throw 'Isolated MySQL initialize timeout' }
    if ($init.ExitCode -ne 0) { throw 'Isolated MySQL initialization failed' }
    $ca = Join-Path $tls 'ca.pem'; $caKey = Join-Path $tls 'ca-key.pem'; $cert = Join-Path $tls 'server.pem'; $key = Join-Path $tls 'server-key.pem'; $csr = Join-Path $tls 'server.csr'; $ext = Join-Path $tls 'server.cnf'
    OpenSslCommand @('req','-x509','-newkey','rsa:2048','-nodes','-keyout',$caKey,'-out',$ca,'-subj','/CN=UDA Isolated Test CA','-days','1','-addext','basicConstraints=critical,CA:TRUE') 'ca'
    OpenSslCommand @('req','-newkey','rsa:2048','-nodes','-keyout',$key,'-out',$csr,'-subj','/CN=localhost') 'csr'
    [IO.File]::WriteAllText($ext,"[server]`nsubjectAltName=DNS:localhost`nextendedKeyUsage=serverAuth`nbasicConstraints=CA:FALSE`n")
    OpenSslCommand @('x509','-req','-in',$csr,'-CA',$ca,'-CAkey',$caKey,'-CAcreateserial','-out',$cert,'-days','1','-extfile',$ext,'-extensions','server') 'server-cert'
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0); $listener.Start(); $port=$listener.LocalEndpoint.Port; $listener.Stop()
    $process = StartHidden $mysqld @('--no-defaults',('--basedir='+(Split-Path -Parent $MySqlBin)),('--datadir='+$data),('--pid-file='+(Join-Path $data 'tls-test.pid')),('--port='+$port),'--bind-address=127.0.0.1','--mysqlx=OFF',('--ssl-ca='+$ca),('--ssl-cert='+$cert),('--ssl-key='+$key),'--console') 'server'
    $rootConnection = 'Server=127.0.0.1;Port='+$port+';Database=mysql;User ID=root;SslMode=Required;Connection Timeout=1;Pooling=False'
    $ready=$false
    for($i=0;$i -lt 100;$i++) { try { [UDA.ServerStorage]::TestConnection($rootConnection); $ready=$true; break } catch { if($process.HasExited){throw 'Isolated MySQL exited before accepting connections'}; Start-Sleep -Milliseconds 200 } }
    Check $ready 'isolated MySQL TLS listener is ready'
    $db = New-Object MySqlConnector.MySqlConnection($rootConnection); $db.Open()
    try {
        $cmd=$db.CreateCommand(); $cmd.CommandText="CREATE DATABASE uda_tls CHARACTER SET utf8mb4; CREATE USER 'uda_tls'@'%' IDENTIFIED WITH caching_sha2_password BY 'Isolated-Test-Password-Only-123!'; GRANT CREATE,SELECT,INSERT,UPDATE,DELETE ON uda_tls.* TO 'uda_tls'@'%';"; [void]$cmd.ExecuteNonQuery(); $cmd.Dispose()
        $cmd=$db.CreateCommand(); $cmd.CommandText="SELECT plugin FROM mysql.user WHERE User='uda_tls'"; Check ($cmd.ExecuteScalar() -eq 'caching_sha2_password') 'test account uses MySQL 8 SHA-2 authentication'; $cmd.Dispose()
    } finally { $db.Dispose() }
    $optionsType = [UDA.DatabaseMigrationProgram].Assembly.GetType('UDA.MigrationConnectionOptions')
    $build = $optionsType.GetMethod('Build',[Reflection.BindingFlags]'Static,NonPublic')
    function Connection($hostName,$mode,$caFile) { return [string]$build.Invoke($null,[object[]]@([string]$hostName,[uint32]$port,[string]'uda_tls',[string]'uda_tls',[string]'Isolated-Test-Password-Only-123!',[string]$mode,[string]$caFile)) }
    $verified=Connection 'localhost' 'VerifyFull' $ca
    [UDA.ServerStorage]::TestConnection($verified); Check $true 'VerifyFull with trusted PEM CA and matching hostname supports caching_sha2_password'
    $errorText=''; try { [UDA.ServerStorage]::TestConnection((Connection '127.0.0.1' 'VerifyFull' '')) } catch { $errorText=[UDA.ServerStorage]::DatabaseFailureMessage($_.Exception) }
    Check ($errorText.Contains('CA') -and -not $errorText.Contains('Isolated-Test-Password-Only-123!')) 'VerifyFull without trusted CA gives safe certificate diagnostics'
    $failed=$false; try { [UDA.ServerStorage]::TestConnection((Connection '127.0.0.1' 'VerifyFull' $ca)) } catch { $failed=$true }
    Check $failed 'VerifyFull still rejects an address absent from certificate SAN'
    [UDA.ServerStorage]::TestConnection((Connection '127.0.0.1' 'VerifyCA' $ca)); Check $true 'explicit VerifyCA validates CA without hostname check'
    [UDA.ServerStorage]::TestConnection((Connection '127.0.0.1' 'Required' '')); Check $true 'explicit Required connects with TLS when CA is not trusted'
    $source=Join-Path $work 'source'; [void][IO.Directory]::CreateDirectory($source)
    $inf=@'
[Version]
Signature="$WINDOWS NT$"
Class=Net
ClassGuid={4d36e972-e325-11ce-bfc1-08002be10318}
Provider=UDA Test
DriverVer=10/20/2026,1.0.0.0
[Manufacturer]
UDA=Models,NTamd64.10
[Models.NTamd64.10]
TestDevice=Install,PCI\VEN_FFFF&DEV_F001
[Install]
'@
    [IO.File]::WriteAllText((Join-Path $source 'tls-test.inf'),$inf)
    $logger=New-Object UDA.L((Join-Path $work 'logs'))
    try {
        $generator=New-Object UDA.OfflineRepoGenerator((New-Object UDA.TrustingSignatureVerifier),$logger)
        $repo=Join-Path $work 'repo'
        [void]$generator.Generate($repo,[string[]]@($source),(New-Object UDA.OsTarget),$null,'tls-test',[Threading.CancellationToken]::None,$null)
        $eventId=[Guid]::NewGuid().ToString('N'); $eventKey='install-events/'+$eventId+'.json'; $eventText='{"EventId":"'+$eventId+'","Machine":"TLS-TEST","Status":"installed"}'
        [UDA.ServerStorage]::Write($repo,$eventKey,$eventText,$true)
        $credentials=Join-Path $work 'test-admin.json'; [UDA.AdminCredentialStore]::Create($credentials,'tls-admin','Isolated-Admin-Password-123!')
        $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0); $listener.Start(); $httpPort=$listener.LocalEndpoint.Port; $listener.Stop()
        $config=New-Object UDA.LanServerConfig; $config.RepoRoot=$repo; $config.Host='localhost'; $config.Port=$httpPort; $config.AdminCredentialsFile=$credentials
        $server=New-Object UDA.LanDriverServer($config,$logger)
        $server.ImportSignatureVerifier=New-Object UDA.TrustingSignatureVerifier
        $cookies=New-Object Net.CookieContainer; $serializer=New-Object System.Web.Script.Serialization.JavaScriptSerializer
        function Request($path,$body=$null) {
            $request=[Net.HttpWebRequest]::Create('http://localhost:'+$httpPort+$path); $request.Proxy=$null; $request.CookieContainer=$cookies; $request.Timeout=180000
            if($null-ne $body){$request.Method='POST';$request.ContentType='application/json';$request.Headers['X-UDA-Protocol']='1';$bytes=[Text.Encoding]::UTF8.GetBytes($body);$request.ContentLength=$bytes.Length;$stream=$request.GetRequestStream();$stream.Write($bytes,0,$bytes.Length);$stream.Dispose()}
            try{$response=$request.GetResponse()}catch [Net.WebException]{if(-not $_.Exception.Response){throw};$response=$_.Exception.Response}
            try{$reader=New-Object IO.StreamReader($response.GetResponseStream());return @{Status=[int]$response.StatusCode;Data=$serializer.DeserializeObject($reader.ReadToEnd())}}finally{$response.Dispose()}
        }
        try {
            $server.Start()
            for($i=0;$i-lt 100;$i++){$health=Request '/api/health';if($health.Data.RepoLoaded -and -not $health.Data.ImportRunning){break};Start-Sleep -Milliseconds 100}
            $login=Request '/api/admin/login' ($serializer.Serialize(@{user='tls-admin';password='Isolated-Admin-Password-123!'}));Check ($login.Status -eq 200) 'standalone migration administrator authentication succeeds'
            $bad=Request '/api/admin/database' ($serializer.Serialize(@{action='test';connection=(Connection '127.0.0.1' 'VerifyFull' '')}))
            Check ($bad.Status -eq 400 -and $bad.Data.error.Contains('CA') -and -not $bad.Data.error.Contains('Isolated-Test-Password-Only-123!')) 'actual admin API returns safe CA diagnostics on certificate failure'
            Check (([UDA.ServerStorage]::Status($repo)).backend -eq 'local') 'failed connection test does not enable external storage'
            $test=Request '/api/admin/database' ($serializer.Serialize(@{action='test';connection=$verified}));Check ($test.Status -eq 200) 'actual admin API tests VerifyFull connection successfully'
            $migrated=Request '/api/admin/database' ($serializer.Serialize(@{action='migrate';connection=$verified}))
            Check ($migrated.Status -eq 200 -and $migrated.Data.documents -gt 0 -and $migrated.Data.storage.backend -eq 'mysql') 'actual admin API migrates to MySQL using VerifyFull TLS with SHA-2 account'
            Check ([UDA.ServerStorage]::Read($repo,$eventKey) -eq $eventText) 'historical installation event survives MySQL migration'
            Check (([UDA.ServerStorage]::AuditLogLines($repo)) -contains $historicalAudit) 'historical administrator audit is read from MySQL'
            $sql = New-Object MySqlConnector.MySqlConnection($verified); $sql.Open()
            try {
                $command=$sql.CreateCommand(); $command.CommandText="SELECT document_key,body FROM uda_documents WHERE document_key LIKE 'server-logs/service/%' ORDER BY document_key"
                $reader=$command.ExecuteReader(); $blocks=New-Object Collections.Generic.List[object]
                while($reader.Read()) { $block=$serializer.DeserializeObject($reader.GetString(1)); if($block.FileName-eq 'history.log'){[void]$blocks.Add($block)} }; $reader.Dispose()
                $bytes=New-Object IO.MemoryStream
                foreach($block in ($blocks | Sort-Object {[long]$_.Offset})){ $part=[Convert]::FromBase64String($block.Data); Check ($part.Length -le 65536) 'service log SQL blocks stay within 64 KiB'; $bytes.Write($part,0,$part.Length) }
                Check ([Text.Encoding]::UTF8.GetString($bytes.ToArray()) -eq $serviceText) 'large service log preserves UTF-8, emoji and multiline exceptions byte for byte'; $bytes.Dispose(); $command.Dispose()
            } finally { $sql.Dispose() }
            [UDA.ServerStorage]::SyncLogs($repo)
            $additionalAudit='{"Utc":"2026-10-02T00:00:00Z","User":"incremental-admin","Status":200}'
            [IO.File]::AppendAllText($auditFile,$additionalAudit+[Environment]::NewLine,$utf8)
            [UDA.ServerStorage]::SyncLogs($repo); [UDA.ServerStorage]::SyncLogs($repo)
            Check (@([UDA.ServerStorage]::AuditLogLines($repo) | Where-Object {$_ -eq $additionalAudit}).Count -eq 1) 'incremental append and repeated sync do not duplicate audit events'
            $storageFile=Join-Path $repo '.uda-storage.json'; $originalStorage=[IO.File]::ReadAllText($storageFile)
            $broken=$serializer.DeserializeObject($originalStorage)
            $broken.ProtectedConnection=[Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('Server=127.0.0.1;Port=1;User ID=test;Connection Timeout=1;Pooling=False'),$null,[Security.Cryptography.DataProtectionScope]::LocalMachine))
            $offlineAudit='{"Utc":"2026-10-03T00:00:00Z","User":"offline-admin","Status":200}'
            [IO.File]::AppendAllText($auditFile,$offlineAudit+[Environment]::NewLine,$utf8)
            try {
                [IO.File]::WriteAllText($storageFile,$serializer.Serialize($broken),$utf8)
                $failed=$false; try{[UDA.ServerStorage]::SyncLogs($repo)}catch{$failed=$true}
                Check ($failed -and ([IO.File]::ReadAllText($auditFile)).Contains($offlineAudit)) 'database outage retains original local audit for retry'
            } finally { [IO.File]::WriteAllText($storageFile,$originalStorage,$utf8) }
            [UDA.ServerStorage]::SyncLogs($repo)
            Check (([UDA.ServerStorage]::AuditLogLines($repo)) -contains $offlineAudit) 'database recovery retries pending local audit successfully'
            $archived=Request '/api/admin/audit'
            Check ($archived.Status -eq 200 -and @($archived.Data.items | Where-Object {$_.User -eq 'history-admin'}).Count -eq 1) 'authenticated audit console merges SQL and local records without duplicates'
            $local=Request '/api/admin/database' ($serializer.Serialize(@{action='local'}))
            Check ($local.Status -eq 200 -and $local.Data.storage.backend -eq 'local') 'reverse migration preserves the dataset and returns to local storage'
            Check ([UDA.ServerStorage]::Read($repo,$eventKey) -eq $eventText) 'historical installation event survives reverse migration'
            Check (([UDA.ServerStorage]::AuditLogLines($repo)) -contains $offlineAudit) 'reverse migration exports audit archive for local reading'
            Check (@(Get-ChildItem -LiteralPath (Join-Path $repo 'server-logs\service') -Filter '*.json' -Recurse).Count -gt 0) 'reverse migration exports service log blocks locally'
            if ($VerifyDatabaseOnly) { $server.Dispose(); & (Join-Path $taskRoot 'Verify-DatabaseOnlyUpgrade.ps1') -Repository $repo -Connection $verified -Source $source -WorkDirectory $work }
        } finally { $server.Dispose() }
    } finally { $logger.Dispose() }
    Write-Output ('ARTIFACTS: '+$work)
} finally {
    $auditField.SetValue($null,$null)
    $logField.SetValue($null,$null)
    # MySQL on Windows may launch a child server and exit the original process. Stop only a
    # PID recorded in this test datadir, after verifying its command line belongs to this run.
    foreach ($pidFile in @(Get-ChildItem -LiteralPath $data -Filter '*.pid' -ErrorAction SilentlyContinue)) {
        $serverId = 0
        if ([int]::TryParse(([IO.File]::ReadAllText($pidFile.FullName)).Trim(),[ref]$serverId)) {
            $serverProcess = Get-CimInstance Win32_Process -Filter ('ProcessId='+$serverId) -ErrorAction SilentlyContinue
            if ($null -ne $serverProcess -and $serverProcess.Name -eq 'mysqld.exe' -and $serverProcess.CommandLine.Contains($data)) { Stop-Process -Id $serverId -Force }
        }
    }
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
}
