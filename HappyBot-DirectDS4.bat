@echo off
set "HAPPYBOT_EXE=%~dp0bin\Release\net10.0-windows\HappyBot.exe"
if not exist "%HAPPYBOT_EXE%" set "HAPPYBOT_EXE=%~dp0bin\Debug\net10.0-windows\HappyBot.exe"
if not exist "%HAPPYBOT_EXE%" (
    echo HappyBot.exe was not found. Build it first:
    echo     dotnet build HappyBot.csproj --configuration Release
    pause
    exit /b 1
)
start "" "%HAPPYBOT_EXE%"
