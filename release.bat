@echo off
setlocal EnableDelayedExpansion

set "RELEASE_NOTES=%~1"

:: Step 1: Locate a usable .csproj file
echo [INFO] Scanning for .csproj files...
set "PROJECT="
for /f "delims=" %%f in ('dir /b /s *.csproj ^| findstr /v /i ".Tests.csproj"') do (
    set "PROJECT=%%f"
    goto :found
)

:found
if not defined PROJECT (
    echo [ERROR] No .csproj file found.
    exit /b 1
)

:: Step 2: Extract current version
echo [INFO] Extracting version from: %PROJECT%
set "VERSION_LINE="
for /f "tokens=1* delims=:" %%a in ('findstr /n "<Version>" "%PROJECT%"') do (
    set "VERSION_LINE=%%b"
    goto :parseversion
)

:parseversion
if not defined VERSION_LINE (
    echo [ERROR] <Version> tag not found.
    exit /b 1
)

:: Remove whitespace and extract value
set "VERSION_LINE=%VERSION_LINE: =%"
for /f "tokens=2 delims=<>" %%v in ("%VERSION_LINE%") do (
    set "VERSION=%%v"
)

:: Validate version format
> .versioncheck.tmp echo %VERSION%
findstr /r "^[0-9]\+\.[0-9]\+\.[0-9]\+$" .versioncheck.tmp >nul
if errorlevel 1 (
    echo [ERROR] Invalid version format: %VERSION%
    del .versioncheck.tmp >nul
    exit /b 1
)
del .versioncheck.tmp >nul

echo [INFO] Current version: %VERSION%

:: Step 3: Bump patch version
for /f "tokens=1-3 delims=." %%a in ("%VERSION%") do (
    set /a PATCH=%%c + 1
    set "NEW_VERSION=%%a.%%b.!PATCH!"
)
echo [INFO] New version: %NEW_VERSION%

:: Step 4: Update all .csproj files
echo [INFO] Updating all .csproj files...
for /f "delims=" %%f in ('dir /b /s *.csproj ^| findstr /v /i ".Tests.csproj"') do (
    echo [INFO] Updating:
