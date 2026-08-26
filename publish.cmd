@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

set "OUT=publish\win-x64"
set "ZIP=publish\Writersword-win-x64.zip"

if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%ZIP%" del /q "%ZIP%"

dotnet publish Writersword.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=none ^
    -o "%OUT%"

if errorlevel 1 (
    echo.
    echo Publish failed
    pause
    exit /b 1
)

if not exist "%OUT%\appsettings.json" (
    echo.
    echo appsettings.json missing in output
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%OUT%\*' -DestinationPath '%ZIP%' -CompressionLevel Optimal"

if errorlevel 1 (
    echo.
    echo Archive creation failed
    pause
    exit /b 1
)

echo.
for %%F in ("%ZIP%") do echo Archive: %%~fF  %%~zF bytes
echo.
pause
