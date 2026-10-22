param([string]$OutputDirectory = 'web-console-live-catalog')
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::DefaultConnectionLimit = 128
Add-Type -AssemblyName System.Web.Extensions
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory + '\Lan\UDA-Server-Manager.exe')))
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
public sealed class UdaDownloadStat { public double RateMiB; public long FirstByteMs; public long Bytes; public long ExtractMs; }
public static class UdaSlowDownloadFixture {
  static byte[] Bytes;
  public static string LastError = "";
  public static Func<string,byte[]> Create() { return Create(32 * 1024 * 1024); }
  public static Func<string,byte[]> Create(int size) { Bytes = new byte[size]; return path => Bytes; }
  public static UdaDownloadStat[] ConcurrentFetch(string url, string token, int count, long sizeBytes, int payloadBytes, int timeoutMs, string destination) {
    var tasks = new Task<UdaDownloadStat>[count];
    for (int i = 0; i < count; i++) tasks[i] = Task.Factory.StartNew(() => {
      var request = (HttpWebRequest)WebRequest.Create(url); request.Proxy = null; request.Timeout = timeoutMs; request.ReadWriteTimeout = timeoutMs;
      request.Headers["X-UDA-Protocol"] = "2"; request.Headers["X-UDA-Token"] = token;
      var clock = System.Diagnostics.Stopwatch.StartNew(); long firstByte = -1, total = 0, expectedLength = -1; byte[] buffer = new byte[1024 * 1024]; int n; HttpStatusCode status;
      string outputPath = Path.Combine(destination, Guid.NewGuid().ToString("N") + ".zip");
      using (var response = (HttpWebResponse)request.GetResponse()) using (var input = response.GetResponseStream())
      using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan)) {
        status = response.StatusCode; expectedLength = response.ContentLength;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0) { if (firstByte < 0) firstByte = clock.ElapsedMilliseconds; output.Write(buffer, 0, n); total += n; }
      }
      clock.Stop();
      if (status != HttpStatusCode.OK || total != sizeBytes || total != expectedLength || total <= 0) throw new IOException("wrong download status/length: status="+status+", bytes="+total+", expected="+sizeBytes+", contentLength="+expectedLength);
      var info = new FileInfo(outputPath); if (info.Length != sizeBytes) throw new IOException("client file length mismatch");
      string extractDir = outputPath + ".expanded"; var extractClock = System.Diagnostics.Stopwatch.StartNew();
      var clientType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UDA.LanDriverClient", false)).FirstOrDefault(t => t != null);
      var extractor = clientType == null ? null : clientType.GetMethod("ExtractZipFile", BindingFlags.NonPublic | BindingFlags.Static);
      if (extractor == null) throw new MissingMethodException("LanDriverClient.ExtractZipFile");
      extractor.Invoke(null, new object[] { outputPath, extractDir, CancellationToken.None }); extractClock.Stop();
      string payload = Path.Combine(extractDir, "bulk.sys");
      if (!File.Exists(payload) || new FileInfo(payload).Length != payloadBytes) throw new IOException("client package extraction verification failed");
      return new UdaDownloadStat { Bytes = total, FirstByteMs = firstByte, RateMiB = (total / 1048576.0) / Math.Max(0.001, clock.Elapsed.TotalSeconds), ExtractMs = extractClock.ElapsedMilliseconds };
    });
    try { if (!Task.WaitAll(tasks, timeoutMs)) { LastError = "concurrent timeout"; return null; } return tasks.Select(t => t.Result).ToArray(); }
    catch (Exception ex) { LastError = ex.ToString(); return null; }
  }
}
'@
$json = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$root = Join-Path $PSScriptRoot ($OutputDirectory + '\concurrency-' + [Guid]::NewGuid().ToString('N'))
$cfg = New-Object UDA.LanServerConfig; $cfg.RepoRoot = Join-Path $root 'repo'; $cfg.Host='localhost'; $cfg.Token='concurrency-test'
$generator = New-Object UDA.OfflineRepoGenerator($null,$null)
$bulkSource=Join-Path $root 'bulk-package';[IO.Directory]::CreateDirectory($bulkSource)|Out-Null
$bulkInf=@'
[Version]
Signature="$Windows NT$"
Class=Net
ClassGuid={4d36e972-e325-11ce-bfc1-08002be10318}
Provider=%P%
DriverVer=03/14/2026,2.0.0.0
[Manufacturer]
%P%=M,NTamd64.10
[M.NTamd64.10]
%D%=Install,PCI\VEN_C1A0&DEV_0001
[Install]
'@
[IO.File]::WriteAllText((Join-Path $bulkSource 'bulk.inf'),$bulkInf,(New-Object Text.UTF8Encoding($false)))
$bulkPayload=New-Object byte[] (8*1024*1024);(New-Object Random 481516).NextBytes($bulkPayload);[IO.File]::WriteAllBytes((Join-Path $bulkSource 'bulk.sys'),$bulkPayload)
[void]$generator.Generate($cfg.RepoRoot,[string[]]@((Join-Path $PSScriptRoot 'test-fixtures\organized-v1'),$bulkSource),(New-Object UDA.OsTarget),$null,'test',[Threading.CancellationToken]::None,$null)
$manifest = [UDA.OfflineRepoGenerator]::TryLoadManifest($cfg.RepoRoot); $bulkEntry=@($manifest.Entries|Where-Object {$_.RelativePath -like '*bulk.inf'})[0]; if($null -eq $bulkEntry){throw 'Synthetic large package was not indexed'}; $oldPath = $bulkEntry.RelativePath
$downloadDirectory=Join-Path $root 'client-downloads';[IO.Directory]::CreateDirectory($downloadDirectory)|Out-Null
$tcp = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0); $tcp.Start(); $cfg.Port=$tcp.LocalEndpoint.Port; $tcp.Stop()
$baseUrl='http://localhost:'+$cfg.Port
$server = New-Object UDA.LanDriverServer($cfg,$null)
function Request($path,$body) {
  $request=[Net.HttpWebRequest]::Create($baseUrl+$path); $request.Proxy=$null; $request.Timeout=5000; $request.ReadWriteTimeout=5000
  $protocol=if($path.StartsWith('/api/admin/upload/',[StringComparison]::OrdinalIgnoreCase)){'1'}else{[UDA.LanProtocol]::Version.ToString([Globalization.CultureInfo]::InvariantCulture)}
  $request.Headers['X-UDA-Protocol']=$protocol; $request.Headers['X-UDA-Token']=$cfg.Token
  if($null -ne $body){$request.Method='POST';$bytes=[Text.Encoding]::UTF8.GetBytes($body);$request.ContentLength=$bytes.Length; $s=$request.GetRequestStream();$s.Write($bytes,0,$bytes.Length);$s.Dispose()}
  try{$response=$request.GetResponse()}catch [Net.WebException]{if(-not $_.Exception.Response){throw};$response=$_.Exception.Response}
  try{$reader=New-Object IO.StreamReader($response.GetResponseStream());return @{Status=[int]$response.StatusCode;Body=$reader.ReadToEnd()}}finally{$response.Dispose()}
}
try {
  $server.Start()
  for($i=0;$i -lt 50;$i++){ $health=$json.DeserializeObject((Request '/api/health' $null).Body);if($health.RepoLoaded){break};Start-Sleep -Milliseconds 100 }
  $batch=$json.DeserializeObject((Request '/api/admin/upload/start' '').Body)
  $slow=[Net.HttpWebRequest]::Create($baseUrl+'/api/admin/upload/file?batch='+$batch.batch+'&path=slow.bin')
  $slow.Proxy=$null;$slow.Method='POST';$slow.AllowWriteStreamBuffering=$false;$slow.ContentLength=65536;$slow.Headers['X-UDA-Protocol']='1';$slow.Headers['X-UDA-Token']=$cfg.Token
  $stream=$slow.GetRequestStream();$stream.Write((New-Object byte[] 1024),0,1024);$stream.Flush()
  $watch=[Diagnostics.Stopwatch]::StartNew();$health=Request '/api/health' $null
  if($health.Status -ne 200 -or $watch.ElapsedMilliseconds -gt 2500){throw 'Slow upload blocked HTTP health/control plane'}
  $slow.Abort();try{$stream.Dispose()}catch{}
  Write-Output 'PASS: incomplete slow upload does not block health/admin requests'
  $archivePath=[UDA.OfflineRepoGenerator]::DownloadArchivePath($cfg.RepoRoot,$bulkEntry);if(-not (Test-Path -LiteralPath $archivePath)){throw 'Server did not prebuild the synthetic driver archive'}
  $perClientBytes=(Get-Item -LiteralPath $archivePath).Length; $clientCount=40
  $server.TestDownloadOverride=$null
  $parallelUrl=$baseUrl+'/api/download?path='+[Uri]::EscapeDataString($oldPath)
  $parallelWatch=[Diagnostics.Stopwatch]::StartNew()
  $clientRates=[UdaSlowDownloadFixture]::ConcurrentFetch($parallelUrl,$cfg.Token,$clientCount,$perClientBytes,(8*1024*1024),60000,$downloadDirectory)
  if($null -eq $clientRates -or $clientRates.Length -ne $clientCount){throw ('40 concurrent direct downloads did not all complete with correct lengths: '+[UdaSlowDownloadFixture]::LastError)}
  $parallelWatch.Stop();$aggregateMbps=[Math]::Round((($clientCount*$perClientBytes/1048576.0)/$parallelWatch.Elapsed.TotalSeconds),1)
  $sortedRates=@($clientRates | ForEach-Object {$_.RateMiB} | Sort-Object);$sortedFirstByte=@($clientRates | ForEach-Object {$_.FirstByteMs} | Sort-Object);$sortedExtract=@($clientRates | ForEach-Object {$_.ExtractMs} | Sort-Object)
  $minimumRate=[Math]::Round($sortedRates[0],1);$medianRate=[Math]::Round(($sortedRates[19]+$sortedRates[20])/2,1);$maximumRate=[Math]::Round($sortedRates[-1],1)
  $fairness=[Math]::Round($sortedRates[0]/[Math]::Max(0.1,(($sortedRates[19]+$sortedRates[20])/2)),2)
  $medianFirstByte=$sortedFirstByte[19];$p95Index=[int][Math]::Ceiling($clientCount*0.95)-1;$p95Index=[Math]::Min($sortedFirstByte.Length-1,$p95Index);$p95FirstByte=$sortedFirstByte[$p95Index];$medianExtract=$sortedExtract[19];$p95Extract=$sortedExtract[$p95Index]
  if($p95FirstByte -gt 3000){throw ('P95 time to first byte exceeded 3 seconds: '+$p95FirstByte+' ms')}
  if($p95Extract -gt 3000){throw ('P95 client package extraction exceeded 3 seconds: '+$p95Extract+' ms')}
  if($fairness -lt 0.35){throw ('Client throughput fairness fell below 0.35 min/median: '+$fairness)}
  Write-Output ('PASS: 40 clients download the prebuilt on-disk ZIP to separate files ('+[Math]::Round($perClientBytes/1048576.0,2)+' MiB each), then extract with the client implementation; transfer MiB/s min/median/max='+$minimumRate+'/'+$medianRate+'/'+$maximumRate+' (min/median='+$fairness+'); first-byte median/p95='+$medianFirstByte+'/'+$p95FirstByte+' ms; extraction median/p95='+$medianExtract+'/'+$p95Extract+' ms; aggregate='+$aggregateMbps+' MiB/s')
  if((Get-ChildItem -LiteralPath $downloadDirectory -Directory -Filter '*.zip.expanded').Count -ne $clientCount){throw 'Not all downloaded client packages were extracted'}
  $server.TestDownloadOverride=[UdaSlowDownloadFixture]::Create()
  $download=[Net.HttpWebRequest]::Create($baseUrl+'/api/download?path='+[Uri]::EscapeDataString($oldPath));$download.Proxy=$null;$download.Headers['X-UDA-Protocol']=[UDA.LanProtocol]::Version.ToString([Globalization.CultureInfo]::InvariantCulture);$download.Headers['X-UDA-Token']=$cfg.Token
  $response=$download.GetResponse();$input=$response.GetResponseStream();[void]$input.ReadByte();Start-Sleep -Milliseconds 150
  $accepted=Request '/api/admin/organize' ''
  if($accepted.Status -ne 202){throw ('Organize not accepted: '+$accepted.Body)}
  $status=$json.DeserializeObject((Request '/api/admin/import-status' $null).Body)
  if(-not $status.running){throw 'Mutation did not wait for active download'}
  $watch.Restart();if((Request '/api/health' $null).Status -ne 200 -or $watch.ElapsedMilliseconds -gt 2500){throw 'Pending mutation blocked control plane'}
  $download.Abort();$input.Dispose();$response.Dispose();$server.TestDownloadOverride=$null
  for($i=0;$i -lt 100;$i++){ $status=$json.DeserializeObject((Request '/api/admin/import-status' $null).Body);if(-not $status.running){break};Start-Sleep -Milliseconds 100 }
  if($status.stage -ne 'completed'){throw ('Organize failed: '+$status.error)}
  if((Request ('/api/download?path='+[Uri]::EscapeDataString($oldPath)) $null).Status -ne 200){throw 'Old scan path cannot download after organization'}
  Write-Output 'PASS: organization waits for active download, control plane remains responsive, old scan path resolves after hot reload'
}finally{$server.Dispose()}
