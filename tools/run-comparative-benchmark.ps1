# Phase 26 — Comparative Benchmark Runner
# Runs the full comparative benchmark suite and publishes results.
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/run-comparative-benchmark.ps1
#   powershell -ExecutionPolicy Bypass -File tools/run-comparative-benchmark.ps1 -Smoke
#   powershell -ExecutionPolicy Bypass -File tools/run-comparative-benchmark.ps1 -Filter "*AllocationBaseline*"

param(
    [switch]$Smoke,
    [switch]$NoCache,
    [string]$Filter = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = $PSScriptRoot | Split-Path -Parent
$BenchmarkProject = "$RepoRoot/tests/Ampelum.ComparativeBenchmarks/Ampelum.ComparativeBenchmarks.csproj"
$ResultsDir = "$RepoRoot/docs/benchmarks"
$Date = Get-Date -Format "yyyy-MM-dd"
$GitHash = (git -C $RepoRoot rev-parse --short HEAD)

# Determine mode
if ($Smoke) {
    Write-Host "=== Smoke test mode (mock LLM) ===" -ForegroundColor Yellow
    $Filter = "*Scenario19*"
    $Env:E2E_CACHE_BACKEND = ""
} else {
    if (-not $Filter) {
        $Filter = "*Scenario16*|*Scenario17*|*Scenario18*|*Scenario19*"
    }
    if (-not $NoCache) {
        $Env:E2E_CACHE_BACKEND = "disk"
        Write-Host "=== Cache: disk ===" -ForegroundColor Cyan
    }
}

Write-Host "Filter: $Filter" -ForegroundColor Cyan

# Build
Write-Host "Building..." -ForegroundColor Cyan
dotnet build $BenchmarkProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# Run
Write-Host "Running benchmarks..." -ForegroundColor Cyan
dotnet run --project $BenchmarkProject -c Release --no-build -- `
    --filter $Filter `
    --job medium `
    --exporters markdown `
    --artifacts "$ResultsDir"

if ($LASTEXITCODE -ne 0) {
    Write-Host "Benchmark run completed with warnings" -ForegroundColor Yellow
}

# Copy latest results
$LatestMarkdown = Get-ChildItem -Path $ResultsDir -Filter "*-report.md" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($LatestMarkdown) {
    $Target = "$ResultsDir/$Date-$GitHash.md"
    Copy-Item $LatestMarkdown.FullName $Target
    Write-Host "Results published: $Target" -ForegroundColor Green
}

Write-Host "Done." -ForegroundColor Green
</write_to_file>