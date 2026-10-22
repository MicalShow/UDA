param([string]$OutputDirectory = 'bin')
$ErrorActionPreference = 'Stop'
$buildRoot = Join-Path $PSScriptRoot $OutputDirectory
Add-Type -AssemblyName System.Windows.Forms
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $buildRoot 'Lan\UDA-Server-Manager.exe'))
$settingsType = $assembly.GetType('UDA.LanClientUiSettings')
$settingsMethod = $settingsType.GetMethod('SettingsFileName')
$oldKey = $settingsMethod.Invoke($null, @('http://10.100.66.99:8731'))
$newKey = $settingsMethod.Invoke($null, @('http://10.100.66.100:8731'))
if ($oldKey -eq $newKey) { throw 'New server inherits old client settings' }
if ($newKey -ne $settingsMethod.Invoke($null, @('http://10.100.66.100:8731/'))) { throw 'Equivalent endpoints do not share overrides' }
Write-Output 'PASS: New server package isolates old address overrides; same server retains manual settings'
$form = $assembly.CreateInstance('UDA.ServerManagerForm')
try {
    $form.CreateControl()
    if ([string]::IsNullOrWhiteSpace($form.Text)) { throw 'Native manager title missing' }
    if ($form.Controls.Count -eq 0) { throw 'Native manager has no controls' }
    $consoleField = $form.GetType().GetField('console', [Reflection.BindingFlags]'Instance,NonPublic')
    $consoleField.GetValue($form).Visible = $true
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    $bitmap = New-Object Drawing.Bitmap $form.Width,$form.Height
    try { $form.DrawToBitmap($bitmap, $form.ClientRectangle); $bitmap.Save((Join-Path $buildRoot 'server-manager-preview.png')) } finally { $bitmap.Dispose() }
    Write-Output 'PASS: Native server manager initializes with Windows controls'
} finally { $form.Dispose() }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $buildRoot 'Packages\UDA-Server-Setup.zip'))
try {
    if (-not ($archive.Entries | Where-Object { $_.FullName -match 'UDA-Server-Manager.exe$' })) { throw 'Installer missing native manager' }
    Write-Output 'PASS: Installer includes native server manager'
} finally { $archive.Dispose() }
$setupSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ServerSetup.cs') -Raw
$copyStatement = 'File.Copy(Path.Combine(source, "UDA-Server-Manager.exe"), Path.Combine(target, "UDA-Server-Manager.exe"), true);'
if (-not $setupSource.Contains($copyStatement)) { throw 'Installer does not copy native manager to installation directory' }
Write-Output 'PASS: Installer explicitly deploys native manager to installation directory'
