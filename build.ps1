<#
  build.ps1 -- compiles ZoneMax.exe with the C# compiler that ships inside Windows.
  No .NET SDK, no NuGet, no dependencies. Output is a single ~30KB exe.
#>
$ErrorActionPreference = 'Stop'
$root = 'C:\Projects\zonemax'
$csc  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

# Don't compile over a running instance.
Get-Process ZoneMax -ErrorAction SilentlyContinue | ForEach-Object {
    "stopping running ZoneMax (PID $($_.Id))"
    Stop-Process -Id $_.Id -Force -Confirm:$false
}
Start-Sleep -Milliseconds 400

$out = Join-Path $root 'ZoneMax.exe'
$args = @(
    '/nologo'
    '/target:winexe'
    '/platform:x64'
    '/optimize+'
    "/out:$out"
    '/r:System.dll'
    '/r:System.Core.dll'
    '/r:System.Drawing.dll'
    '/r:System.Windows.Forms.dll'
    (Join-Path $root 'ZoneMax.cs')
)

& $csc $args
if ($LASTEXITCODE -ne 0) { throw "BUILD FAILED (exit $LASTEXITCODE)" }

$fi = Get-Item $out
"BUILD OK  ->  $($fi.FullName)  ($([math]::Round($fi.Length/1KB,1)) KB)"
