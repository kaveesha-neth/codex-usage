param([string]$Codex = "codex", [string]$Out = "schemas-local")
$ErrorActionPreference = "Stop"
& $Codex --version
if ($LASTEXITCODE -ne 0) { throw "Codex version check failed" }
& $Codex app-server generate-ts --out $Out
if ($LASTEXITCODE -ne 0) { throw "Schema generation failed; update your Codex CLI" }
$required = @{
    "GetAccountRateLimitsResponse.ts" = @("rateLimits", "rateLimitsByLimitId")
    "RateLimitSnapshot.ts" = @("limitId", "limitName", "primary", "secondary")
    "RateLimitWindow.ts" = @("usedPercent", "windowDurationMins", "resetsAt")
    "AccountRateLimitsUpdatedNotification.ts" = @("rateLimits")
    "GetAccountResponse.ts" = @("account")
    "LoginAccountParams.ts" = @('"chatgpt"')
}
foreach ($entry in $required.GetEnumerator()) {
    $file = Get-ChildItem -LiteralPath $Out -Recurse -Filter $entry.Key | Select-Object -First 1
    if (-not $file) { throw "Missing schema $($entry.Key)" }
    $body = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($field in $entry.Value) {
        if (-not $body.Contains($field)) { throw "Missing field $field in $($entry.Key). Review protocol before use." }
    }
}
Write-Host "Required account quota fields found. This checks schema only; it does not sign in or request model work."
