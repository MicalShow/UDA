param([string]$OutputDirectory = 'web-console-live-catalog', [switch]$ImportSignedPackage)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory + '\Lan\UDA-Server-Manager.exe')))
$token = [Threading.CancellationToken]::None
$downloads = [UDA.OnlineDriverCatalog]::Search('PCI\VEN_10EC&DEV_8168',19045,$token)
if ($downloads.Count -eq 0) { throw 'Live official catalog returned no driver downloads' }
foreach ($driver in $downloads) {
    if ($driver['sha256'] -notmatch '^[a-f0-9]{64}$' -or -not ([Uri]$driver['url']).Host.EndsWith('.download.windowsupdate.com')) { throw 'Unverified source metadata' }
}
Write-Output ('PASS: live official catalog search and download manifests; candidates=' + $downloads.Count)
if ($ImportSignedPackage) {
    $root = Join-Path $PSScriptRoot ($OutputDirectory + '\live-import-' + [Guid]::NewGuid().ToString('N'))
    $config = New-Object UDA.LanServerConfig; $config.RepoRoot = Join-Path $root 'repo'
    New-Item -ItemType Directory -Path $config.RepoRoot -Force | Out-Null
    $controller = New-Object UDA.WebAdminController($config,([Func[UDA.OfflineRepo]]{return $null}))
    $arguments = New-Object 'System.Collections.Generic.Dictionary[string,object]'
    $arguments.Add('url',$downloads[0]['url']); $arguments.Add('sha256',$downloads[0]['sha256']); $arguments.Add('hardwareId','PCI\VEN_10EC&DEV_8168'); $arguments.Add('architecture','amd64'); $arguments.Add('build',19045)
    $result = $controller.RunAiTask('update',$arguments,$token,[Action[string]]{param($message)})
    $manifest = [UDA.OfflineRepoGenerator]::TryLoadManifest($config.RepoRoot)
    if ($null -eq $manifest -or $manifest.Entries.Count -eq 0 -or @($manifest.Entries | Where-Object SignatureStatus -ne 'trusted').Count -gt 0) { throw ('Live import is unsigned or empty: ' + $result) }
    $generator = New-Object UDA.OfflineRepoGenerator((New-Object UDA.WinTrustSignatureVerifier),$null)
    if ($generator.Verify($config.RepoRoot,$token).Count -ne 0) { throw 'Imported repository integrity failed' }
    Write-Output ('PASS: real HTTPS driver archive, source SHA256, CAB extraction, trusted catalog, hardware/OS applicability and incremental import: ' + $manifest.Entries.Count + ' INF')
}
