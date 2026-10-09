@echo off
setlocal
cd /d "%~dp0"
title Espanso Simple Editor - build

where dotnet >nul 2>nul
if errorlevel 1 (
  echo The .NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download and run this again.
  pause
  exit /b 1
)

echo.
echo [1/4] Running the tests (they never touch your Espanso files)...
dotnet test tests\EspansoEditor.Tests -c Release --nologo -v quiet
if errorlevel 1 goto :fail

echo.
echo [2/4] Small exe (needs the .NET 10 Desktop Runtime, which a .NET 10 SDK includes)...
dotnet publish src\EspansoEditor.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist\small --nologo -v quiet
if errorlevel 1 goto :fail

echo.
echo [3/4] Portable exe (runs on any 64-bit Windows, no .NET needed; larger)...
dotnet publish src\EspansoEditor.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\portable --nologo -v quiet
if errorlevel 1 goto :fail

echo.
echo [4/4] snipconv checker...
dotnet publish src\snipconv -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist\snipconv --nologo -v quiet
if errorlevel 1 goto :fail

echo.
echo [docs] Jira versions of the guides...
where py >nul 2>nul
if errorlevel 1 (
  echo   Python not found: skipped. The .jira.txt files in docs stay as they are.
) else (
  py tools\md2jira.py docs\Espanso-Guide.md docs\Espanso-Guide-Short.md
)

echo.
echo Done.
echo   dist\small\EspansoSimpleEditor.exe
echo   dist\portable\EspansoSimpleEditor.exe
echo   dist\snipconv\snipconv.exe   (try: dist\snipconv\snipconv.exe check "%%APPDATA%%\espanso")
echo.
pause
exit /b 0

:fail
echo.
echo BUILD FAILED. Scroll up for the first error.
pause
exit /b 1
