@echo off
chcp 65001 >nul
cd /d "%~dp0"

REM Kill any old instance holding the exe (avoids MSB3027 copy failure)
taskkill /F /IM EasyMovie.Client.exe >nul 2>&1

REM Force regeneration of the auto-generated App.g.cs so the OLD auto-splash
REM code (SplashScreen.Show(true)) is removed and our manual ShowSplash/CloseSplash takes over.
if exist "EasyMovie.Client\obj\Debug\net10.0-windows\App.g.cs" del /f /q "EasyMovie.Client\obj\Debug\net10.0-windows\App.g.cs" >nul 2>&1
if exist "EasyMovie.Client\obj\Release\net10.0-windows\win-x64\App.g.cs" del /f /q "EasyMovie.Client\obj\Release\net10.0-windows\win-x64\App.g.cs" >nul 2>&1

echo Building Release (publish, with ReadyToRun) ...
dotnet publish -c Release -r win-x64
if %ERRORLEVEL%==0 (
  echo.
  echo ========== BUILD OK ==========
  echo Launching: bin\Release\net10.0-windows\win-x64\publish\EasyMovie.Client.exe
  echo.
  REM 编译成功直接启动新版（开头 taskkill 已结束旧实例，避免 Mutex/文件占用）
  start "" "bin\Release\net10.0-windows\win-x64\publish\EasyMovie.Client.exe"
  goto :eof
) else (
  echo.
  echo ========== BUILD FAILED, see errors above ==========
)
pause
