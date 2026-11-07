@echo off
chcp 65001 >nul
rem Собирает установщик: dist\WinIsland-Setup.exe
cd /d "%~dp0"
dotnet publish Setup\WinIslandSetup.csproj -c Release -o dist -nologo
if errorlevel 1 (
  echo.
  echo Сборка не удалась.
  pause
  exit /b 1
)
echo.
echo Готово: dist\WinIsland-Setup.exe
pause
