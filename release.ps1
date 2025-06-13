param (
    [string]$NewVersion = ""
)

function Fail {
    param([string]$Step)
    Write-Host "[ERROR] $Step" -ForegroundColor Red
    exit 1
}

# Switch to script directory
Set-Location -Path $PSScriptRoot
Write-Host "[INFO] Changed to script directory: $PSScriptRoot"

# Get .csproj files
Write-Host "[INFO] Scanning for .csproj files..."
$projects = Get-ChildItem -Recurse -Filter *.csproj | Where-Object { $_.Name -notlike '*.Tests.csproj' }
if (-not $projects) { Fail "No .csproj files found" }

if (-not $NewVersion) {
    # Attempt to read version from first .csproj and bump patch version
    [xml]$xml = Get-Content $projects[0].FullName
    $currentVersion = $xml.Project.PropertyGroup.Version
    if (-not $currentVersion -or $currentVersion -match "[^0-9\.]") {
        Fail "Invalid or missing version: $currentVersion"
    }

    Write-Host "[INFO] Current version: $currentVersion"
    $parts = $currentVersion -split "\."
    if ($parts.Count -ne 3) { Fail "Version must be in format X.Y.Z" }
    $NewVersion = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"
}

Write-Host "[INFO] New version: $NewVersion"

# Update version in all .csproj files
Write-Host "[INFO] Updating all .csproj files..."
foreach ($proj in $projects) {
    [xml]$doc = Get-Content $proj.FullName
    $pg = $doc.Project.PropertyGroup
    if ($pg.Version -and $pg.Version -ne $NewVersion) {
        Write-Host "[INFO] Updating: $($proj.FullName)"
        $pg.Version = $NewVersion
        $doc.Save($proj.FullName)
    }
}

Write-Host "[SUCCESS] Updated all projects to version $NewVersion" -ForegroundColor Green
