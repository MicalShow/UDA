param([string]$OutputDirectory = 'web-console-ui')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Web.Extensions
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory + '\Lan\UDA-Server-Manager.exe')))
Add-Type -TypeDefinition @'
using System; using System.Net; using System.Text; using System.Threading;
public sealed class UdaMockAi : IDisposable {
  readonly HttpListener listener = new HttpListener(); readonly Thread thread;
  public string ChatResponse, SeenAuthorization, SeenApiKey, SeenVersion, SeenPath, SeenBody; public int Requests;
  public UdaMockAi(int port) { listener.Prefixes.Add("http://localhost:"+port+"/"); listener.Start(); thread = new Thread(Loop); thread.IsBackground=true; thread.Start(); }
  void Loop() { while(listener.IsListening) { HttpListenerContext c; try { c=listener.GetContext(); } catch { return; }
    try { SeenAuthorization=c.Request.Headers["Authorization"]; SeenApiKey=c.Request.Headers["x-api-key"]; SeenVersion=c.Request.Headers["anthropic-version"]; SeenPath=c.Request.Url.AbsolutePath; using(var reader=new System.IO.StreamReader(c.Request.InputStream)) SeenBody=reader.ReadToEnd(); Interlocked.Increment(ref Requests); string body=c.Request.Url.AbsolutePath.EndsWith("/models") ? "{\"data\":[{\"id\":\"mock-model\"}]}" : ChatResponse; byte[] b=Encoding.UTF8.GetBytes(body); c.Response.ContentType="application/json"; c.Response.ContentLength64=b.Length; c.Response.OutputStream.Write(b,0,b.Length); } finally { c.Response.Close(); }
  }} public void Dispose(){listener.Close();thread.Join(1000);}
}
'@
$json = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$root = Join-Path $PSScriptRoot ($OutputDirectory + '\ai-telemetry-test-' + [Guid]::NewGuid().ToString('N'))
$repo = Join-Path $root 'repo'; $source = Join-Path $root 'source'
New-Item -ItemType Directory -Path $source -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'test-fixtures\organized-v1\driver.inf') -Destination $source
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'test-fixtures\organized-v1\driver.sys') -Destination $source
$generator = New-Object UDA.OfflineRepoGenerator((New-Object UDA.TrustingSignatureVerifier),$null)
$token = [Threading.CancellationToken]::None
[void]$generator.AppendOrganizedSource($repo,$source,$token,$null)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'test-fixtures\organized-v2\driver.inf') -Destination (Join-Path $source 'driver.inf') -Force
[void]$generator.AppendOrganizedSource($repo,$source,$token,$null)
$manifest = [UDA.OfflineRepoGenerator]::TryLoadManifest($repo)
$tcp = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0); $tcp.Start(); $port = $tcp.LocalEndpoint.Port; $tcp.Stop()
$mock = New-Object UdaMockAi($port)
try {
  $config = New-Object UDA.AiSubscription
  $config.Name = 'mock'; $config.BaseUrl = 'http://localhost:' + $port + '/v1'; $config.Model = 'mock-model'
  [UDA.AiSubscriptionStore]::Save($repo,$config,'UDA-TEST-KEY')
  if ([UDA.AiSubscriptionStore]::Models($repo)[0] -ne 'mock-model') { throw 'Model discovery failed' }
  $categories = @{}; foreach ($entry in $manifest.Entries) { $categories[$entry.PackageHash] = [UDA.OfflineRepoGenerator]::ClassifyPackage([UDA.InfRecord[]]@([UDA.InfParser]::Parse((Join-Path $repo $entry.RelativePath),'test'))) }
  $mock.ChatResponse = $json.Serialize(@{ choices = @(@{ message = @{ content = $json.Serialize(@{ categories = $categories; command = 'must-never-execute' }) } }) })
  $serverConfig = New-Object UDA.LanServerConfig; $serverConfig.RepoRoot = $repo
  $controller = New-Object UDA.WebAdminController($serverConfig,([Func[UDA.OfflineRepo]]{ return $null }))
  $argsMap = New-Object 'System.Collections.Generic.Dictionary[string,object]'
  $argsMap.Add('build',19045); $argsMap.Add('architecture','amd64'); $argsMap.Add('hardwareId','PCI\VEN_1234&DEV_5678')
  [void]$controller.RunAiTask('classify',$argsMap,$token,[Action[string]]{param($message)})
  if (-not (Test-Path -LiteralPath (Join-Path $repo 'repo.category-overrides.json'))) { throw 'AI classification not applied' }
  $stable = ($manifest.Entries | Where-Object DriverVerVersion -eq '1.0.0.0').ContentHash
  $mock.ChatResponse = $json.Serialize(@{ choices = @(@{ message = @{ content = $json.Serialize(@{ stableHash = $stable; reason = 'synthetic test recommendation' }) } }) })
  [void]$controller.RunAiTask('releases',$argsMap,$token,[Action[string]]{param($message)})
  $policies = [UDA.DriverReleasePolicies]::Load($repo)
  if ($policies.Count -ne 1 -or $policies[0].StableHash -ne $stable -or $policies[0].StableHash -eq $policies[0].LatestHash) { throw 'AI version selection failed' }
  if ($mock.SeenAuthorization -ne 'Bearer UDA-TEST-KEY') { throw 'Encrypted key was not decrypted for authenticated API request' }
  if ([UDA.WebAdminController]::IsAllowedDownload([Uri]'https://download.microsoft.com.evil.invalid/a.zip','download.microsoft.com')) { throw 'Download host suffix bypass' }
  if ([UDA.WebAdminController]::IsAllowedDownload([Uri]'http://download.microsoft.com/a.zip','download.microsoft.com')) { throw 'Insecure download accepted' }
  Write-Output 'PASS: mock-compatible model discovery/chat, encrypted authentication, AI classification, stable/latest policies, download source validation'
  $config.Protocol = 'anthropic'
  [UDA.AiSubscriptionStore]::Save($repo,$config,'')
  $mock.ChatResponse = $json.Serialize(@{ content = @(@{ type = 'text'; text = '{"answer":"verified"}' }) })
  $answer = [UDA.AiSubscriptionStore]::ChatJson($repo,'Return JSON',@{test='anthropic'},$token)
  if ($answer['answer'] -ne 'verified' -or $mock.SeenPath -ne '/v1/messages' -or $mock.SeenVersion -ne '2023-06-01' -or $mock.SeenApiKey -ne 'UDA-TEST-KEY') { throw 'Anthropic-compatible request/response failed' }
  $sent = $json.DeserializeObject($mock.SeenBody)
  if (-not $sent['max_tokens'] -or -not $sent['system']) { throw 'Anthropic request schema invalid' }
  Write-Output 'PASS: Anthropic-compatible messages, API key/version headers, system prompt, max_tokens and content-block response'
  $config.MaximumRequestsPerJob = 1; $config.MaximumOutputTokens = 256
  [UDA.AiSubscriptionStore]::Save($repo,$config,'')
  $budget = [UDA.AiSubscriptionStore]::EnterTaskBudget($repo)
  try {
    [void][UDA.AiSubscriptionStore]::ChatJson($repo,'Return JSON',@{test='budget'},$token)
    $before = $mock.Requests; $rejected = $false
    try { [void][UDA.AiSubscriptionStore]::ChatJson($repo,'Return JSON',@{test='over-budget'},$token) }
    catch { $rejected = $_.Exception.ToString().Contains('RequestBudgetExceededException') }
    if (-not $rejected -or $mock.Requests -ne $before) { throw 'Request budget did not prevent outbound call' }
    if ($json.DeserializeObject($mock.SeenBody)['max_tokens'] -ne 256) { throw 'Output budget was not sent' }
  } finally { $budget.Dispose() }
  $config.MaximumRequestsPerJob = 0; $invalid = $false
  try { [UDA.AiSubscriptionStore]::Save($repo,$config,'') } catch { $invalid = $true }
  if (-not $invalid) { throw 'Invalid AI budget accepted' }
  Write-Output 'PASS: per-task request budget, output token budget and invalid configuration rejection'
} finally { $mock.Dispose() }
$queue = Join-Path $root 'queue'
$event = New-Object UDA.DriverInstallEvent
$event.EventId = [Guid]::NewGuid().ToString('N'); $event.RunId = [Guid]::NewGuid().ToString('N'); $event.Detail = 'offline test'
[UDA.ClientTelemetryQueue]::Enqueue($queue,$event)
[UDA.ClientTelemetryQueue]::Enqueue($queue,$event)
if ([UDA.ClientTelemetryQueue]::Flush($queue,[Func[UDA.DriverInstallEvent,bool]]{param($e) return $false},100) -ne 0) { throw 'Failed send was discarded' }
if (@(Get-ChildItem -LiteralPath $queue -Filter '*.json').Count -ne 1) { throw 'Offline event lost or duplicated' }
if ([UDA.ClientTelemetryQueue]::Flush($queue,[Func[UDA.DriverInstallEvent,bool]]{param($e) return $true},100) -ne 1) { throw 'Retry did not send event' }
if (@(Get-ChildItem -LiteralPath $queue -Filter '*.json').Count -ne 0) { throw 'Acknowledged event not cleared' }
Write-Output 'PASS: durable offline event queue, enqueue deduplication, failed-send retention, retry and acknowledged cleanup'
