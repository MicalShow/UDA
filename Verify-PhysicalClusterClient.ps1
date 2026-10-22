[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$RouterUrl,
  [Parameter(Mandatory=$true)][string]$HardwareId,
  [ValidateRange(1,128)][int]$Clients=40,
  [string]$DeviceClass='Net',
  [string]$DownloadDirectory,
  [System.Security.SecureString]$ClientToken
)
$ErrorActionPreference='Stop'
[Net.ServicePointManager]::DefaultConnectionLimit=[Math]::Max(128,[Net.ServicePointManager]::DefaultConnectionLimit)
$routerUri=$null
if(-not [Uri]::TryCreate($RouterUrl,[UriKind]::Absolute,[ref]$routerUri) -or $routerUri.Scheme -ne 'https' -or $routerUri.UserInfo){throw 'RouterUrl must be an HTTPS URL without embedded credentials'}
$base=$routerUri.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
if([string]::IsNullOrWhiteSpace($DownloadDirectory)){$DownloadDirectory=Join-Path ([IO.Path]::GetTempPath()) ('UDA-PhysicalCluster-'+[Guid]::NewGuid().ToString('N'))}
[IO.Directory]::CreateDirectory($DownloadDirectory)|Out-Null
$ownsSecureToken=$null -eq $ClientToken
$secureToken=if($ownsSecureToken){Read-Host '输入测试集群客户端 Token' -AsSecureString}else{$ClientToken}
$tokenPtr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
try{$token=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenPtr)}finally{[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPtr)}
try {
  $routes=[System.Collections.Generic.List[object]]::new()
  for($i=0;$i -lt $Clients;$i++) {
    $name='UDA-CLUSTER-TEST-{0}-{1}' -f $env:COMPUTERNAME,$i
    $request=@{
      Protocol=2;RequestId=[Guid]::NewGuid().ToString('N');MachineName=$name
      OsArchitecture='amd64';OsMajorVersion=10;OsBuildNumber=19045
      Devices=@(@{DeviceInstanceId="TEST\$i";FriendlyName='UDA physical cluster validation';Class=$DeviceClass;HardwareIds=@($HardwareId);CompatibleIds=@()})
    } | ConvertTo-Json -Depth 8 -Compress
    $response=Invoke-RestMethod -Method Post -Uri ($base+'/api/report') -Headers @{'X-UDA-Protocol'='2';'X-UDA-Token'=$token} -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($request)) -TimeoutSec 30
    if(-not $response.ok){throw "report request $i was rejected: $($response.error)"}
    if(@($response.candidates).Count -ne 1){$why=@($response.unmatchedDevices|ForEach-Object{$_.reason}) -join '; ';throw "report request $i returned no unique driver candidate for '$HardwareId'. $why"}
    $candidate=$response.candidates[0]
    if([string]::IsNullOrWhiteSpace($candidate.downloadBaseUrl) -or [string]::IsNullOrWhiteSpace($candidate.infRelativePath)){throw "candidate $i is missing its download node or INF path"}
    $nodeUri=$null
    if(-not [Uri]::TryCreate($candidate.downloadBaseUrl,[UriKind]::Absolute,[ref]$nodeUri) -or $nodeUri.Scheme -ne 'https' -or $nodeUri.UserInfo){throw "candidate $i selected a non-HTTPS or invalid node URL"}
    $download=$nodeUri.GetLeftPart([UriPartial]::Authority).TrimEnd('/')+'/api/download?path='+[Uri]::EscapeDataString($candidate.infRelativePath)
    $routes.Add([pscustomobject]@{Machine=$name;Node=$nodeUri.GetLeftPart([UriPartial]::Authority).TrimEnd('/');Url=$download;Inf=$candidate.infRelativePath;Version=$candidate.driverVerVersion})
  }
  $distribution=@($routes|Group-Object Node|Sort-Object Name|ForEach-Object{[pscustomobject]@{Node=$_.Name;Assignments=$_.Count}})
  if($distribution.Count -lt 2){throw "all $Clients report requests selected one node. Check that both nodes are healthy, have identical manifests and share the same client token."}
  Add-Type -AssemblyName System.IO.Compression
  Add-Type -TypeDefinition @'
