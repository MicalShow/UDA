[CmdletBinding()]
param(
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $diagnosticDirectory = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($diagnosticDirectory) -and $MyInvocation.MyCommand.Path) {
        $diagnosticDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    if ([string]::IsNullOrWhiteSpace($diagnosticDirectory)) { $diagnosticDirectory = (Get-Location).Path }
    $OutputPath = Join-Path $diagnosticDirectory 'UDA-AI-TLS-diagnostic.txt'
}
# Read-only diagnostics: no API key, no registry edits, no service restarts.
Add-Type -TypeDefinition @"
using System;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
public static class UdaTlsProbe {
    public static string Run(bool direct) {
        var log = new StringBuilder();
        var previous = ServicePointManager.SecurityProtocol;
        try {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            var request = (HttpWebRequest)WebRequest.Create("https://ark.cn-beijing.volces.com/api/plan/v1/models");
            request.Method = "GET"; request.AllowAutoRedirect = false;
            request.Timeout = 20000; request.ReadWriteTimeout = 20000;
            request.UserAgent = "UDA-TLS-Diagnostic"; request.Accept = "application/json";
            request.Proxy = direct ? null : WebRequest.DefaultWebProxy;
            request.ServerCertificateValidationCallback = delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors) {
                log.AppendLine("Certificate policy errors: " + errors);
                if (cert != null) {
                    var c = new X509Certificate2(cert);
                    log.AppendLine("Subject: " + c.Subject);
                    log.AppendLine("Issuer: " + c.Issuer);
                    log.AppendLine("Valid UTC: " + c.NotBefore.ToUniversalTime().ToString("o") + " -> " + c.NotAfter.ToUniversalTime().ToString("o"));
                }
                if (chain != null) foreach (var status in chain.ChainStatus)
                    log.AppendLine("Chain: " + status.Status + " " + status.StatusInformation.Trim());
                return errors == SslPolicyErrors.None;
            };
            using (var response = (HttpWebResponse)request.GetResponse()) log.AppendLine("HTTP: " + (int)response.StatusCode + " (TLS succeeded)");
        } catch (WebException error) {
            var response = error.Response as HttpWebResponse;
            if (response != null) { log.AppendLine("HTTP: " + (int)response.StatusCode + " (TLS succeeded; no API key was sent)"); response.Close(); }
            else log.AppendLine("Network status: " + error.Status);
            for (Exception e=error; e!=null; e=e.InnerException) log.AppendLine(e.GetType().Name + " (0x" + e.HResult.ToString("X8") + "): " + e.Message);
        } catch (Exception error) { log.AppendLine(error.GetType().Name + ": " + error.Message); }
        finally { ServicePointManager.SecurityProtocol = previous; }
        return log.ToString();
    }
}
"@
$report = New-Object Text.StringBuilder
[void]$report.AppendLine('UDA AI TLS diagnostic - no credentials sent; certificate validation remains enabled')
[void]$report.AppendLine('UTC: ' + [DateTime]::UtcNow.ToString('o'))
[void]$report.AppendLine('Identity: ' + [Security.Principal.WindowsIdentity]::GetCurrent().Name)
[void]$report.AppendLine('OS: ' + [Environment]::OSVersion.VersionString)
[void]$report.AppendLine('PowerShell: ' + $PSVersionTable.PSVersion.ToString())
try {
 $release = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction Stop
 [void]$report.AppendLine('.NET Framework Release: ' + $release.Release)
 $service = Get-CimInstance Win32_Service -Filter "Name='UdaLanDriverService'" -ErrorAction Stop
 if($service){[void]$report.AppendLine('Service identity: ' + $service.StartName + '; State: ' + $service.State)}
} catch {[void]$report.AppendLine('System inventory: ' + $_.Exception.Message)}
foreach($path in @('HKLM:\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client','HKLM:\SOFTWARE\Microsoft\.NETFramework\v4.0.30319','HKLM:\SOFTWARE\WOW6432Node\Microsoft\.NETFramework\v4.0.30319')) {
 [void]$report.AppendLine('Registry: ' + $path)
 if(Test-Path $path){
  $values=Get-ItemProperty $path
  foreach($name in @('Enabled','DisabledByDefault','SchUseStrongCrypto','SystemDefaultTlsVersions')) {
   $property=$values.PSObject.Properties[$name]
   if($null -ne $property){[void]$report.AppendLine('  '+$name+'='+$property.Value)}
  }
 } else {[void]$report.AppendLine('  Not explicitly configured (OS/runtime defaults apply)')}
}
[void]$report.AppendLine('--- Direct connection / TLS 1.2 ---')
[void]$report.AppendLine([UdaTlsProbe]::Run($true))
[void]$report.AppendLine('--- Current account default proxy / TLS 1.2 ---')
[void]$report.AppendLine([UdaTlsProbe]::Run($false))
[void]$report.AppendLine('HTTP 401/403/404/405 still proves HTTPS succeeded; this diagnostic does not test model permissions.')
[void]$report.AppendLine('An administrator account test does not prove the service account has identical trust/proxy settings.')
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $report.ToString(), (New-Object Text.UTF8Encoding($true)))
Write-Output $report.ToString()
Write-Output ('Report saved: '+[IO.Path]::GetFullPath($OutputPath))
