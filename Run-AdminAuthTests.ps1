param([string]$OutputDirectory = "admin-auth-test")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Force $out | Out-Null

& (Join-Path $root "Build.ps1") -OutputDirectory $OutputDirectory | Out-Null
$repo = Join-Path $root "lan-final-verify\work\report\repo"
if (-not (Test-Path $repo)) { throw "Synthetic test repository is missing: $repo" }

$salt = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($salt)
$password = "AdminPassword-For-Test-99"
$derive = New-Object Security.Cryptography.Rfc2898DeriveBytes($password, $salt, 150000)
$record = [ordered]@{
  User = "udaadmin"
  Salt = [Convert]::ToBase64String($salt)
  Hash = [Convert]::ToBase64String($derive.GetBytes(32))
  Iterations = 150000
}
$credentials = Join-Path $out "admin-credentials.json"
[IO.File]::WriteAllText($credentials, ($record | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding($false)))

$port = 18741
$exe = Join-Path $out "Lan\LanDriverService.exe"
$process = Start-Process -FilePath $exe -ArgumentList @(
  "--port", "$port", "--repo", $repo,
  "--admin-credentials", $credentials, "--bind", "localhost"
) -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 900
try {
  $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
  $code = 0
  try { Invoke-WebRequest "http://localhost:$port/api/admin/dashboard" -UseBasicParsing -WebSession $session -ErrorAction Stop | Out-Null }
  catch { $code = [int]$_.Exception.Response.StatusCode }
  if ($code -ne 401) { throw "Unauthenticated dashboard expected 401, got $code" }

  $importCode = 0
  try {
    Invoke-WebRequest "http://localhost:$port/api/admin/import?src=C%3A%5Cnot-allowed" -Method Post `
      -Headers @{ "X-UDA-Protocol" = "1" } -UseBasicParsing -WebSession $session -ErrorAction Stop | Out-Null
  } catch { $importCode = [int]$_.Exception.Response.StatusCode }
  if ($importCode -ne 401) { throw "Anonymous import expected 401, got $importCode" }

  $bad = 0
  try {
    Invoke-WebRequest "http://localhost:$port/api/admin/login" -Method Post -ContentType "application/json" `
      -Body '{"user":"udaadmin","password":"incorrect-value"}' -UseBasicParsing -WebSession $session -ErrorAction Stop | Out-Null
  } catch { $bad = [int]$_.Exception.Response.StatusCode }
  if ($bad -ne 401) { throw "Bad login expected 401, got $bad" }

  $body = @{ user = "udaadmin"; password = $password } | ConvertTo-Json -Compress
  $login = Invoke-WebRequest "http://localhost:$port/api/admin/login" -Method Post -ContentType "application/json" `
    -Body $body -UseBasicParsing -WebSession $session
  if ($login.StatusCode -ne 200) { throw "Login failed" }
  $cookie = $session.Cookies.GetCookies("http://localhost:$port")["UDAADMIN"]
  if ($null -eq $cookie -or -not $cookie.HttpOnly) { throw "Secure admin session cookie missing" }

  $dashboard = Invoke-WebRequest "http://localhost:$port/api/admin/dashboard" -UseBasicParsing -WebSession $session
  if ($dashboard.StatusCode -ne 200) { throw "Authenticated dashboard failed" }

  $logout = Invoke-WebRequest "http://localhost:$port/api/admin/logout" -Method Post -UseBasicParsing -WebSession $session
  if ($logout.StatusCode -ne 200) { throw "Logout failed" }
  Write-Host "PASS: admin account/password, PBKDF2 credential and session authentication"
}
finally {
  Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
}
