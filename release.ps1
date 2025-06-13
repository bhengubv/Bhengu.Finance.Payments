param (
    [string]$Message = "",
    [string]$Branch = "master"
)

function Fail {
    param([string]$Step)
    Write-Host "[ERROR] $Step" -ForegroundColor Red
    exit 1
}

function Rollback {
    param([string]$Reason, [string]$Version)
    Write-Host "[ROLLBACK] $Reason" -ForegroundColor Yellow
    git reset --soft HEAD~1 | Out-Null
    git tag -d "v$Version" | Out-Null
    git push origin ":refs/tags/v$Version" | Out-Null
    Fail "$Reason (rollback complete)"
}

# Switch to script directory
Set-Location -Path $PSScriptRoot
Write-Host "[INFO] Changed to script directory: $PSScriptRoot"

# Get .csproj files
Write-Host "[INFO] Scanning for .csproj files..."
$projects = Get-ChildItem -Recurse -Filter *.csproj | Where-Object { $_.Name -notlike '*.Tests.csproj' }
if (-not $projects) { Fail "No .csproj files found" }

# Just commit the state — let GitHub Actions bump version
$changed = $false
foreach ($proj in $projects) {
    [xml]$doc = Get-Content $proj.FullName
    $pg = $doc.Project.PropertyGroup
    if ($pg.Version) {
        Write-Host "[INFO] Keeping version unchanged in: $($proj.FullName)"
        $changed = $true
    }
}

# Commit if needed
if ($changed) {
    git add . || Fail "Git add failed"
    if (-not $Message) { $Message = "🔖 Release trigger (let GitHub Actions bump version)" }
    git commit -m "$Message" || Fail "Git commit failed"
} else {
    Write-Host "[INFO] No changes to commit — exiting early"
    exit 0
}

# Push to origin and let CI/CD handle versioning and tagging
git push origin $Branch || Fail "Git push failed"

Write-Host "[SUCCESS] Release commit pushed — GitHub Actions will handle version bumping." -ForegroundColor Green
