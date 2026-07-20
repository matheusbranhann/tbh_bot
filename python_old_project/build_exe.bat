@echo off
REM Build tbh_bot into a single TBH_Panel.exe (PyInstaller onefile). Run from this folder.
cd /d "%~dp0"
python -m PyInstaller --onefile --windowed --name TBH_Panel --noconfirm ^
  --collect-all customtkinter --collect-all winsdk --collect-submodules pymem ^
  --hidden-import tbh_core --hidden-import tbh_overlay --hidden-import market_db --hidden-import tbh_rune_assets ^
  --add-data "item_prices.json;." --add-data "market_prices.json;." ^
  --add-data "_cache_bundle;cache" ^
  --add-data "tools\Il2CppDumper\Il2CppDumper.exe;tools\Il2CppDumper" ^
  --add-data "tools\Il2CppDumper\config.json;tools\Il2CppDumper" ^
  tbh_panel.py
echo.
echo Done. Output: dist\TBH_Panel.exe
pause
