@echo off
setlocal
title tbh_bot - Instalador
cd /d "%~dp0"

echo ============================================================
echo   tbh_bot - Instalador automatico
echo   Nao precisa digitar nada. So aguarde.
echo ============================================================
echo.

REM --- procura um Python 64-bit, versao 3.9+ (nunca usa python3 = stub da Store) ---
set "PY="
call :try py -3.12
call :try py -3.11
call :try py -3.13
call :try py -3
call :try python
if defined PY goto run

echo Python 64-bit nao foi encontrado.
echo Tentando instalar o Python 3.12 automaticamente (via winget)...
echo.
where winget >nul 2>nul
if errorlevel 1 goto manual
winget install -e --id Python.Python.3.12 --scope user --accept-package-agreements --accept-source-agreements --disable-interactivity
echo.
echo Reprocurando o Python recem-instalado...
call :try py -3.12
call :try py -3
call :trypath "%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
call :trypath "%LOCALAPPDATA%\Programs\Python\Python311\python.exe"
if defined PY goto run
goto manual

:run
echo Usando o Python: %PY%
echo.
%PY% "%~dp0install.py"
set "RC=%ERRORLEVEL%"
echo.
if "%RC%"=="0" (
  echo ============================================================
  echo  Tudo certo! Agora de DUPLO-CLIQUE em TBH.bat para abrir.
  echo ============================================================
) else (
  echo ============================================================
  echo  A instalacao teve um problema. Leia as mensagens acima.
  echo ============================================================
)
echo.
pause
exit /b %RC%

REM --- sub-rotinas -----------------------------------------------------------
:try
REM  %* = lancador (ex.: "py -3.12"). Aceita se rodar Python 64-bit ^>= 3.9.
if defined PY goto :eof
%* -c "import sys,struct;sys.exit(0 if sys.version_info>=(3,9) and struct.calcsize('P')==8 else 1)" >nul 2>nul
if not errorlevel 1 set "PY=%*"
goto :eof

:trypath
REM  %~1 = caminho completo de um python.exe. Mesmo teste do :try.
if defined PY goto :eof
if not exist "%~1" goto :eof
"%~1" -c "import sys,struct;sys.exit(0 if sys.version_info>=(3,9) and struct.calcsize('P')==8 else 1)" >nul 2>nul
if not errorlevel 1 set "PY=%~1"
goto :eof

:manual
echo.
echo ============================================================
echo  Nao foi possivel instalar o Python automaticamente.
echo.
echo  Faca isto uma vez so:
echo    1) Abra:  https://www.python.org/downloads/
echo    2) Baixe o Python 3.12 (Windows installer 64-bit).
echo    3) No instalador, MARQUE a caixa "Add python.exe to PATH".
echo    4) Conclua e rode este INSTALL.bat de novo.
echo ============================================================
echo.
pause
exit /b 1
