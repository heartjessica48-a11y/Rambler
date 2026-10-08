<#
.SYNOPSIS
  Builds, tests and publishes Rambler as a self-contained Windows x64 executable.
.EXAMPLE
  ./build.ps1                 # build + unit tests + publish to artifacts/publish/win-x64
  ./build.ps1 -SkipTests
  ./build.ps1 -LiveTests      # also runs live Gemini tests (needs $env:GEMINI_API_KEY)
#>
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$LiveTests
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet build Rambler.sln -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not $SkipTests) {
    dotnet test tests/Rambler.Core.Tests -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($LiveTests) {
    dotnet test tests/Rambler.IntegrationTests -c $Configuration --no-build --logger "console;verbosity=detailed"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

dotnet publish src/Rambler/Rambler.csproj -c $Configuration -r win-x64 --self-contained true -o artifacts/publish/win-x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`nPublished: $(Resolve-Path artifacts/publish/win-x64/Rambler.exe)"
