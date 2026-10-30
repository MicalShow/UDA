$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $MyInvocation.MyCommand.Path
$work=Join-Path $workspace ('validation-5344-rebind-'+[Guid]::NewGuid().ToString('N'))
$data=Join-Path $work 'data';[void][IO.Directory]::CreateDirectory($data)
$mysqld='C:\Program Files\MySQL\MySQL Server 8.0\bin\mysqld.exe'
$init=Start-Process $mysqld -ArgumentList @('--no-defaults','--initialize-insecure',('--datadir="'+$data+'"')) -WindowStyle Hidden -PassThru -Wait -RedirectStandardError (Join-Path $work 'init.err')
if($init.ExitCode -ne 0){throw 'Isolated MySQL initialization failed'}
$listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0);$listener.Start();$port=([Net.IPEndPoint]$listener.LocalEndpoint).Port;$listener.Stop()
$server=Start-Process $mysqld -ArgumentList @('--no-defaults',('--datadir="'+$data+'"'),('--port='+$port),'--bind-address=127.0.0.1','--mysqlx=0') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $work 'server.out') -RedirectStandardError (Join-Path $work 'server.err')
try {
 [void][Reflection.Assembly]::LoadFrom((Join-Path $workspace 'validation-5344-database\DatabaseMigrationHarness.exe'))
 [UDA.ServerStorage]::UseProviderDirectory((Join-Path $workspace 'DatabaseProvider'))
 $flags=[Reflection.BindingFlags]'NonPublic,Static';$type=[UDA.ServerStorage]
 $open=$type.GetMethod('Open',$flags);$needs=$type.GetMethod('SetupConnectionNeedsRebind',$flags);$rebind=$type.GetMethod('RebindSetupConnection',$flags)
 $base='Server=127.0.0.1;Port='+$port+';User ID=root;SslMode=None;Connection Timeout=1'
 $db=$null
 for($i=0;$i -lt 40;$i++){try{$db=$open.Invoke($null,[object[]]@([string]$base));break}catch{Start-Sleep -Milliseconds 500}}
 if(!$db){throw 'Isolated MySQL not ready'}
 try{$cmd=$db.CreateCommand();$cmd.CommandText='CREATE DATABASE uda_rebind CHARACTER SET utf8mb4';[void]$cmd.ExecuteNonQuery();$cmd.CommandText='CREATE DATABASE uda_wrong CHARACTER SET utf8mb4';[void]$cmd.ExecuteNonQuery();$cmd.Dispose()}finally{$db.Dispose()}
 $connection=$base+';Database=uda_rebind';$wrong=$base+';Database=uda_wrong'
 $repo=Join-Path $work 'repo';$source=Join-Path $work 'source';[void][IO.Directory]::CreateDirectory($source)
 Copy-Item (Join-Path $workspace 'test-fixtures\organized-v1\*') $source
 $generator=New-Object UDA.OfflineRepoGenerator((New-Object UDA.TrustingSignatureVerifier),$null)
 [void]$generator.Generate($repo,[string[]]@($source),(New-Object UDA.OsTarget),$null,'setup-test',[Threading.CancellationToken]::None,$null)
 if($needs.Invoke($null,[object[]]@([string]$repo))){throw 'Fresh repository incorrectly needs rebind'}
 [UDA.ServerStorage]::Write($repo,'install-events/0123456789abcdef0123456789abcdef.json','{"EventId":"setup-rebind-test","Machine":"keep-history"}',$true)
 [void][UDA.ServerStorage]::Migrate($repo,$connection)
 if($needs.Invoke($null,[object[]]@([string]$repo))){throw 'Normal local upgrade incorrectly needs rebind'}
 $cfg=[UDA.ServerStorage]::Config($repo);$identity=$cfg.RepositoryId;$configPath=Join-Path $repo '.uda-storage.json'
 $cfg.ProtectedConnection=[Convert]::ToBase64String([byte[]](1,2,3,4,5))
 $json=New-Object Web.Script.Serialization.JavaScriptSerializer
 [IO.File]::WriteAllText($configPath,$json.Serialize($cfg));$original=[IO.File]::ReadAllText($configPath)
 if(!$needs.Invoke($null,[object[]]@([string]$repo))){throw 'Unreadable copied ciphertext not detected'}
 $type.GetMethod('TestSetupConnection',$flags).Invoke($null,[object[]]@([string]$wrong))
 $refused=$false;try{$rebind.Invoke($null,[object[]]@([string]$repo,[string]$wrong))}catch{$refused=$true}
 if(!$refused -or [IO.File]::ReadAllText($configPath) -ne $original){throw 'Wrong dataset changed local configuration'}
 $rebind.Invoke($null,[object[]]@([string]$repo,[string]$connection))
 [UDA.ServerStorage]::RequireMigratedDatabase($repo)
 if($needs.Invoke($null,[object[]]@([string]$repo)) -or [UDA.ServerStorage]::Config($repo).RepositoryId -ne $identity){throw 'Rebind did not preserve repository identity'}
 if([IO.File]::ReadAllText($configPath).Contains('Server=127.0.0.1')){throw 'Plaintext connection saved'}
 $history=[UDA.ServerStorage]::Read($repo,'install-events/0123456789abcdef0123456789abcdef.json')
 if(!$history.Contains('keep-history')){throw 'Historical log lost'}
 'PASS: fresh setup, normal upgrade, unreadable DPAPI, wrong dataset refusal without config change, same dataset reconnect, encrypted connection, historical log retention'
} finally {
 if(!$server.HasExited){Stop-Process -Id $server.Id -Force;$server.WaitForExit()}
}
