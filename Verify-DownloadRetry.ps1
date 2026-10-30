param([string]$OutputDirectory = 'release-v5.0.0')
$ErrorActionPreference='Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory+'\Lan\UDA-Server-Manager.exe')))
Add-Type -TypeDefinition @'
using System;using System.Net;using System.Threading;
public sealed class RetryHttpFixture:IDisposable {
 readonly HttpListener listener=new HttpListener(); readonly Thread worker;
 public int Requests; public int Status=503;
 public RetryHttpFixture(int port){listener.Prefixes.Add("http://localhost:"+port+"/");listener.Start();worker=new Thread(Loop);worker.Start();}
 void Loop(){while(listener.IsListening){HttpListenerContext c;try{c=listener.GetContext();}catch{return;}
 try{if(c.Request.HttpMethod=="POST")using(var r=new System.IO.StreamReader(c.Request.InputStream))r.ReadToEnd();int n=Interlocked.Increment(ref Requests);c.Response.StatusCode=Status==503&&n>1?200:Status;if(c.Response.StatusCode==200){byte[] b=c.Request.HttpMethod=="POST"?System.Text.Encoding.UTF8.GetBytes("{\"Ok\":true,\"Candidates\":[]}"):new byte[]{1,2,3};c.Response.OutputStream.Write(b,0,b.Length);}}finally{c.Response.Close();}}}
 public void Dispose(){listener.Close();worker.Join(2000);}
}
'@
$tcp=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0);$tcp.Start();$port=$tcp.LocalEndpoint.Port;$tcp.Stop()
$fixture=New-Object RetryHttpFixture($port)
try {
 $cfg=New-Object UDA.LanClientConfig;$cfg.MaxRetries=3;$cfg.BackoffInitialMs=10
 $client=New-Object UDA.LanDriverClient($cfg,$null,$null,(New-Object UDA.OsTarget),$null,$null,$null,$null,$null)
 $method=$client.GetType().GetMethod('Download',[Reflection.BindingFlags]'Instance,NonPublic')
 $values=[object[]]@(('http://localhost:'+$port+'/driver'),[Threading.CancellationToken]::None,$null)
 if(-not $method.Invoke($client,$values) -or $fixture.Requests-ne 2 -or $values[2].Length-ne 3){throw '503 was not retried successfully'}
 $fixture.Status=401;$before=$fixture.Requests;$values[2]=$null
 if($method.Invoke($client,$values) -or $fixture.Requests-ne ($before+1)){throw 'Authentication error was retried or accepted'}
 $fixture.Status=429;$before=$fixture.Requests;$values[2]=$null
 if($method.Invoke($client,$values) -or $fixture.Requests-ne ($before+3)){throw 'Temporary error retry was not bounded'}
 Write-Output 'PASS: transient 503 recovery, permanent 401 rejection and bounded 429 retries'
 $fixture.Status=503
 $diskMethod=$client.GetType().GetMethod('DownloadToFile',[Reflection.BindingFlags]'Instance,NonPublic')
 [string]$cache=Join-Path $PSScriptRoot ($OutputDirectory+'\stream-download-'+[Guid]::NewGuid().ToString('N')+'.zip')
 $received=New-Object 'System.Collections.Generic.List[long]'
 $callback=[Action[long]]{param($bytes)$received.Add($bytes)}
 $diskValues=[object[]]@(('http://localhost:'+$port+'/driver'),[string]$cache,[Threading.CancellationToken]::None,$callback)
 if(-not $diskMethod.Invoke($client,$diskValues) -or (Get-Item -LiteralPath $cache).Length-ne 3 -or $received.Count-eq 0 -or $received[$received.Count-1]-ne 3){throw 'Disk streaming or byte progress failed'}
 $cancel=New-Object Threading.CancellationTokenSource;$cancel.Cancel();$diskValues[2]=$cancel.Token;$cancelled=$false
 try{[void]$diskMethod.Invoke($client,$diskValues)}catch{$cancelled=$_.Exception.ToString().Contains('OperationCanceledException')}
 if(-not $cancelled -or (Test-Path -LiteralPath $cache)){throw 'Cancelled streaming cache was not removed'}
 $cancel.Dispose()
 Write-Output 'PASS: disk-backed download, byte progress and cancellation cleanup'
 $post=$client.GetType().GetMethod('PostJson',[Reflection.BindingFlags]'Instance,NonPublic')
 $fixture.Requests=0;$fixture.Status=503
 $payload=New-Object UDA.LanReportRequest
 $postValues=[object[]]@(('http://localhost:'+$port+'/report'),$payload.PSObject.BaseObject,[Threading.CancellationToken]::None,$null)
 if(-not $post.Invoke($client,$postValues)-or $fixture.Requests-ne 2){throw 'Transient hardware report failure was not recovered'}
 $fixture.Status=401;$before=$fixture.Requests
 if($post.Invoke($client,$postValues)-or $fixture.Requests-ne ($before+1)){throw 'Report authentication failure was retried'}
 $fixture.Status=429;$before=$fixture.Requests
 if($post.Invoke($client,$postValues)-or $fixture.Requests-ne ($before+3)){throw 'Hardware report retries were not bounded'}
 Write-Output 'PASS: hardware report transient recovery, permanent rejection and bounded retries'
} finally {$fixture.Dispose()}
