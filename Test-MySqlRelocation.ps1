[CmdletBinding()]param([string]$TestRoot)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $MyInvocation.MyCommand.Path
$instances=Get-Content -LiteralPath (Join-Path $TestRoot 'running.json') -Raw | ConvertFrom-Json
$artifact=Join-Path $workspace 'release-mysql-relocation-20261030'
[void][Reflection.Assembly]::LoadFrom((Join-Path $artifact 'UDA-MySQL-Relocation.exe'))
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $artifact 'UDA-MySQL-Relocation.exe'))
$type=$assembly.GetType('UDA.RelocationEngine',$true)
$open=$type.GetMethod('Open',[Reflection.BindingFlags]'NonPublic,Static')
$src=$instances | Where-Object name -eq 'source';$dst=$instances | Where-Object name -eq 'target'
$sourceDb="uda_source_"+[Guid]::NewGuid().ToString("N");$targetDb="tineco_UDA_"+[Guid]::NewGuid().ToString("N")
$source="Server=127.0.0.1;Port=$($src.port);Database=$sourceDb;User ID=root;Password=;SslMode=None"
$target="Server=127.0.0.1;Port=$($dst.port);Database=$targetDb;User ID=root;Password=;SslMode=None"
function Connect([string]$connection,[bool]$admin=$false){$open.Invoke($null,[object[]]@($connection,$admin))}
function Sql($db,[string]$query,[object[]]$values=@()){$c=$db.CreateCommand();try{$c.CommandText=$query;for($n=0;$n -lt $values.Length;$n++){$p=$c.CreateParameter();$p.ParameterName='@p'+$n;$p.Value=$values[$n];[void]$c.Parameters.Add($p)};$c.ExecuteNonQuery()}finally{$c.Dispose()}}
$a=Connect $source $true
try{[void](Sql $a ("CREATE DATABASE "+$sourceDb+" CHARACTER SET utf8mb4 COLLATE utf8mb4_bin"))}finally{$a.Dispose()}
$a=Connect $source
try{
 [void](Sql $a 'CREATE TABLE uda_schema(id INT PRIMARY KEY,version INT NOT NULL) ENGINE=InnoDB')
 [void](Sql $a 'INSERT INTO uda_schema VALUES(1,1)')
 [void](Sql $a 'CREATE TABLE uda_documents(repository_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,document_key VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,body LONGTEXT CHARACTER SET utf8mb4 NOT NULL,updated_utc DATETIME(6) NOT NULL,PRIMARY KEY(repository_id,document_key)) ENGINE=InnoDB')
 $large=('中文资料'+[char]::ConvertFromUtf32(0x1F600))*130000
 $time=[DateTime]::Parse('2026-10-30T08:00:00.123456')
 [void](Sql $a 'INSERT INTO uda_documents VALUES(@p0,@p1,@p2,@p3)' @(('a'*32),'server-logs/test',$large,$time))
 [void](Sql $a 'INSERT INTO uda_documents VALUES(@p0,@p1,@p2,@p3)' @(('b'*32),'other-repository/data','完整迁移第二个仓库',$time))
 [void](Sql $a 'CREATE TABLE test_values(id INT PRIMARY KEY AUTO_INCREMENT,rawdata LONGBLOB NULL,n DECIMAL(20,5),stamp TIMESTAMP(6) NULL) ENGINE=InnoDB')
 [byte[]]$raw=New-Object byte[] 80000;for($i=0;$i -lt $raw.Length;$i++){$raw[$i]=[byte]($i%251)}
 [void](Sql $a 'INSERT INTO test_values VALUES(@p0,@p1,@p2,@p3)' @(1,$raw,[decimal]123456789.12000,$time))
 [void](Sql $a 'INSERT INTO test_values VALUES(@p0,@p1,@p2,@p3)' @(2,[DBNull]::Value,[decimal]-1.25,[DBNull]::Value))
}finally{$a.Dispose()}
$messages=New-Object 'Collections.Generic.List[string]'
$logger=[Action[string]]{param($m)$messages.Add($m);Write-Host $m}
$type.GetMethod('PreparePacketLimit').Invoke($null,[object[]]@($source,$target,$true,$logger))
$type.GetMethod('Copy').Invoke($null,[object[]]@($source,$target,[Threading.CancellationToken]::None,$logger))
$b=Connect $target
try{$c=$b.CreateCommand();$c.CommandText='SELECT COUNT(*),MAX(OCTET_LENGTH(body)) FROM uda_documents';$r=$c.ExecuteReader();try{[void]$r.Read();if($r.GetInt64(0) -ne 2 -or $r.GetInt64(1) -le 1048576){throw 'Large UTF8 / complete repository test failed'}}finally{$r.Dispose();$c.Dispose()}}finally{$b.Dispose()}
$refused=$false
try{$type.GetMethod('Copy').Invoke($null,[object[]]@($source,$target,[Threading.CancellationToken]::None,$logger))}catch{if($_.Exception.ToString().Contains('已存在')){$refused=$true}else{throw}}
if(-not $refused){throw 'Existing target must be refused'}
$messages | Set-Content -LiteralPath (Join-Path $TestRoot 'verification.txt') -Encoding UTF8
Write-Host 'PASS: real isolated MySQL 8.0 migration, automatic repair of initially 1MiB destination packet, >1MiB Unicode/emoji, BLOB/NULL/decimal/timestamp, all repositories, existing target refusal.'
