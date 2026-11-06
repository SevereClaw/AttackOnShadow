# Builds setup\out\AttackOnShadow.exe with the C# compiler that ships with Windows.
# Usage (from the repo root):  pwsh setup/build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force $out | Out-Null

$version = (Get-Content (Join-Path $root 'mod/version.json') -Raw | ConvertFrom-Json).version
Write-Host "AttackOnShadow v$version"

# payload.zip: mod/** and patch/patch.js, with forward-slash entry names
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$payload = Join-Path $out 'payload.zip'
if (Test-Path $payload) { Remove-Item $payload }
$zip = [System.IO.Compression.ZipFile]::Open($payload, 'Create')
try {
  $files = @(Get-ChildItem (Join-Path $root 'mod') -Recurse -File) + @(Get-Item (Join-Path $root 'patch/patch.js'))
  foreach ($f in $files) {
    $rel = $f.FullName.Substring($root.Length + 1).Replace('\', '/')
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $rel, 'Optimal')
    Write-Host "  + $rel"
  }
} finally { $zip.Dispose() }

# file version from mod/version.json
$v4 = ($version -replace '[^0-9.]', '') + '.0.0.0'
$v4 = ($v4.Split('.')[0..3]) -join '.'
$verCs = Join-Path $out 'Version.cs'
"[assembly: System.Reflection.AssemblyVersion(`"$v4`")]`n[assembly: System.Reflection.AssemblyFileVersion(`"$v4`")]`n[assembly: System.Reflection.AssemblyInformationalVersion(`"$version`")]" | Set-Content $verCs -Encoding UTF8

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found in $fw" }
function Ref($name) {
  $cands = @((Join-Path $fw "$name.dll")) + @(Get-ChildItem "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\$name" -Recurse -Filter "$name.dll" -ErrorAction SilentlyContinue | ForEach-Object FullName)
  foreach ($c in $cands) { if (Test-Path $c) { return $c } }
  throw "$name.dll not found"
}
$zipRef = Ref 'System.IO.Compression'
$zipFsRef = Ref 'System.IO.Compression.FileSystem'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
  /out:"$out\AttackOnShadow.exe" `
  /win32icon:"$PSScriptRoot\aos.ico" /win32manifest:"$PSScriptRoot\app.manifest" `
  /resource:"$payload,payload.zip" `
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
  /r:"$zipRef" /r:"$zipFsRef" `
  "$PSScriptRoot\AttackOnShadow.cs" $verCs
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
Get-Item "$out\AttackOnShadow.exe" | Format-List Name, Length
