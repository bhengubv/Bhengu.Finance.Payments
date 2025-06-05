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
    Fail "$Reason (rollback complete for v$Version)"
}

# Change to script directory
$scriptPath = $MyInvocation.MyCommand.Path
$scriptDir = Split-Path $scriptPath -Parent
Set-Location $scriptDir
Write-Host "[INFO] Changed to script directory: $scriptDir"

# Find all .csproj files excluding tests
Write-Host "[INFO] Scanning for .csproj files..."
$projects = Get-ChildItem -Recurse -Filter *.csproj | Where-Object { $_.Name -notlike '*.Tests.csproj' }
if (-not $projects) { Fail "No .csproj files found" }

# Extract version from first project
$firstProject = $projects[0].FullName
Write-Host "[INFO] Extracting version from: $firstProject"
$xml = [xml](Get-Content $firstProject)
$currentVersion = $xml.Project.PropertyGroup.Version
if (-not $currentVersion -or $currentVersion -notmatch '^\d+\.\d+\.\d+$') {
    Fail "Invalid version format: $currentVersion"
}
Write-Host "[INFO] Current version: $currentVersion"

# Bump patch version
$parts = $currentVersion -split '\.'
$patch = [int]$parts[2] + 1
$newVersion = "$($parts[0]).$($parts[1]).$patch"
Write-Host "[INFO] New version: $newVersion"

# Update all .csproj files
Write-Host "[INFO] Updating all .csproj files..."
foreach ($proj in $projects) {
    Write-Host "[INFO] Updating: $($proj.FullName)"
    $xml = [xml](Get-Content $proj.FullName)
    $xml.Project.PropertyGroup.Version = $newVersion
    $xml.Save($proj.FullName)
    git add $proj.FullName
}

# Commit
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) {
    git commit -m "Release v$newVersion" || Fail "Git commit failed"
} else {
    Fail "No changes to commit"
}

# Push to branch
git push origin $Branch || Fail "Git push failed"

# Tag release
git tag "v$newVersion" || Rollback -Reason "Failed to create git tag" -Version $newVersion
git push origin "v$newVersion"
if ($LASTEXITCODE -ne 0) {
    Rollback -Reason "Failed to push git tag" -Version $newVersion
}

# Create GitHub release
if (-not $Message) { $Message = "Automated release v$newVersion" }
gh release create "v$newVersion" --title "v$newVersion" --notes "$Message"
if ($LASTEXITCODE -ne 0) {
    Rollback -Reason "GitHub release failed" -Version $newVersion
}

Write-Host "[SUCCESS] Release v$newVersion complete!" -ForegroundColor Green
