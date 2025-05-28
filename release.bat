@echo off
setlocal EnableDelayedExpansion

:: Usage: release.bat 1.0.1 "Your release notes"

:: Check arguments
if "%~1"=="" (
  echo ❌ Version number is required.
  echo Example: release.bat 1.0.1 "Added new payment gateway"
  exit /b 1
)

set VERSION=%1
set NOTES=%~2

echo 🔖 Tagging version: v%VERSION%

:: Tag the version (force delete if exists)
git tag -d v%VERSION% >nul 2>&1
git push origin :refs/tags/v%VERSION% >nul 2>&1
git tag v%VERSION%
git push origin v%VERSION%

:: Commit any staged changes
echo ✅ Committing staged changes...
git commit -m "Release v%VERSION%" >nul 2>&1
git push origin master

:: Create GitHub release
echo 🚀 Creating GitHub release...
if not defined NOTES (
  set NOTES=Automated release v%VERSION%
)

gh release create v%VERSION% -t "v%VERSION%" -n "%NOTES%" || (
  echo ❌ GitHub CLI failed. Make sure you're authenticated with 'gh auth login'.
  exit /b 1
)

echo 🎉 Release v%VERSION% created and pushed to GitHub!

endlocal
