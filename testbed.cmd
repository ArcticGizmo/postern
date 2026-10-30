@echo off
rem Builds and starts the test bed, each part its own process:
rem   Postern (Sandbox)  on pipe postern-sandbox, under its supervisor; only reaches 127.0.0.1, made-up credentials
rem   fake-upstream      on 127.0.0.1:5599, minimised; stands in for GitHub, Jira, Slack, Octopus and Anthropic
rem   Sallyport          the window that sends scenarios through Postern and soaks while you crash it
rem
rem   testbed                    start it
rem   testbed --reset-sandbox    start from a fresh sandbox (new connections and credentials, no approvals)
rem
rem Quit Postern (Dev) and Postern (Sandbox) first (tray, Quit): they run from the same bin folder, which the build
rem replaces. Sallyport asks to be approved the first time; the prompt is Postern's own.
setlocal
set root=%~dp0
dotnet build "%root%src\Postern.App" -nologo -v q || exit /b 1
dotnet build "%root%tools\Postern.FakeUpstream" -nologo -v q || exit /b 1
dotnet build "%root%tools\Postern.Sallyport" -nologo -v q || exit /b 1

start "fake-upstream" /min "%root%tools\Postern.FakeUpstream\bin\Debug\net10.0\fake-upstream.exe"
start "" "%root%src\Postern.App\bin\Debug\net10.0-windows\Postern.exe" --sandbox --supervise %*
start "" "%root%tools\Postern.Sallyport\bin\Debug\net10.0-windows\sallyport.exe"
