param([string]$OutputDirectory='release-v5.0.2',[string]$DriverZip='C:\Users\Administrator\Desktop\ibtusb.inf_amd64_167bc46a8547c07f.zip')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Web.Extensions
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory+'\Lan\UDA-Server-Manager.exe')))
$root=Join-Path $PSScriptRoot ($OutputDirectory+'\subtree-'+[Guid]::NewGuid().ToString('N'))
$source=Join-Path $root 'source';New-Item -ItemType Directory -Path $source -Force|Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory($DriverZip,$source)
$inf=Get-ChildItem -LiteralPath $source -Filter ibtusb.inf -Recurse|Select-Object -First 1
$token=[Threading.CancellationToken]::None
$selected=[UDA.OfflineRepoGenerator]::DownloadPackageRoot($source,$inf.FullName,$token)
if($selected-ne $inf.DirectoryName){throw 'Self-contained Intel package was widened'}
$shared=Join-Path $PSScriptRoot 'test-fixtures\shared-package'
if([UDA.OfflineRepoGenerator]::DownloadPackageRoot($shared,(Join-Path $shared 'sub\driver.inf'),$token)-ne $shared){throw 'Parent dependency was dropped'}
$repo=Join-Path $root 'repo';$generator=New-Object UDA.OfflineRepoGenerator($null,$null)
[void]$generator.Generate($repo,[string[]]@($selected,(Join-Path $PSScriptRoot 'test-fixtures\organized-v1')),(New-Object UDA.OsTarget),$null,'test',$token,$null)
$manifest=[UDA.OfflineRepoGenerator]::TryLoadManifest($repo)
$entry=$manifest.Entries|Where-Object RelativePath -like '*/ibtusb.inf'|Select-Object -First 1
$entry.PackageRoot='packages'
$manifest.IntegrityHash=[UDA.OfflineRepoGenerator]::ComputeIntegrityHash($manifest)
[IO.File]::WriteAllText((Join-Path $repo 'repo.manifest.json'),[UDA.OfflineRepoGenerator]::SerializeManifest($manifest))
$cfg=New-Object UDA.LanServerConfig;$cfg.RepoRoot=$repo;$cfg.Host='localhost'
$tcp=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0);$tcp.Start();$cfg.Port=$tcp.LocalEndpoint.Port;$tcp.Stop()
$server=New-Object UDA.LanDriverServer($cfg,$null)
try {
 $server.Start();$json=New-Object System.Web.Script.Serialization.JavaScriptSerializer
 for($i=0;$i-lt 100;$i++){$health=Invoke-WebRequest -UseBasicParsing -Uri ('http://localhost:'+$cfg.Port+'/api/health');if($json.DeserializeObject($health.Content).RepoLoaded){break};Start-Sleep -Milliseconds 100}
 $zipPath=Join-Path $root 'download.zip'
 Invoke-WebRequest -UseBasicParsing -Uri ('http://localhost:'+$cfg.Port+'/api/download?path='+[Uri]::EscapeDataString($entry.RelativePath)) -Headers @{'X-UDA-Protocol'='1'} -OutFile $zipPath
 $zip=[IO.Compression.ZipFile]::OpenRead($zipPath)
 try {
  if($zip.Entries.Count-ne 4 -or @($zip.Entries|Where-Object FullName -match 'driver\.(inf|sys)$').Count-ne 0){throw 'Unrelated driver included in HTTP ZIP'}
  $expected=$entry.RelativePath.Substring('packages/'.Length)
  $item=$zip.GetEntry($expected);if($null-eq $item){throw 'Historical PackageInfPath changed'}
  $sha=[Security.Cryptography.SHA256]::Create();$stream=$item.Open()
  try{$hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$stream.Dispose();$sha.Dispose()}
  if($hash-ne $entry.ContentHash){throw 'Intel INF changed during export'}
  Write-Output ('PASS: real Intel package HTTP export only 4 files; bytes='+(Get-Item -LiteralPath $zipPath).Length+'; exact historical ZIP path and INF SHA256 preserved')
 }finally{$zip.Dispose()}
 Write-Output 'PASS: parent-directory shared dependency keeps complete subtree'
}finally{$server.Dispose()}
