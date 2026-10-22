param([string]$OutputDirectory='release-v5.0.8',[string]$DriverDirectory='analysis-amd-20220802\20220802',[switch]$SkipRealImport)
$ErrorActionPreference='Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot ($OutputDirectory+'\Lan\LanDriverService.exe')))
$root=Join-Path $PSScriptRoot ($OutputDirectory+'\amd-parent-test-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$method=[UDA.OfflineRepoGenerator].GetMethod('RequiredPackageRoot',[Reflection.BindingFlags]'Static,NonPublic')
$ct=[Threading.CancellationToken]::None
function Required([string]$inf,[string]$boundary){return $method.Invoke($null,[object[]]@($inf,$boundary))}
function Check($ok,$message){if(-not $ok){throw $message};Write-Output ('PASS: '+$message)}
function Fixture($name,$tail){
    $dir=Join-Path $root $name;[void][IO.Directory]::CreateDirectory($dir)
    $inf=Join-Path $dir 'test.inf'
    $text=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'test-fixtures\organized-v1\driver.inf'))+"`r`n"+$tail
    [IO.File]::WriteAllText($inf,$text);return $inf
}
function Rejected($inf,$boundary,$name){
    $rejected=$false
    try{[void](Required $inf $boundary)}catch{if(($_|Out-String)-match '父级|包内相对路径|无法解析的变量'){$rejected=$true}else{throw}}
    Check $rejected $name
}
$runtime=Fixture 'runtime' @'
[Runtime.AddReg]
HKR,,DalDCELogFilePath,0x00000000,%13%\..\..\..\..\..\Program Files\AMD\atikmdag_dce.log
HKR,,QuotedLog,0,"a; ..\..\log.txt"
[DestinationDirs]
Files=13,..\..\runtime-only
[Service]
ServiceBinary=%12%\..\..\installed.sys
'@
Check ((Required $runtime ([IO.Path]::GetDirectoryName($runtime)))-eq [IO.Path]::GetDirectoryName($runtime)) 'runtime registry/service/destination paths do not expand source boundary'
$inside=Fixture 'inside\sub' @'
[SourceDisksFiles]
shared.dll=1,..\lib
'@
Check ((Required $inside (Join-Path $root 'inside'))-eq (Join-Path $root 'inside')) 'real SourceDisksFiles parent dependency expands within approved root'
$normalized=Fixture 'normalized' @'
[SourceDisksFiles]
driver.sys=1,child\..\bin
'@
Check ((Required $normalized ([IO.Path]::GetDirectoryName($normalized)))-eq [IO.Path]::GetDirectoryName($normalized)) 'normalized child-parent path does not falsely ascend above package'
$variables=Fixture 'variables\sub' @'
[SourceDisksFiles]
driver.sys=1,%SharedPath%
[Strings]
SharedPath="..\lib"
'@
Check ((Required $variables (Join-Path $root 'variables'))-eq (Join-Path $root 'variables')) 'source path string tokens preserve real parent dependency'
foreach($fixture in @(
    @('source-escape',"[SourceDisksFiles]`r`ndriver.sys=1,..\outside"),
    @('disk-escape',"[SourceDisksNames]`r`n1=Disk,,,..\outside"),
    @('copy-direct',"[Install.NT]`r`nCopyFiles=@..\outside.sys"),
    @('copy-list',"[Files]`r`ndestination.sys,..\outside.sys"),
    @('include-escape',"[Install.NT]`r`nInclude=..\outside.inf"),
    @('copyinf-escape',"[Install.NT]`r`nCopyINF=..\outside.inf"),
    @('source-drive',"[SourceDisksFiles]`r`ndriver.sys=1,C:\outside"),
    @('source-unc',"[SourceDisksNames]`r`n1=Disk,,,\\server\outside"),
    @('source-token-escape',"[SourceDisksFiles]`r`ndriver.sys=1,%Unknown%\..\..\outside")
)){
    $inf=Fixture $fixture[0] $fixture[1]
    Rejected $inf ([IO.Path]::GetDirectoryName($inf)) ($fixture[0]+' genuine source escape remains rejected')
}
$shared=Join-Path $PSScriptRoot 'test-fixtures\shared-package'
Check ([UDA.OfflineRepoGenerator]::DownloadPackageRoot($shared,(Join-Path $shared 'sub\driver.inf'),$ct)-eq $shared) 'download keeps real shared sibling DLL dependency'
$amd=if([IO.Path]::IsPathRooted($DriverDirectory)){$DriverDirectory}else{Join-Path $PSScriptRoot $DriverDirectory}
$main=Join-Path $amd 'U0382934.inf'
Check ((Required $main $amd)-eq $amd) 'actual AMD INF registry log path no longer rejects package'
Check ([UDA.OfflineRepoGenerator]::DownloadPackageRoot($amd,$main,$ct)-eq $amd) 'actual AMD package download dependency scope remains complete'
if(-not $SkipRealImport){
    $repo=Join-Path $root 'real-amd-repo'
    $generator=New-Object UDA.OfflineRepoGenerator((New-Object UDA.TrustingSignatureVerifier),$null)
    $first=$generator.AppendOrganizedSource($repo,$amd,$ct,$null)
    $manifest=[UDA.OfflineRepoGenerator]::TryLoadManifest($repo)
    Check ($first.Added-eq 2 -and $manifest.Entries.Count-eq 2) 'actual 1.33 GB AMD source imports both display INFs incrementally'
    $entry=$manifest.Entries|Where-Object {$_.RelativePath.EndsWith('/U0382934.inf')}|Select-Object -First 1
    $target=Join-Path $repo $entry.PackageRoot
    $files=@(Get-ChildItem -LiteralPath $amd -File -Recurse)
    Check (@(Get-ChildItem -LiteralPath $target -File -Recurse).Count-eq $files.Count) 'actual package preserves all 163 source files'
    foreach($file in $files){
        $relative=$file.FullName.Substring($amd.TrimEnd('\').Length).TrimStart('\')
        if((Get-FileHash -LiteralPath $file.FullName).Hash-ne (Get-FileHash -LiteralPath (Join-Path $target $relative)).Hash){throw ('Copied bytes differ: '+$relative)}
    }
    Write-Output 'PASS: every actual AMD package file SHA256 preserved'
    $timestamp=[IO.File]::GetLastWriteTimeUtc((Join-Path $repo $entry.RelativePath))
    $repeat=$generator.AppendOrganizedSource($repo,$amd,$ct,$null)
    Check ($repeat.Added-eq 0 -and $repeat.Unchanged-eq 2) 'repeat AMD import is idempotent'
    Check ([IO.File]::GetLastWriteTimeUtc((Join-Path $repo $entry.RelativePath))-eq $timestamp) 'repeat AMD import does not recopy existing package'
    Check ($generator.Verify($repo,$ct).Count-eq 0) 'actual imported repository integrity verification succeeds'
}
Write-Output ('ARTIFACTS: '+$root)
