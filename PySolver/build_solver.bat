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
REM
REM Issue #12: dependencies are installed from the pinned requirements.txt (the known-good set
REM captured from the venv the shipped solve.exe was built in), NOT an unpinned
REM "pip install numpy scipy astropy ...". An unpinned install silently floats to whatever is
REM current on PyPI; photutils 3.x renamed DAOStarFinder's output columns (xcentroid/ycentroid),
REM which breaks detect.py with KeyError on every solve — this already hit the macOS build when
REM a fresh venv pulled photutils 3.0.0. Pinning makes the build reproducible and immune to that.

setlocal
cd /d "%~dp0"

if exist .buildenv rmdir /s /q .buildenv
python -m venv .buildenv
.buildenv\Scripts\python.exe -m pip install --quiet -r requirements.txt

if exist build rmdir /s /q build
if exist dist rmdir /s /q dist
if exist solve.spec del solve.spec

.buildenv\Scripts\pyinstaller.exe --onedir --noconfirm --clean --name solve --collect-all photutils solve.py

REM Destination = the PySolver\solve folder of the newest "StarFix v*" app folder one level up
REM (dir /o:-n sorts names descending, so v1.2.0 wins over v1.1.1), or an explicit path passed as
REM the first argument. The old hardcoded "..\StarFix\PySolver\solve" predated the per-version
REM app folders (StarFix v1.1.1, StarFix v1.2.0, ...) and no longer pointed at a real folder.
set "DEST=%~1"
if "%DEST%"=="" (
  for /f "delims=" %%D in ('dir /b /a:d /o:-n "..\StarFix v*" 2^>nul') do (
    set "DEST=..\%%D\PySolver\solve"
    goto :gotdest
  )
)
:gotdest
if "%DEST%"=="" (
  echo ERROR: could not find a "StarFix v*" app folder to copy into, and no target was given.
  echo The frozen build is in dist\solve — copy it into your app's PySolver\solve manually.
  endlocal & exit /b 1
)

if exist "%DEST%" rmdir /s /q "%DEST%"
xcopy /e /i /q dist\solve "%DEST%"

echo.
echo Build complete: %DEST%\solve.exe
endlocal
