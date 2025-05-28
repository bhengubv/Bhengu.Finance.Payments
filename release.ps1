param (
    [string]$Version,
    [string]$Notes
)

if (-not $Version -or -not $Notes) {
    Write-Host "❌ Usage: .\release.ps1 v1.0.0 'Release notes here'"
    exit 1
}

Write-Host "📦 Tagging version: $Version"
git tag $Version
git push origin $Version

Write-Host "🚀 Creating GitHub release for $Version"
gh release create $Version --title $Version --notes "$Notes" --target master

Write-Host "🎉 Release triggered. Check the GitHub Actions tab for progress!"