#pragma warning disable SYSLIB0014
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
public sealed class UdaClusterClientStat { public long Bytes; public long FirstByteMs; public long ExtractMs; public double MiBPerSecond; public string Node; }
public static class UdaClusterTransferBenchmark {
  public static UdaClusterClientStat[] Run(string[] urls,string[] nodes,string token,string root,int timeoutMs) {
    var tasks=new Task<UdaClusterClientStat>[urls.Length];
    for(int i=0;i<urls.Length;i++){int n=i;tasks[i]=Task.Factory.StartNew(()=>Transfer(urls[n],nodes[n],token,root,n,timeoutMs),TaskCreationOptions.LongRunning);}
    try{Task.WaitAll(tasks);return tasks.Select(t=>t.Result).ToArray();}catch(AggregateException e){throw e.Flatten().InnerExceptions[0];}
  }
  private static UdaClusterClientStat Transfer(string url,string node,string token,string root,int index,int timeoutMs){
    string zip=Path.Combine(root,index.ToString("D3")+"-"+Guid.NewGuid().ToString("N")+".zip"),expanded=zip+".expanded";long first=-1,total=0,expected=-1;
    try{
      var req=(HttpWebRequest)WebRequest.Create(url);req.Proxy=null;req.Timeout=timeoutMs;req.ReadWriteTimeout=60000;req.Headers["X-UDA-Protocol"]="2";req.Headers["X-UDA-Token"]=token;
      var clock=System.Diagnostics.Stopwatch.StartNew();
      using(var response=(HttpWebResponse)req.GetResponse()){
        if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("HTTP "+(int)response.StatusCode);expected=response.ContentLength;if(expected<=0)throw new IOException("missing Content-Length");
        using(var input=response.GetResponseStream())using(var output=new FileStream(zip,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,FileOptions.SequentialScan)){byte[] b=new byte[1024*1024];int r;while((r=input.Read(b,0,b.Length))>0){if(first<0)first=clock.ElapsedMilliseconds;output.Write(b,0,r);total+=r;}}
      }
      clock.Stop();if(total!=expected)throw new IOException("download length mismatch");
      var extract=System.Diagnostics.Stopwatch.StartNew();string full=Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;Directory.CreateDirectory(expanded);
      using(var fs=File.OpenRead(zip))using(var archive=new ZipArchive(fs,ZipArchiveMode.Read))foreach(var entry in archive.Entries){string target=Path.GetFullPath(Path.Combine(expanded,entry.FullName.Replace('/',Path.DirectorySeparatorChar)));if(!target.StartsWith(full,StringComparison.OrdinalIgnoreCase))throw new IOException("archive path traversal rejected");if(entry.FullName.EndsWith("/",StringComparison.Ordinal)){Directory.CreateDirectory(target);continue;}Directory.CreateDirectory(Path.GetDirectoryName(target));using(var src=entry.Open())using(var dst=File.Create(target))src.CopyTo(dst,1024*1024);}
      extract.Stop();if(!Directory.GetFiles(expanded,"*.inf",SearchOption.AllDirectories).Any())throw new IOException("archive did not contain an INF");
      return new UdaClusterClientStat{Bytes=total,FirstByteMs=first,ExtractMs=extract.ElapsedMilliseconds,MiBPerSecond=(total/1048576.0)/Math.Max(.001,clock.Elapsed.TotalSeconds),Node=node};
    }finally{try{if(File.Exists(zip))File.Delete(zip);}catch{}try{if(Directory.Exists(expanded))Directory.Delete(expanded,true);}catch{}}
  }
}
'@
  $urls=[string[]]@($routes|ForEach-Object{$_.Url});$nodes=[string[]]@($routes|ForEach-Object{$_.Node})
  $watch=[Diagnostics.Stopwatch]::StartNew();$stats=[UdaClusterTransferBenchmark]::Run($urls,$nodes,$token,$DownloadDirectory,180000);$watch.Stop()
  $rates=@($stats|ForEach-Object{$_.MiBPerSecond}|Sort-Object);$first=@($stats|ForEach-Object{$_.FirstByteMs}|Sort-Object);$extract=@($stats|ForEach-Object{$_.ExtractMs}|Sort-Object);$p95=[int][Math]::Ceiling($Clients*.95)-1
  if($first[$p95] -gt 3000){throw "TTFB P95 exceeded 3 seconds: $($first[$p95])ms"};if($extract[$p95] -gt 3000){throw "extraction P95 exceeded 3 seconds: $($extract[$p95])ms"}
  $sum=($stats|Measure-Object Bytes -Sum).Sum
  [pscustomobject]@{Router=$base;Clients=$Clients;HardwareId=$HardwareId;SelectedNodes=$distribution;PackageMiB=[Math]::Round($stats[0].Bytes/1MB,2);ClientMiBpsMedian=[Math]::Round(($rates[[int][Math]::Floor(($Clients-1)/2)]+$rates[[int][Math]::Ceiling(($Clients-1)/2)])/2,2);FirstByteMsP95=$first[$p95];ExtractionMsP95=$extract[$p95];AggregateMiBps=[Math]::Round(($sum/1MB)/$watch.Elapsed.TotalSeconds,2);Completed=$stats.Count} | Format-List
} finally { $token=$null;if($ownsSecureToken){$secureToken.Dispose()} }
