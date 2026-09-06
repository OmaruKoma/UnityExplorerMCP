# Build script for UnityExplorer MCP Bridge

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

# Build MCP Bridge
Write-Host "Building MCP Bridge..." -ForegroundColor Green
Set-Location "src\MCPBridge"
dotnet build -c $Configuration
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to build MCP Bridge"
    exit 1
}

# Build MCP Server
Write-Host "Building MCP Server..." -ForegroundColor Green
Set-Location "..\..\mcp-server"
npm install
npm run build
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to build MCP Server"
    exit 1
}

Set-Location ".."
Write-Host "Build complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Output files:" -ForegroundColor Yellow
Write-Host "  MCP Bridge: src\MCPBridge\bin\$Configuration\UnityExplorer.MCPBridge.dll"
Write-Host "  MCP Server: mcp-server\dist\server.js"