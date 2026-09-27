"""Build a local Gaia DR3 catalog, partitioned into HEALPix pixel files.

GAIA_HEALPIX_INDEX(level, source_id) as a WHERE filter was tried first and
confirmed too slow to use (15+ minutes with no result on a single pixel,
almost certainly an unindexed full-table scan across all 1.8B rows) —
whereas this project's many CONTAINS(POINT,CIRCLE) cone searches have
consistently completed in single-digit seconds throughout, confirmed
directly with a fresh timing test (10.1s). So: query a bounding CIRCLE
around each pixel's center (fast, spatially indexed) that's guaranteed to
fully contain the pixel, then filter locally to exact HEALPix membership
via astropy_healpix — trading some download overage (~1.85x the pixel's
true area) for a query that actually completes.

Real root cause of the original slowness, found after much confusion: it was
specifically the ASYNC job path (Gaia.launch_job_async), not query size or
indexing. Confirmed directly: the exact same ~1.82M-row circle query that
stalled 20-30+ minutes via async completed in 113s via a plain SYNC query
(Gaia.launch_job) with an explicit large TOP clause. Sync jobs on this
archive appear to go through an entirely different, much faster path than
async for the same work. So: level=2 (nside=4, 192 pixels, ~215 sq deg
each) is back in play and is actually the better choice now that per-query
cost is dominated by data volume, not per-query fixed overhead — fewer,
bigger sync queries beat many smaller ones. ~113s/pixel x 192 pixels =~ 6hr
full-sky build.
"""
import argparse
import os
import time

import astropy.units as u
import astropy_healpix as ah
import numpy as np
from astroquery.gaia import Gaia

HEALPIX_LEVEL = 2
NSIDE = 2 ** HEALPIX_LEVEL
# 11.5deg (based on sampling only pixel 117) was WRONG — confirmed by checking
# all 192 pixels directly that the true max center-to-corner distance is
# 14.57deg (pixel 53). Using 11.5 silently clipped stars near the edges of
# many pixels (real bug, found via a ~40% shortfall vs the expected G<17
# total star count: 95.8M built vs ~157.7M expected). 16.0 gives real margin.
BOUNDING_RADIUS_DEG = 16.0
ROW_CAP_BUFFER = 1.10  # TOP is set to count*this, not a fixed cap

_HP = ah.HEALPix(nside=NSIDE, order="nested")


class TruncatedResultError(Exception):
    pass


# Confirmed directly from the TAP server's own /capabilities document:
# <outputLimit><default unit="row">3000000</default><hard unit="row">3000000
# </hard></outputLimit> — an absolute server-side ceiling, not a client-side
# setting, not overridable by any TOP clause. Pixel 60 (near the galactic
# plane) has a true count of 5,875,768 — no single query can ever return
# that completely. SAFE_COUNT_LIMIT stays well under the hard 3M so a single
# fetch is never at risk of hitting it.
SAFE_COUNT_LIMIT = 1_500_000


def _fetch_validated(spatial_where: str, mag_lo: float, mag_hi: float, label: str, depth: int = 0) -> "Table":
    """Fetch all rows matching spatial_where with mag in [mag_lo, mag_hi),
    recursively splitting the magnitude range if the true count would risk
    the server's hard 3M row limit, and validating every leaf fetch against
    its own COUNT(*) (confirmed necessary: this archive can silently return
    truncated results with no error at all under sustained load — a
    "looks plausible but wrong" truncation, e.g. 654045 -> 222131 stars for
    the same pixel across two runs, would pass any simple low-count check).
    """
    where = f"{spatial_where} AND phot_g_mean_mag >= {mag_lo} AND phot_g_mean_mag < {mag_hi} AND phot_g_mean_mag IS NOT NULL"
    count_job = Gaia.launch_job(f"SELECT COUNT(*) as n FROM gaiadr3.gaia_source_lite WHERE {where}")
    expected = int(count_job.get_results()["n"][0])

    if expected > SAFE_COUNT_LIMIT and mag_hi - mag_lo > 0.01:
        mag_mid = (mag_lo + mag_hi) / 2
        print(f"  {label}: {expected} rows exceeds safe limit, splitting mag [{mag_lo:.2f},{mag_hi:.2f}) "
              f"at {mag_mid:.2f}", flush=True)
        t1 = _fetch_validated(spatial_where, mag_lo, mag_mid, label + "a", depth + 1)
        t2 = _fetch_validated(spatial_where, mag_mid, mag_hi, label + "b", depth + 1)
        from astropy.table import vstack
        return vstack([t1, t2])

    top = max(int(expected * ROW_CAP_BUFFER), 1000)
    query = f"""
    SELECT TOP {top} source_id, ra, dec, pmra, pmdec, phot_g_mean_mag
    FROM gaiadr3.gaia_source_lite
    WHERE {where}
    """
    print(f"  {label}: submitting sync query, mag [{mag_lo:.2f},{mag_hi:.2f}), expected {expected} rows...",
          flush=True)
    job = Gaia.launch_job(query)
    table = job.get_results()
    print(f"  {label}: got {len(table)} rows (expected {expected})", flush=True)

    if len(table) != expected:
        raise TruncatedResultError(
            f"{label}: fetched {len(table)} rows but COUNT(*) said {expected} — likely silent server-side truncation")

    return table


