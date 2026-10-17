@echo off
setlocal
rem Newfie's Server Suite restart loop for a directly hosted Vintage Story server.
rem Edit VS_SERVER and DATA_PATH for your installation. Close this wrapper when you
rem intentionally want the server to remain offline.

set "VS_SERVER=C:\path\to\VintagestoryServer.exe"
set "DATA_PATH=C:\path\to\vintagestory\data"
set "RESTART_DELAY_SECONDS=5"

:restart
 echo [NSE restart loop] Starting Vintage Story server...
 "%VS_SERVER%" --dataPath "%DATA_PATH%"
 echo [NSE restart loop] Server exited. Restarting in %RESTART_DELAY_SECONDS%s...
 timeout /t %RESTART_DELAY_SECONDS% /nobreak >nul
goto restart
