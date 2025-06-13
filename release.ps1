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

# Get current version from the first .csproj
$firstProject = $projects[0].FullName
Write-Host "[INFO] Extracting version from: $firstProject"
[xml]$xml = Get-Content $firstProject
$currentVersion = $xml.Project.PropertyGroup.Version
if (-not $currentVersion -or $currentVersion -match "[^0-9\.]") {
    Fail "Invalid version format: $currentVersion"
}
Write-Host "[INFO] Current version: $currentVersion"

# Check if repo has any changes
$hasChanges = git status --porcelain | Where-Object { $_.Trim() -ne "" }

if ($hasChanges) {
    # Bump patch version
    $parts = $currentVersion -split "\."
    if ($parts.Count -ne 3) { Fail "Version must be in format X.Y.Z" }
    $newVersion = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"
    Write-Host "[INFO] New version (due to changes): $newVersion"

    # Update version in all .csproj files
    foreach ($proj in $projects) {
        [xml]$doc = Get-Content $proj.FullName
        $pg = $doc.Project.PropertyGroup
        if ($pg.Version -and $pg.Version -ne $newVersion) {
            Write-Host "[INFO] Updating version in: $($proj.FullName)"
            $pg.Version = $newVersion
            $doc.Save($proj.FullName)
        }
    }

    git add . || Fail "Git add failed"
    if (-not $Message) { $Message = "Release v$newVersion" }
    git commit -m "$Message" || Fail "Git commit failed"

    git push origin $Branch || Fail "Git push failed"

    Write-Host "[INFO] Tagging version: v$newVersion"
    git tag -f "v$newVersion" || Rollback "Failed to create git tag", $newVersion
    git push origin -f "v$newVersion" || Rollback "Failed to push git tag", $newVersion

    Write-Host "[SUCCESS] Release v$newVersion pushed and tagged." -ForegroundColor Green
} else {
    Write-Host "[INFO] No changes detected — skipping version bump and tag."
}
