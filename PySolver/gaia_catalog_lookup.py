"""Local Gaia catalog lookup, backed by the self-built HEALPix-pixel catalog
(gaia_catalog/, built by build_catalog.py directly from ESA's own open
gaiadr3.gaia_source_lite table) instead of a live Gaia TAP query or the
PixInsight XPSD files. Copyright-clean and safe to redistribute via GitHub
for TransitLab users to download — see plate_solver.md.

Drop-in replacement for catalog.query_region()'s interface, used by solve.py.
"""
import os

import astropy.units as u
import astropy_healpix as ah
import numpy as np

_DEFAULT_CATALOG_DIR = os.path.join(os.path.dirname(__file__), "gaia_catalog")
# StarFix (the standalone GUI app) downloads the catalog to its own AppData folder and
# passes this env var when invoking the frozen solver exe; falls back to the relative
# default for direct `python solve.py` CLI usage during development.
_CATALOG_DIR = os.environ.get("STARFIX_GAIA_CATALOG_DIR") or _DEFAULT_CATALOG_DIR
_HEALPIX_LEVEL = 2
_NSIDE = 2 ** _HEALPIX_LEVEL
_HP = ah.HEALPix(nside=_NSIDE, order="nested")

# True max center-to-corner distance across all 192 level=2 pixels, computed
# directly in build_catalog.py (max at pixel 53) and used there to size the
# bounding circle each pixel file was built from. A pixel whose center is
# within (search radius + this) of the search point might overlap the
# search circle, so must be checked/loaded — same margin the build used.
_MAX_PIXEL_CORNER_DEG = 14.57

_pixel_cache = {}


def _angsep_deg(ra0, dec0, ra1, dec1):
    r0, d0 = np.radians(ra0), np.radians(dec0)
    r1, d1 = np.radians(ra1), np.radians(dec1)
    cos_s = np.sin(d0) * np.sin(d1) + np.cos(d0) * np.cos(d1) * np.cos(r0 - r1)
    return np.degrees(np.arccos(np.clip(cos_s, -1.0, 1.0)))


def _overlapping_pixels(ra_deg: float, dec_deg: float, radius_deg: float):
    all_pixels = np.arange(_HP.npix)
    lon, lat = _HP.healpix_to_lonlat(all_pixels)
    sep = _angsep_deg(ra_deg, dec_deg, lon.to(u.deg).value, lat.to(u.deg).value)
    return all_pixels[sep <= radius_deg + _MAX_PIXEL_CORNER_DEG]


def _load_pixel(pixel: int):
    if pixel not in _pixel_cache:
        path = os.path.join(_CATALOG_DIR, f"pixel_{pixel:04d}.npz")
        if not os.path.exists(path):
            _pixel_cache[pixel] = None
        else:
            d = np.load(path)
            _pixel_cache[pixel] = (d["source_id"], d["ra"], d["dec"], d["pmra"], d["pmdec"], d["mag"])
    return _pixel_cache[pixel]


def query_region(ra_deg: float, dec_deg: float, radius_deg: float, max_stars: int = 2000):
    """Same interface/return shape as catalog.query_region(): a list of
    (source_id, ra_deg, dec_deg, pmra_mas_yr, pmdec_mas_yr, g_mag) tuples,
    sorted brightest first. source_id is the real Gaia DR3 source_id, unlike
    the XPSD-backed version which used a synthetic placeholder.
    """
    pixels = _overlapping_pixels(ra_deg, dec_deg, radius_deg)
    if len(pixels) == 0:
        return []

    all_source_id, all_ra, all_dec, all_pmra, all_pmdec, all_mag = [], [], [], [], [], []
    for pixel in pixels:
        data = _load_pixel(int(pixel))
        if data is None:
            continue
        source_id, ra, dec, pmra, pmdec, mag = data
        sep = _angsep_deg(ra_deg, dec_deg, ra, dec)
        mask = sep <= radius_deg
        if not np.any(mask):
            continue
        all_source_id.append(source_id[mask])
        all_ra.append(ra[mask])
        all_dec.append(dec[mask])
        all_pmra.append(pmra[mask])
        all_pmdec.append(pmdec[mask])
        all_mag.append(mag[mask])

    if not all_ra:
        return []

    source_id = np.concatenate(all_source_id)
    ra = np.concatenate(all_ra)
    dec = np.concatenate(all_dec)
    pmra = np.concatenate(all_pmra)
    pmdec = np.concatenate(all_pmdec)
    mag = np.concatenate(all_mag)

    order = np.argsort(mag)[:max_stars]
    return [
        (int(source_id[i]), float(ra[i]), float(dec[i]), float(pmra[i]), float(pmdec[i]), float(mag[i]))
        for i in order
    ]
