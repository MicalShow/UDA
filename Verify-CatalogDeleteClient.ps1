param([string]$InputFile)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Web.Extensions
$json=New-Object Web.Script.Serialization.JavaScriptSerializer
$cfg=$json.DeserializeObject([IO.File]::ReadAllText($InputFile))
$request=[Net.HttpWebRequest]::Create($cfg.url);$request.Proxy=$null;$request.Method='POST';$request.ContentType='application/json';$request.Headers['X-UDA-Protocol']=[string]$cfg.protocol
$request.CookieContainer=New-Object Net.CookieContainer;$request.CookieContainer.SetCookies([Uri]$cfg.url,$cfg.cookie)
$bytes=[Text.Encoding]::UTF8.GetBytes($cfg.body);$request.ContentLength=$bytes.Length;$stream=$request.GetRequestStream();$stream.Write($bytes,0,$bytes.Length);$stream.Dispose()
try{$response=$request.GetResponse()}catch{if($_.Exception.Response){$response=$_.Exception.Response}else{throw}}
try{$reader=New-Object IO.StreamReader($response.GetResponseStream());[IO.File]::WriteAllText($cfg.output,$reader.ReadToEnd());if([int]$response.StatusCode-ne 200){exit 1}}finally{$response.Dispose()}