def build_pixel(pixel: int, mag_limit: float, out_dir: str) -> int:
    lon, lat = _HP.healpix_to_lonlat(pixel)
    ra_center, dec_center = lon.to(u.deg).value, lat.to(u.deg).value
    spatial_where = f"1=CONTAINS(POINT('ICRS', ra, dec), CIRCLE('ICRS', {ra_center}, {dec_center}, {BOUNDING_RADIUS_DEG}))"

    table = _fetch_validated(spatial_where, -5.0, mag_limit, f"pixel{pixel}")
    print(f"  pixel {pixel}: {len(table)} total rows in bounding circle, "
          f"filtering to exact pixel + writing...", flush=True)

    # keep only rows that truly belong to this pixel (the bounding circle is
    # larger than the pixel, by design)
    row_pixels = _HP.lonlat_to_healpix(np.array(table["ra"]) * u.deg, np.array(table["dec"]) * u.deg)
    table = table[row_pixels == pixel]
    n = len(table)

    source_id = np.array(table["source_id"], dtype=np.int64)
    ra = np.array(table["ra"], dtype=np.float32)
    dec = np.array(table["dec"], dtype=np.float32)
    pmra_col = table["pmra"]
    pmdec_col = table["pmdec"]
    pmra = np.array(pmra_col.filled(0.0) if hasattr(pmra_col, "filled") else pmra_col, dtype=np.float32)
    pmdec = np.array(pmdec_col.filled(0.0) if hasattr(pmdec_col, "filled") else pmdec_col, dtype=np.float32)
    mag = np.array(table["phot_g_mean_mag"], dtype=np.float32)

    path = os.path.join(out_dir, f"pixel_{pixel:04d}.npz")
    np.savez(path, source_id=source_id, ra=ra, dec=dec, pmra=pmra, pmdec=pmdec, mag=mag)
    return n


def main():
    parser = argparse.ArgumentParser(description="Build local Gaia HEALPix catalog")
    parser.add_argument("pixels", nargs="+", type=int, help="HEALPix pixel indices to build (level=2, 0-191)")
    parser.add_argument("--mag-limit", type=float, default=17.0)
    parser.add_argument("--out-dir", default=os.path.join(os.path.dirname(__file__), "gaia_catalog"))
    args = parser.parse_args()

    os.makedirs(args.out_dir, exist_ok=True)
    t_start = time.time()
    for i, pixel in enumerate(args.pixels):
        out_path = os.path.join(args.out_dir, f"pixel_{pixel:04d}.npz")
        if os.path.exists(out_path):
            print(f"[{i+1}/{len(args.pixels)}] pixel {pixel}: already built, skipping", flush=True)
            continue
        print(f"[{i+1}/{len(args.pixels)}] starting pixel {pixel}...", flush=True)
        t0 = time.time()
        for attempt in range(1, 4):
            try:
                n = build_pixel(pixel, args.mag_limit, args.out_dir)
                print(f"[{i+1}/{len(args.pixels)}] pixel {pixel}: {n} stars in {time.time()-t0:.1f}s "
                      f"(total elapsed {time.time()-t_start:.0f}s)", flush=True)
                break
            except Exception as e:
                print(f"[{i+1}/{len(args.pixels)}] pixel {pixel}: attempt {attempt} FAILED "
                      f"({type(e).__name__}: {e})", flush=True)
                if attempt == 3:
                    print(f"[{i+1}/{len(args.pixels)}] pixel {pixel}: giving up after 3 attempts, "
                          f"continuing to next pixel", flush=True)
                else:
                    time.sleep(30 * attempt)
        time.sleep(3)  # brief pause between pixels — reduce load-induced silent truncation risk


if __name__ == "__main__":
    main()
