@echo off
setlocal EnableExtensions DisableDelayedExpansion
title Safe C Drive Cleanup

echo.
echo ========================================
echo       Safe Temporary File Cleanup
echo ========================================
echo.
echo This script only removes unused files from:
echo   1. The current user's Temp folder
echo   2. The Windows Temp folder
echo.
echo It does NOT touch Downloads, Desktop, Documents, browser data,
echo Recycle Bin, registry, installed apps, drivers, or system files.
echo Locked or protected files are skipped automatically.
echo.
choice /C YN /N /M "Start cleanup? [Y/N]"
if errorlevel 2 (
    echo.
    echo Cancelled. No files were removed.
    pause
    exit /b 0
)

echo.
echo [1/2] Cleaning current user Temp folder...
if exist "%TEMP%\" (
    for /d %%D in ("%TEMP%\*") do rd /s /q "%%~fD" >nul 2>&1
    del /f /q /s "%TEMP%\*" >nul 2>&1
    echo Processed: %TEMP%
) else (
    echo Folder not found, skipped: %TEMP%
)

echo.
echo [2/2] Cleaning Windows Temp folder...
if exist "%SystemRoot%\Temp\" (
    for /d %%D in ("%SystemRoot%\Temp\*") do rd /s /q "%%~fD" >nul 2>&1
    del /f /q /s "%SystemRoot%\Temp\*" >nul 2>&1
    echo Processed: %SystemRoot%\Temp
) else (
    echo Folder not found, skipped: %SystemRoot%\Temp
)

echo.
echo Cleanup finished. Locked and protected files were kept.
pause
exit /b 0
