@echo off
REM ==========================================================================
REM  TBH.bat - Lancador do painel tbh_bot.
REM  Prefere o Python do ambiente isolado (.venv) criado pelo INSTALL.bat.
REM  Se as libs nao estiverem instaladas, manda rodar o INSTALL.bat.
REM  NUNCA usa python3 (stub da Microsoft Store). Em erro faz "pause".
REM ==========================================================================
setlocal EnableExtensions
title tbh_bot
cd /d "%~dp0"
set "VENVPY=%~dp0.venv\Scripts\python.exe"

REM --- 1) ambiente isolado (.venv), o caminho normal ------------------------
if not exist "%VENVPY%" goto sys
"%VENVPY%" -c "import customtkinter" >nul 2>nul
if errorlevel 1 goto sys
"%VENVPY%" "%~dp0tbh_panel.py"
if errorlevel 1 pause
goto :eof

REM --- 2) fallback: Python do sistema, so se as libs existirem -------------
:sys
py -3 -c "import customtkinter" >nul 2>nul
if errorlevel 1 goto trypython
py -3 "%~dp0tbh_panel.py"
if errorlevel 1 pause
goto :eof

:trypython
python -c "import customtkinter" >nul 2>nul
if errorlevel 1 goto notinstalled
python "%~dp0tbh_panel.py"
if errorlevel 1 pause
goto :eof

REM --- 3) nada instalado: instruir o usuario -------------------------------
:notinstalled
echo.
echo  ============================================================
echo   O tbh_bot ainda nao esta instalado.
echo   De DUPLO-CLIQUE em INSTALL.bat uma vez (instala tudo sozinho).
echo   Depois abra este TBH.bat de novo.
echo  ============================================================
echo.
pause
goto :eof
