param([string]$Output = (Join-Path $PSScriptRoot 'artifacts\DynamicApi-win-x64'))
$ErrorActionPreference = 'Stop'
$Output = [System.IO.Path]::GetFullPath($Output)
if (Test-Path (Join-Path $Output 'App_Data')) { throw 'Use a new output folder; never publish over existing data.' }
if (Test-Path $Output) { if (@(Get-ChildItem -LiteralPath $Output -Force).Count -gt 0) { throw 'Use an empty output directory.' } }
& dotnet publish (Join-Path $PSScriptRoot 'src\DynamicApi\DynamicApi.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
foreach ($name in @('DynamicApi.exe','schema.json','users.json')) { if (-not (Test-Path (Join-Path $Output $name))) { throw "Missing output: $name" } }
Write-Host "Ready: $Output\DynamicApi.exe" -ForegroundColor Green
