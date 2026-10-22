[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$ServerUrl,
  [Parameter(Mandatory=$true)][string]$InfPath,
  [ValidateRange(1,128)][int]$Clients=40,
  [string]$DownloadDirectory,
  [System.Security.SecureString]$ClientToken
)
$ErrorActionPreference='Stop'
[Net.ServicePointManager]::DefaultConnectionLimit=[Math]::Max(128,[Net.ServicePointManager]::DefaultConnectionLimit)
$uri=$null
if(-not [Uri]::TryCreate($ServerUrl,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -notin @('http','https') -or $uri.UserInfo){throw 'ServerUrl must be an HTTP(S) URL without embedded credentials'}
$base=$uri.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
if([string]::IsNullOrWhiteSpace($DownloadDirectory)){$DownloadDirectory=Join-Path ([IO.Path]::GetTempPath()) ('UDA-PhysicalTransfer-'+[Guid]::NewGuid().ToString('N'))}
[IO.Directory]::CreateDirectory($DownloadDirectory)|Out-Null
Add-Type -AssemblyName System.IO.Compression
$uriPath=[Uri]::EscapeDataString($InfPath.Trim().Replace('\','/'))
$downloadUrl=$base+'/api/download?path='+$uriPath
$ownsSecureToken=$null -eq $ClientToken
$secureToken=if($ownsSecureToken){Read-Host '输入服务端客户端 Token' -AsSecureString}else{$ClientToken}
$tokenPtr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
try{$token=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenPtr)}finally{[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPtr)}
Add-Type -TypeDefinition @'
#pragma warning disable SYSLIB0014
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
public sealed class UdaPhysicalClientStat { public long Bytes; public long FirstByteMs; public long DownloadMs; public long ExtractMs; public double MiBPerSecond; }
public static class UdaPhysicalTransferBenchmark {
  private static long ReservedBytes;
  public static UdaPhysicalClientStat[] Run(string url, string token, int clients, string root, int timeoutMs) {
    var tasks = new Task<UdaPhysicalClientStat>[clients];
    for (int i=0; i<clients; i++) {
      int index=i;
      tasks[i]=Task.Factory.StartNew(() => {
        var request=(HttpWebRequest)WebRequest.Create(url); request.Proxy=null; request.Timeout=timeoutMs; request.ReadWriteTimeout=60000;
        request.Headers["X-UDA-Protocol"]="2"; request.Headers["X-UDA-Token"]=token;
        string zip=Path.Combine(root,index.ToString("D3")+"-"+Guid.NewGuid().ToString("N")+".zip");
        string extracted=zip+".expanded"; long firstByte=-1, total=0, expected=-1, reservation=0; var downloadClock=System.Diagnostics.Stopwatch.StartNew();
        try {
          using (var response=(HttpWebResponse)request.GetResponse()) {
            if(response.StatusCode!=HttpStatusCode.OK) throw new IOException("HTTP status "+(int)response.StatusCode);
            expected=response.ContentLength; if(expected<=0) throw new IOException("server did not provide Content-Length");
            Interlocked.Add(ref ReservedBytes,expected); reservation=expected;
            var drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)));
            if(Interlocked.Read(ref ReservedBytes)+256L*1024*1024>drive.AvailableFreeSpace) throw new IOException("not enough free space for concurrent download files; reduce Clients or choose another DownloadDirectory");
            using(var input=response.GetResponseStream())
            using(var output=new FileStream(zip,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,FileOptions.SequentialScan)) {
              byte[] buffer=new byte[1024*1024]; int n;
              while((n=input.Read(buffer,0,buffer.Length))>0) { if(firstByte<0) firstByte=downloadClock.ElapsedMilliseconds; output.Write(buffer,0,n); total+=n; }
            }
          }
          downloadClock.Stop();
          if(total<=0 || total!=expected || new FileInfo(zip).Length!=total) throw new IOException("response length mismatch: received="+total+", expected="+expected);
        Interlocked.Add(ref ReservedBytes,-expected); reservation-=expected;
        long expandedBytes=0;
        using(var archiveFile=File.OpenRead(zip)) using(var archive=new ZipArchive(archiveFile,ZipArchiveMode.Read))
          foreach(var entry in archive.Entries) { if(entry.Length>long.MaxValue-expandedBytes) throw new IOException("archive expansion size overflow"); expandedBytes+=entry.Length; }
        Interlocked.Add(ref ReservedBytes,expandedBytes); reservation+=expandedBytes;
        var outputDrive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)));
        if(Interlocked.Read(ref ReservedBytes)+256L*1024*1024>outputDrive.AvailableFreeSpace) throw new IOException("not enough free space to extract concurrent packages; reduce Clients or choose another DownloadDirectory");
          var extractClock=System.Diagnostics.Stopwatch.StartNew();
        ExtractZip(zip,extracted); extractClock.Stop();
          if(!Directory.Exists(extracted) || !Directory.GetFiles(extracted,"*.inf",SearchOption.AllDirectories).Any()) throw new IOException("client extraction did not produce an INF");
          return new UdaPhysicalClientStat { Bytes=total, FirstByteMs=firstByte, DownloadMs=downloadClock.ElapsedMilliseconds, ExtractMs=extractClock.ElapsedMilliseconds,
            MiBPerSecond=(total/1048576.0)/Math.Max(0.001,downloadClock.Elapsed.TotalSeconds) };
        } finally {
          if(reservation>0) Interlocked.Add(ref ReservedBytes,-reservation);
          try { if(File.Exists(zip)) File.Delete(zip); } catch { }
          try { if(Directory.Exists(extracted)) Directory.Delete(extracted,true); } catch { }
        }
      },TaskCreationOptions.LongRunning);
    }
    try { if(!Task.WaitAll(tasks,timeoutMs)) throw new TimeoutException("one or more downloads exceeded the benchmark timeout"); return tasks.Select(t=>t.Result).ToArray(); }
    catch(AggregateException ex) { throw ex.Flatten().InnerExceptions[0]; }
  }
  private static void ExtractZip(string zipPath,string destination) {
    try {
      if(Directory.Exists(destination)) Directory.Delete(destination,true);
      Directory.CreateDirectory(destination); string root=Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
      using(var input=File.OpenRead(zipPath)) using(var archive=new ZipArchive(input,ZipArchiveMode.Read))
        foreach(var entry in archive.Entries) {
          string path=Path.GetFullPath(Path.Combine(destination,entry.FullName.Replace('/',Path.DirectorySeparatorChar)));
          if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase)) throw new IOException("archive path traversal rejected");
          if(entry.FullName.EndsWith("/",StringComparison.Ordinal)) { Directory.CreateDirectory(path); continue; }
          Directory.CreateDirectory(Path.GetDirectoryName(path)); using(var source=entry.Open()) using(var output=File.Create(path)) source.CopyTo(output,1024*1024);
        }
    } finally { try { if(File.Exists(zipPath)) File.Delete(zipPath); } catch { } }
  }
}
'@
try {
  $watch=[Diagnostics.Stopwatch]::StartNew()
  $stats=[UdaPhysicalTransferBenchmark]::Run($downloadUrl,$token,$Clients,$DownloadDirectory,180000)
  $watch.Stop()
  $rates=@($stats|ForEach-Object {$_.MiBPerSecond}|Sort-Object)
  $first=@($stats|ForEach-Object {$_.FirstByteMs}|Sort-Object)
  $extract=@($stats|ForEach-Object {$_.ExtractMs}|Sort-Object)
  $idx=[int][Math]::Ceiling($Clients*0.95)-1
  $medianRate=[Math]::Round(($rates[[int][Math]::Floor(($Clients-1)/2)]+$rates[[int][Math]::Ceiling(($Clients-1)/2)])/2,2)
  $p95First=[long]$first[$idx];$p95Extract=[long]$extract[$idx]
  if($p95First -gt 3000){throw "P95 time to first byte exceeded the 3-second target: ${p95First}ms"}
  if($p95Extract -gt 3000){throw "P95 client extraction exceeded the 3-second target: ${p95Extract}ms"}
  $fairness=[Math]::Round($rates[0]/[Math]::Max(0.01,$medianRate),2)
  $totalMiB=($stats|Measure-Object -Property Bytes -Sum).Sum/1048576.0
  $aggregate=[Math]::Round($totalMiB/$watch.Elapsed.TotalSeconds,2)
  [pscustomobject]@{
    Server=$base;InfPath=$InfPath;Clients=$Clients;PackageMiB=[Math]::Round($stats[0].Bytes/1048576.0,2)
    ClientDownloadMiBpsMin=[Math]::Round($rates[0],2);ClientDownloadMiBpsMedian=$medianRate;ClientDownloadMiBpsMax=[Math]::Round($rates[-1],2)
    ClientMinToMedianFairness=$fairness
    FirstByteMsMedian=[long]$first[[int][Math]::Floor(($Clients-1)/2)];FirstByteMsP95=$p95First
    ExtractionMsMedian=[long]$extract[[int][Math]::Floor(($Clients-1)/2)];ExtractionMsP95=$p95Extract
    EndToEndAggregateMiBps=$aggregate;Completed=$stats.Count;DownloadDirectory=$DownloadDirectory
  } | Format-List
} finally { $token=$null; if($ownsSecureToken){$secureToken.Dispose()} }
