@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title Emilio's Soundboard - Single EXE
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
    )
  )
)

if not exist "dist" mkdir "dist"
echo Publishing one self-contained EmiliosSoundboard.exe...
dotnet publish "EmiliosSoundboard\EmiliosSoundboard.csproj" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "%~dp0dist"
if errorlevel 1 goto failed
if not exist "%~dp0dist\EmiliosSoundboard.exe" goto failed
echo.
echo PUBLISH SUCCEEDED: dist\EmiliosSoundboard.exe
echo The EXE is self-contained; the Rust DLL (if built) is bundled and extracted automatically.
exit /b 0

:failed
echo PUBLISH FAILED. Copy the full output above for troubleshooting.
pause
exit /b 1
