@echo off
rem Builds and runs the dev (Debug) tray app, Postern (Dev) on pipe postern-dev. Arguments go to Postern.exe:
rem
rem   run                              start the dev tray
rem   run --headless                   broker only, no tray (registrations are denied)
rem   run --supervise                  restart it if it crashes
rem   run --dev-auto-approve a,b       approve these app names without a prompt
rem
rem Quit any Postern (Dev) that's already running first: it locks the bin folder, so the build can't replace it,
rem and it owns the pipe, so a second copy would only say it's already running.
rem Works from any folder (the project path is relative to this script). The "--" keeps Postern's arguments away
rem from `dotnet run`.
dotnet run --project "%~dp0src\Postern.App" -- %*
