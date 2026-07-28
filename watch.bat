@echo off
rem Dev run: dotnet watch + HotAvalonia.
rem XAML applies on save, C# via hot reload.
rem Restart: Ctrl+R in this window. Stop: Ctrl+C.
rem ASCII only on purpose: cmd reads .bat in the OEM codepage,
rem so Cyrillic here would corrupt the commands.
cd /d "%~dp0"
dotnet watch --project Writersword.csproj run
echo.
echo === watch exited with code %errorlevel% ===
pause
