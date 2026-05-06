@echo off
setlocal

title Captain's Log Dashboard

set "ROOT=%~dp0"
set "DASHBOARD=%ROOT%cl-dashboard"
set "NODE=%ROOT%node\node.exe"

if not exist "%NODE%" (
    echo.
    echo Bundled Node was not found.
    echo Expected:
    echo %NODE%
    echo.
    echo Please make sure the release folder contains:
    echo node\node.exe
    echo.
    pause
    exit /b 1
)

if not exist "%DASHBOARD%\server.js" (
    echo.
    echo Dashboard server file was not found.
    echo Expected:
    echo %DASHBOARD%\server.js
    echo.
    pause
    exit /b 1
)

echo Starting Captain's Log dashboard...
echo.
echo Dashboard URL:
echo http://localhost:3030
echo.
echo Leave this window open while using the dashboard.
echo Close this window to stop the dashboard.
echo.

cd /d "%DASHBOARD%"

start "" "http://localhost:3030"

"%NODE%" server.js

echo.
echo Dashboard stopped.
pause