# PySolver — StarFix's Python plate-solving engine

The Python source for StarFix's headless plate solver. At build time it is frozen
with PyInstaller into `PySolver/solve/` (a standalone `solve` executable) and the
C# StarFix app invokes it as a subprocess — end users never run Python directly.

## Source
- `solve.py` — entry point / solver orchestration
- `detect.py` — star detection
- `projection.py` — WCS / sky-to-pixel projection
- `gaia_catalog_lookup.py` — offline Gaia DR3 catalog lookup
- `build_catalog.py` — builds the offline Gaia DR3 catalog (large; the built catalog is NOT tracked here)

## Building the frozen solver
- Windows: `pyinstaller solve.spec` (or `build_solver.bat`)
- Linux:   `pyinstaller solve.linux.spec`
- `test_full.sh` — Linux smoke test

Key deps (see the `.spec` files): numpy, astropy, scipy, astroalign, photutils.
