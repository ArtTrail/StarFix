@echo off
REM Builds the frozen, standalone solve.exe for StarFix, in a clean venv containing only
REM the solver's real dependencies (no unrelated packages, keeps the freeze small — see
REM plate_solver.md). Copies the result into ..\StarFix\PySolver\solve\.
REM
REM --collect-all photutils is required, not optional: photutils reads its own CITATION.rst
REM at import time (crashes without --collect-data) and has compiled Cython submodules
REM PyInstaller's static analysis misses on its own (crashes without the binaries/submodules
REM --collect-all also pulls in) — both confirmed by directly running the frozen exe and
REM hitting each failure in turn before landing on --collect-all as the fix.

setlocal
cd /d "%~dp0"

if exist .buildenv rmdir /s /q .buildenv
python -m venv .buildenv
.buildenv\Scripts\python.exe -m pip install --quiet numpy scipy astropy astroalign photutils astropy_healpix pyinstaller

if exist build rmdir /s /q build
if exist dist rmdir /s /q dist
if exist solve.spec del solve.spec

.buildenv\Scripts\pyinstaller.exe --onedir --noconfirm --clean --name solve --collect-all photutils solve.py

if exist ..\StarFix\PySolver\solve rmdir /s /q ..\StarFix\PySolver\solve
xcopy /e /i /q dist\solve ..\StarFix\PySolver\solve

echo.
echo Build complete: ..\StarFix\PySolver\solve\solve.exe
endlocal
