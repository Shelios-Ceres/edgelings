# Agent Island for Windows — release build.
# Publishes the WinUI app (self-contained, unpackaged, x64) and zips it under dist\.
# -Version comes from the shared VERSION file at release time (CI passes it);
# the csproj version is only a dev fallback.
param([string]$Version = "")

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$csproj = "src\AgentIsland.WinUI\AgentIsland.WinUI.csproj"
if (-not $Version) { $Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if (-not $Version) { $Version = "0.0.0" }
$version = "$Version".Trim()

$publishDir = "dist\publish"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

& dotnet publish $csproj `
    -c Release `
    -p:Platform=x64 `
    -r win-x64 `
    --self-contained true `
    -p:Version=$version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $publishDir "AgentIsland.WinUI.exe"
if (-not (Test-Path $exe)) { throw "publish did not produce $exe" }

$zip = "dist\AgentIsland-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$publishDir\*" -DestinationPath $zip

Write-Output "built  $exe"
Write-Output "packed $zip"
