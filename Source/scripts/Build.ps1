param([ValidateSet("win-x64", "win-arm64")][string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet build CodexUsage.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
    dotnet run --project tests/CodexUsage.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "Checks failed" }
    dotnet publish src/CodexUsage.Widget -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "artifacts/$Runtime"
    if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
    Write-Host "Run artifacts/$Runtime/CodexUsage.exe (or add --mock). Codex CLI is a separate prerequisite."
} finally { Pop-Location }
