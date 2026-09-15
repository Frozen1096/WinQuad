@echo off
chcp 65001 >nul
rem ═══════════════════════════════════════════════════════════════
rem  启动桌面四宫格覆盖层。
rem  管理器里点「重启覆盖层」效果一样；这个文件是给
rem  「只想把四宫格叫出来，不想开管理器」的时候用的。
rem
rem  注意：请用普通权限双击运行。以管理员身份运行的话，
rem        四宫格会挂在提权进程里，行为可能和预期不一致。
rem ═══════════════════════════════════════════════════════════════
cd /d "%~dp0"

tasklist /fi "imagename eq WinQuad.exe" 2>nul | find /i "WinQuad.exe" >nul
if not errorlevel 1 (
    echo 四宫格已经在运行了。要重启请到管理器里点「重启覆盖层」。
    timeout /t 3 >nul
    exit /b
)

start "" "WinQuad.exe" --overlay
exit /b
