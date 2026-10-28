@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title Emilio's Soundboard v2 - Build and Run
echo ==========================================
echo   EMILIO'S SOUNDBOARD v2 - BUILD AND RUN
echo ==========================================
echo.
where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: .NET 8 SDK was not found.
  echo Install it from https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
if exist "native\emilios_native\Cargo.toml" (
  where cargo >nul 2>nul
  if not errorlevel 1 (
    echo Building optional Rust starfield helper...
    cargo build --release --manifest-path "native\emilios_native\Cargo.toml"
    if not errorlevel 1 (
      if not exist "EmiliosSoundboard\runtimes\win-x64\native" mkdir "EmiliosSoundboard\runtimes\win-x64\native"
      copy /Y "native\emilios_native\target\release\emilios_native.dll" "EmiliosSoundboard\runtimes\win-x64\native\emilios_native.dll" >nul
    ) else (
      echo Rust helper build failed; using the checked-in DLL or managed fallback.
    )
  )
)
echo Restoring packages and building...
dotnet restore "EmiliosSoundboard.sln"
if errorlevel 1 goto failed
dotnet build "EmiliosSoundboard.sln" -c Release --no-restore
if errorlevel 1 goto failed
set "APP_EXE=%~dp0EmiliosSoundboard\bin\Release\net8.0-windows\EmiliosSoundboard.exe"
if not exist "%APP_EXE%" (
  echo ERROR: Build succeeded but the expected EXE was not found:
  echo %APP_EXE%
  goto failed
)
echo Build succeeded. Launching Emilio's Soundboard...
start "" "%APP_EXE%"
exit /b 0
:failed
echo.
echo BUILD FAILED. Copy the full error output above for troubleshooting.
pause
exit /b 1
