# Downloads the Microsoft .NET Framework 4.8 reference assemblies used to build the WPF shell
# without a system-wide targeting pack installation.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $PSScriptRoot 'refasm'
$target = Join-Path $dest '.NETFramework\v4.8'
if (Test-Path (Join-Path $target 'mscorlib.dll')) {
    Write-Host "[refasm] already present: $target"
    return
}

$cache = Join-Path $env:TEMP 'Microsoft.NETFramework.ReferenceAssemblies.net48.1.0.3.nupkg'
if (-not (Test-Path $cache)) {
    Write-Host '[refasm] downloading Microsoft.NETFramework.ReferenceAssemblies.net48 1.0.3'
    Invoke-WebRequest -Uri 'https://www.nuget.org/api/v2/package/Microsoft.NETFramework.ReferenceAssemblies.net48/1.0.3' -OutFile $cache -UseBasicParsing
}

$pkg = Join-Path $env:TEMP 'iwk-refasm-extract'
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Force $pkg | Out-Null
Copy-Item $cache (Join-Path $pkg 'refasm.zip')
Expand-Archive -Path (Join-Path $pkg 'refasm.zip') -DestinationPath (Join-Path $pkg 'out') -Force

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $pkg 'out\build\.NETFramework') $dest -Recurse -Force
Remove-Item $pkg -Recurse -Force
Write-Host "[refasm] installed to $target"
