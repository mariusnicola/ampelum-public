#!/usr/bin/env pwsh
<#
.SYNOPSIS
Aggregates test-usage-*.json files from tools/usage-reports/ into a summary table.

.DESCRIPTION
Scans all test-usage-*.json files in tools/usage-reports/, extracts request counts,
token counts, and costs per project, and prints a formatted summary table.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools/aggregate-usage.ps1
#>
param(
    [string]$ReportsDir = "tools/usage-reports"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent

$reportsPath = Join-Path $repoRoot $ReportsDir

if (-not (Test-Path $reportsPath)) {
    Write-Host "No usage reports found at $reportsPath" -ForegroundColor Yellow
    Write-Host "Run tests with E2E fixtures first." -ForegroundColor Yellow
    exit 0
}

$reports = Get-ChildItem $reportsPath -Filter "test-usage-*.json" | Sort-Object Name

if ($reports.Count -eq 0) {
    Write-Host "No usage report files found in $reportsPath" -ForegroundColor Yellow
    exit 0
}

$pricing = @{
    "minimax/minimax-m2.7"       = @{ prompt = 0.30; completion = 0.30 }
    "deepseek/deepseek-v4-flash" = @{ prompt = 0.20; completion = 0.20 }
}

Write-Host ""
Write-Host "E2E TEST LLM USAGE REPORT"
Write-Host "=========================="
Write-Host ""

$totalRequests = 0
$totalPrompt = 0L
$totalCompletion = 0L
$totalCost = 0.0
$rows = @()

foreach ($report in $reports) {
    $data = Get-Content $report.FullName -Raw | ConvertFrom-Json
    $project = $data.project
    $model = $data.model
    $requests = $data.summary.RequestCount
    $prompt = $data.summary.PromptTokens
    $completion = $data.summary.CompletionTokens
    $cache = $data.summary.CacheHits

    $price = $pricing[$model]
    if ($price) {
        $cost = ($prompt / 1000000 * $price.prompt) + ($completion / 1000000 * $price.completion)
    } else {
        $cost = 0.0
    }

    $totalRequests += $requests
    $totalPrompt += $prompt
    $totalCompletion += $completion
    $totalCost += $cost

    $rows += [PSCustomObject]@{
        Project       = $project
        Requests      = $requests
        PromptTok     = $prompt
        CompletionTok = $completion
        TotalTok      = $prompt + $completion
        CacheHits     = $cache
        Cost          = $cost
        Model         = $model
    }
}

$fmt = "{0,-14} {1,8} {2,11} {3,11} {4,10} {5,8}"
Write-Host ($fmt -f "Project", "Requests", "Prompt Tok", "Compl Tok", "Total Tok", "Cost")
Write-Host ($fmt -f "----------", "--------", "-----------", "---------", "---------", "--------")

foreach ($row in $rows) {
    $costStr = "$" + $row.Cost.ToString("F4")
    Write-Host ($fmt -f $row.Project, $row.Requests, $row.PromptTok, $row.CompletionTok, $row.TotalTok, $costStr)
}

Write-Host ($fmt -f "----------", "--------", "-----------", "---------", "---------", "--------")
Write-Host ($fmt -f "TOTAL", $totalRequests, $totalPrompt, $totalCompletion, ($totalPrompt + $totalCompletion), ("$" + $totalCost.ToString("F4")))

Write-Host ""
Write-Host "Model used: $(($rows | Select-Object -First 1 -ExpandProperty Model))"
Write-Host ""

foreach ($row in $rows) {
    $cacheSuffix = if ($row.CacheHits -gt 0) { " ($($row.CacheHits) cache hits)" } else { "" }
    Write-Host "$($row.Project): $($row.Requests) requests, $($row.TotalTok) tokens, `$$($row.Cost.ToString('F4'))$cacheSuffix"
}

Write-Host ""
Write-Host "Report files: $reportsPath"