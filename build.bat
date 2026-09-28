@echo off
rem Baut DLSS5Optimizer.exe (Single-File, win-x64). Braucht das .NET 10 SDK: https://dotnet.microsoft.com/download
cd /d "%~dp0"
dotnet test tests\Dlss5Optimizer.Core.Tests -c Release || exit /b 1
dotnet publish src\Dlss5Optimizer.App -c Release -o publish || exit /b 1
echo.
echo Fertig: %~dp0publish\DLSS5Optimizer.exe
pause
