# Build script for UnityExplorer MCP Bridge (dual backend).
#
#   .\build.ps1                       # IL2CPP + Mono + MCP Server
#   .\build.ps1 -Backend IL2CPP       # IL2CPP backend only
#   .\build.ps1 -Backend Mono         # Mono backend only
#
# Reference locations are resolved by src/MCPBridge/MCPBridge.csproj from
# environment variables / MSBuild properties (no hardcoded paths):
#   UNITY_GAME_DIR  : IL2CPP game root containing BepInEx/ (interop + core)
#   MONO_REF_ROOT   : BepInEx Mono refs (install, game Managed/, or UnityExplorer lib/net35)

param(
    [ValidateSet("All", "IL2CPP", "Mono", "Server")]
    [string]$Backend = "All"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

function Invoke-BridgeBuild {
    param([string]$Config, [string]$Label)
    Write-Host "Building MCP Bridge ($Label)..." -ForegroundColor Green
    dotnet build "$RepoRoot\src\MCPBridge\MCPBridge.csproj" -c $Config
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to build MCP Bridge ($Label)"
        exit 1
    }
}

if ($Backend -eq "All" -or $Backend -eq "IL2CPP") {
    Invoke-BridgeBuild -Config "Release_IL2CPP" -Label "IL2CPP"
}

if ($Backend -eq "All" -or $Backend -eq "Mono") {
    Invoke-BridgeBuild -Config "Release_Mono" -Label "Mono"
}

if ($Backend -eq "All" -or $Backend -eq "Server") {
    Write-Host "Building MCP Server..." -ForegroundColor Green
    Push-Location "$RepoRoot\mcp-server"
    try {
        npm install
        npm run build
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to build MCP Server"
            exit 1
        }
    } finally {
        Pop-Location
    }
}

Write-Host "Build complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Output files:" -ForegroundColor Yellow
if ($Backend -eq "All" -or $Backend -eq "IL2CPP") {
    Write-Host "  IL2CPP Bridge: dist\il2cpp\MCPBridge.IL2CPP.dll (+ mcs.dll)"
}
if ($Backend -eq "All" -or $Backend -eq "Mono") {
    Write-Host "  Mono Bridge:   dist\mono\MCPBridge.Mono.dll (+ mcs.dll)"
}
if ($Backend -eq "All" -or $Backend -eq "Server") {
    Write-Host "  MCP Server:    mcp-server\dist\server.js"
}
