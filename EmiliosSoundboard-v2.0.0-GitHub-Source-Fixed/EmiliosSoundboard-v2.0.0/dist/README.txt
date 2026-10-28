Emilio's Soundboard - Windows x64

Run EmiliosSoundboard.exe. This is a self-contained build: the .NET 8
runtime, app assets and the optional Rust starfield helper are bundled.

Troubleshooting:
  EmiliosSoundboard.exe --verbose     write detailed logs
  EmiliosSoundboard.exe --safe-mode   disable animations/native acceleration
  Logs: %LOCALAPPDATA%\EmiliosSoundboard\logs

The app automatically uses its managed starfield renderer if the bundled
Rust DLL cannot be loaded. See the main README for fallback details.
