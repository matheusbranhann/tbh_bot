@echo off
REM Publica o painel TbhBot.App como UM exe self-contained (nao precisa .NET instalado na maquina do usuario).
REM Saida: dist\TbhBot.App.exe
cd /d "%~dp0"
dotnet publish src\TbhBot.App -c Release -r win-x64 --self-contained ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o dist
echo.
echo Done. Output: dist\TbhBot.App.exe
