# Builds the single-file SLiveTranslator.exe into dist\.
#   .\publish.ps1
# The exe needs no .NET install. Settings, cache and logs stay in %APPDATA%\SLiveTranslator (or next to the exe
# when a portable.flag file is there), so replacing the exe keeps them.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\SLiveTranslator\SLiveTranslator.csproj'
$dist = Join-Path $root 'dist'

# A running copy locks the exe being replaced.
Get-Process SLiveTranslator -ErrorAction SilentlyContinue | Stop-Process -Force

dotnet publish $project -p:PublishProfile=win-x64 -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $root 'src\SLiveTranslator\bin\publish\win-x64\SLiveTranslator.exe'
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item $exe (Join-Path $dist 'SLiveTranslator.exe') -Force

$size = (Get-Item (Join-Path $dist 'SLiveTranslator.exe')).Length / 1MB
Write-Host ("dist\SLiveTranslator.exe  {0:N1} MB" -f $size)
