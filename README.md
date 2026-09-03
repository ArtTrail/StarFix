# StarFix

Plate solving via Gaia DR3 + astroalign — a Windows desktop app that finds the exact World Coordinate System (WCS) for astronomical FITS images, matching them against a local, offline copy of the Gaia DR3 catalog.

![StarFix main window](docs/screenshots/main-window.png)

## Features

- **Single-file and batch plate solving** against a self-built, fully offline Gaia DR3 catalog (down to magnitude 17) — no internet connection needed once the catalog is installed.
- **Fast batch solving**: within a same-target batch, each file after the first tries a shortcut first (reusing the previous file's solved position, rotation, and parity) before falling back to a full geometric search — often near-instant after the first file. Every retry attempt also races several detection/match candidates concurrently instead of trying them one at a time.
- **Overwrite or new-file output modes** — write the WCS into the original file (matching ASTAP's default behavior) or into a numbered copy, leaving the original untouched.
- **Already-solved detection** — batch and single-file solving both check for files that appear already solved and ask before re-solving them.
- **Live Results panel** with the full solve summary (center coordinates, pixel scale, field of view, rotation, matched star count, RMS residual), persisted across restarts.
- **Built-in Gaia catalog manager** with byte-exact integrity verification per downloaded file.
- **Diagnostics log, User Guide, and Revision History** built into the app.

![Batch Solve window](docs/screenshots/batch-solve.png)

## How it works

StarFix's solver detects stars in the image (photutils' DAOStarFinder), queries the local HEALPix-indexed Gaia DR3 catalog near a position hint, projects catalog positions onto a tangent plane, and matches the two star patterns geometrically (astroalign) to derive the WCS. The full pipeline, algorithms, and academic references are documented in-app under Help → User Guide.

## Requirements

- Windows (the bundled solver is a Windows executable; the app itself targets .NET 8 / Avalonia)
- The Gaia DR3 catalog (~4.2 GB), downloaded once via Tools → Download Gaia Catalog

## Installation

Download the latest release from the [Releases](../../releases) page — it ships as a ready-to-run build with the solver already bundled.

## Building from source

This repository contains the StarFix Avalonia (C#/.NET 8) application source. The plate-solving engine itself is a separate Python project, built with PyInstaller into a standalone `solve.exe` and bundled at build time into `PySolver\solve\` (not included in this repo — see the Releases page for a build that already has it, or provide your own compatible `solve.exe` there).

```
dotnet build
```

## License

MIT — see [LICENSE](LICENSE).

## Acknowledgements

- **[Gaia DR3](https://www.cosmos.esa.int/web/gaia/dr3)** (ESA) — astrometric reference catalog
- **[astroalign](https://github.com/quatrope/astroalign)** — asterism/triangle-matching star-pattern registration (MIT license)
- **[astropy](https://www.astropy.org/)** / **[photutils](https://photutils.readthedocs.io/)** — FITS I/O, WCS fitting, and star detection

Full academic references for the algorithms used (DAOFIND/Stetson 1987, the FITS WCS standard, HEALPix, etc.) are listed in-app under Help → User Guide.

---

Developed entirely with [Claude Code](https://claude.com/claude-code) (Anthropic).
