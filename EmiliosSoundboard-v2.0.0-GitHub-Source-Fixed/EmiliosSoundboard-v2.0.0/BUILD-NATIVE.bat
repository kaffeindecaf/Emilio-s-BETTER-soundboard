@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title Emilio's Soundboard - Build Rust native helper
where cargo >nul 2>nul
if errorlevel 1 (
  echo ERROR: Rust/Cargo was not found.
  echo Install Rust from https://rustup.rs/ and run this script again.
  echo The app still works without it: the managed starfield renderer is the fallback.
  pause
  exit /b 1
)
echo Building emilios_native.dll...
cargo build --release --manifest-path "native\emilios_native\Cargo.toml"
if errorlevel 1 goto failed
if not exist "native\emilios_native\target\release\emilios_native.dll" goto failed
if not exist "EmiliosSoundboard\runtimes\win-x64\native" mkdir "EmiliosSoundboard\runtimes\win-x64\native"
copy /Y "native\emilios_native\target\release\emilios_native.dll" "EmiliosSoundboard\runtimes\win-x64\native\emilios_native.dll" >nul
if errorlevel 1 goto failed
echo Native helper built and copied.
exit /b 0
:failed
echo NATIVE BUILD FAILED.
echo The app will still build and run with the managed renderer.
pause
exit /b 1
