[CmdletBinding()]
param([string]$Repository='D:\Drvcenter-UDA-Repository',[string]$SourceRepository,[string]$ServerDirectory=(Join-Path $env:ProgramFiles 'UniversalDriverAssistantServer'),[switch]$ScanOnly,[string]$ReportPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$exe=Join-Path $ServerDirectory 'OfflineRepoGenerator.exe'
if(!(Test-Path -LiteralPath $exe)){throw "Missing server executable: $exe"}
[void][Reflection.Assembly]::LoadFrom($exe)
[UDA.ServerStorage]::UseProviderDirectory((Join-Path $ServerDirectory 'DatabaseProvider'))
$Repository=[IO.Path]::GetFullPath($Repository)
$manifest=[UDA.OfflineRepoGenerator]::TryLoadManifest($Repository)
if(!$manifest){throw 'Cannot read repository catalog. Check the local database configuration.'}
if($manifest.IntegrityHash -ne [UDA.OfflineRepoGenerator]::ComputeIntegrityHash($manifest)){throw 'Catalog integrity check failed'}
$groups=@{}
foreach($entry in $manifest.Entries){
 $path=[UDA.OfflineRepoGenerator]::DownloadArchivePath($Repository,$entry)
 if(!$path){throw ('Invalid catalog entry: '+$entry.RelativePath)}
 if(!(Test-Path -LiteralPath $path -PathType Leaf)){
  $name=[IO.Path]::GetFileName($path)
  if($name -notmatch '^[a-f0-9]{64}\.zip$'){throw 'Invalid archive name'}
  if(!$groups.ContainsKey($name)){$groups[$name]=New-Object 'Collections.Generic.List[UDA.OfflineRepoEntry]'}
  $groups[$name].Add($entry)
 }
}
if(!$ReportPath){$ReportPath=Join-Path ([Environment]::GetFolderPath('Desktop')) ('UDA-MissingArchives-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.csv')}
$rows=@($groups.Keys | Sort-Object | ForEach-Object {[pscustomobject]@{Archive=$_;Drivers=(($groups[$_] | ForEach-Object {$_.RelativePath}) -join ' | ')}})
$rows | Export-Csv -LiteralPath $ReportPath -Encoding UTF8 -NoTypeInformation
Write-Host ("Missing archives: {0}. Report: {1}" -f $groups.Count,$ReportPath)
if($ScanOnly -or !$groups.Count){return}
if(!$SourceRepository){$SourceRepository=Read-Host 'Enter the original repository folder (local path or an accessible network share)'}
if([string]::IsNullOrWhiteSpace($SourceRepository)){throw 'No source provided. Missing archive report has been saved.'}
$sourceFolder=Join-Path ([IO.Path]::GetFullPath($SourceRepository)) '.uda-downloads'
$targetFolder=Join-Path $Repository '.uda-downloads'
[void][IO.Directory]::CreateDirectory($targetFolder)
$copied=0;$failed=0;$totalBytes=0L;$index=0
foreach($name in ($groups.Keys | Sort-Object)){
 $index++;$source=Join-Path $sourceFolder $name;$destination=Join-Path $targetFolder $name
 if(Test-Path -LiteralPath $destination -PathType Leaf){continue}
 $temporary=$destination+'.repair-'+[Guid]::NewGuid().ToString('N')
 try {
  if(!(Test-Path -LiteralPath $source -PathType Leaf)){throw "Source archive missing: $source"}
  $size=(Get-Item -LiteralPath $source).Length
  Write-Host ("[{0}/{1}] {2}, {3:N1} MiB; {4}" -f $index,$groups.Count,$name,($size/1MB),$groups[$name][0].RelativePath)
  $inputFile=[IO.File]::Open($source,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
  try {
   $outputFile=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
   try {
    $buffer=New-Object byte[] (1MB);$done=0L
    while(($read=$inputFile.Read($buffer,0,$buffer.Length)) -gt 0){$outputFile.Write($buffer,0,$read);$done+=$read;Write-Progress -Activity "Restore archives $index/$($groups.Count)" -Status $name -PercentComplete ([int](100*$done/[Math]::Max(1,$size)))}
    $outputFile.Flush($true)
   }finally{$outputFile.Dispose()}
   $inputFile.Position=0;$sha=[Security.Cryptography.SHA256]::Create()
   try{$sourceHash=[BitConverter]::ToString($sha.ComputeHash($inputFile)).Replace('-','')}finally{$sha.Dispose()}
  }finally{$inputFile.Dispose()}
  if((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $sourceHash){throw 'Copied archive hash mismatch'}
  $zip=[IO.Compression.ZipFile]::OpenRead($temporary)
  try {
   foreach($entry in $groups[$name]){
    $parent=$entry.PackageRoot
    if(!$parent){$parent=[IO.Path]::GetDirectoryName($entry.RelativePath).Replace('\','/')}
    $memberName=$entry.RelativePath.Substring($parent.Length+1).Replace('\','/')
    $member=$zip.GetEntry($memberName)
    if(!$member){throw "INF missing in archive: $memberName"}
    $stream=$member.Open();$sha=[Security.Cryptography.SHA256]::Create()
    try{$hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()}
    if($hash -ne $entry.ContentHash){throw "INF hash mismatch: $memberName"}
   }
  }finally{$zip.Dispose()}
  if(!(Test-Path -LiteralPath $destination)){[IO.File]::Move($temporary,$destination);$copied++;$totalBytes+=$size}
 }catch{$failed++;Write-Warning ($name+': '+$_.Exception.Message)}
 finally{if(Test-Path -LiteralPath $temporary){[IO.File]::Delete($temporary)}}
}
Write-Progress -Activity 'Restore archives' -Completed
Write-Host ("Copied: {0}; {1:N1} MiB; failed: {2}. Database and existing archives unchanged." -f $copied,($totalBytes/1MB),$failed)
if($failed){throw 'Some archives could not be restored. See individual errors above.'}
