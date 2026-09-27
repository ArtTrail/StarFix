"""Directed plate solver MVP.

Pipeline: detect stars (photutils) -> fetch nearby Gaia stars (live TAP query)
-> project catalog stars to a local tangent plane -> match star patterns via
astroalign (asterism/triangle matching) -> fit a TAN WCS from the matched
pixel<->sky pairs (astropy) -> report fit quality, optionally write to header.

Directed solving (an RA/Dec hint + search radius, like ASTAP's -r mode) is
the only mode supported — an undirected/blind mode (no hint at all) was
prototyped and rejected: a brute-force whole-sky grid search using the
existing 192-pixel HEALPix catalog partitioning doesn't work for any real
narrow-field image (confirmed directly — the grid cells are tens of times
wider than a typical telescope FOV, so the top-N-brightest-stars-per-cell
subset used for matching essentially never contains the actual field's
stars). A real blind solve needs a proper geometric-hash index like
Astrometry.net/ASTAP use, which is out of scope for now.
"""
import argparse
import json
import os
import pickle
import re
import subprocess
import sys
import tempfile
import time
from concurrent.futures import ThreadPoolExecutor, FIRST_COMPLETED, wait as futures_wait

import numpy as np
from astropy.io import fits
from astropy.time import Time, TimeDelta
from astropy.coordinates import SkyCoord
from astropy.wcs import WCS
from astropy.wcs.utils import fit_wcs_from_points
from scipy.spatial import cKDTree
import astroalign

import gaia_catalog_lookup as catalog
import detect
import projection


def _run_match_worker(in_path: str, out_path: str) -> None:
    """The actual body of the match-worker subprocess — invoked either as
    `solve.py --_match-worker <in> <out>` (dev) or `solve.exe --_match-worker
    <in> <out>` (frozen). Dispatched from the bottom of this file rather than
    shelling out to a separate _match_worker.py script, because a frozen
    PyInstaller exe's sys.executable IS the exe itself (there is no separate
    python.exe to hand a .py path to) — confirmed directly: the original
    subprocess.run([sys.executable, _WORKER_SCRIPT, ...]) approach failed
    every time under the frozen build with "unrecognized arguments", since
    solve.exe's own argparse tried (and failed) to parse the worker script's
    path as its normal CLI args. Re-invoking this same exe with a distinct
    leading flag sidesteps that entirely and works identically frozen or not.
    """
    with open(in_path, "rb") as f:
        source, target, max_control_points = pickle.load(f)

    try:
        t, (s, tg) = astroalign.find_transform(source, target, max_control_points=max_control_points)
        result = ("ok", t, s, tg)
    except astroalign.MaxIterError as e:
        # Bare astroalign text alone doesn't say whether this failed because almost nothing
        # was detected (thin cloud, sparse field, short exposure) or because plenty of stars
        # were found but still didn't geometrically match (crowding, trailing, a bad position
        # hint) — those point to very different problems. Surfacing the actual counts lets
        # whoever reads the log tell the two apart at a glance instead of guessing.
        result = ("maxiter", f"{e} ({len(source)} detected star(s) vs {len(target)} catalog star(s) considered)")
    except Exception as e:
        result = ("error", type(e).__name__, str(e))

    with open(out_path, "wb") as f:
        pickle.dump(result, f)


def _match_worker_cmd():
    # Frozen: sys.executable IS solve.exe, no script path needed. Unfrozen: sys.executable
    # is a real python.exe, which needs this script's own path as its first argument.
    if getattr(sys, "frozen", False):
        return [sys.executable]
    return [sys.executable, os.path.abspath(__file__)]


def _decode_match_result(result):
    if result[0] == "ok":
        return result[1], (result[2], result[3])
    elif result[0] == "maxiter":
        raise astroalign.MaxIterError(result[1])
    else:
        raise RuntimeError(f"{result[1]}: {result[2]}")


def find_transform_with_timeout(source, target, max_control_points, timeout_sec=30.0):
    """Single blocking attempt — kept for any caller that just wants one plain attempt
    (used by find_transform_race below for the single-candidate case, and available for
    tests/tools). See find_transform_race for the normal multi-candidate path used by solve().
    """
    with tempfile.TemporaryDirectory() as tmpdir:
        in_path = os.path.join(tmpdir, "in.pkl")
        out_path = os.path.join(tmpdir, "out.pkl")
        with open(in_path, "wb") as f:
            pickle.dump((source, target, max_control_points), f)

        cmd = _match_worker_cmd() + ["--_match-worker", in_path, out_path]
        try:
            subprocess.run(cmd, timeout=timeout_sec, capture_output=True, check=True)
        except subprocess.TimeoutExpired:
            raise TimeoutError(f"find_transform exceeded {timeout_sec}s (control_points={max_control_points})")
        except subprocess.CalledProcessError as e:
            raise RuntimeError(f"match worker crashed: {e.stderr.decode(errors='replace')}")

        with open(out_path, "rb") as f:
            result = pickle.load(f)

    return _decode_match_result(result)


def _run_one_race_attempt(item, attempt_timeout_sec, handles, key):
    """One (fwhm, match_cap) candidate's worker process, run inside a ThreadPoolExecutor
    thread — the thread just blocks on subprocess I/O (the real CPU work happens in the
    child process), so plain threads are fine here, no GIL concern. Stores its own Popen
    handle in the shared `handles` dict (keyed by `key`) as its first action, so the
    orchestrator can kill it early the moment a different candidate wins the race.
    """
    source, target, max_control_points, label = item
    with tempfile.TemporaryDirectory() as tmpdir:
        in_path = os.path.join(tmpdir, "in.pkl")
        out_path = os.path.join(tmpdir, "out.pkl")
        with open(in_path, "wb") as f:
            pickle.dump((source, target, max_control_points), f)

        cmd = _match_worker_cmd() + ["--_match-worker", in_path, out_path]
        # Cap each worker's own BLAS/OpenMP thread pool to 1 — without this, several
        # concurrent match-worker subprocesses each try to use every core for their own numpy/
        # scipy linear algebra by default, and the resulting oversubscription (N processes x
        # each spawning up to `cpu_count` threads) makes even a single easy candidate far
        # SLOWER under the race than it was running alone sequentially — confirmed directly:
        # an easy sparse-field solve that used to take a few seconds took 27s once several
        # candidates started racing on this 24-core machine, before this fix. Outer
        # parallelism (multiple processes, which this module controls) and inner parallelism
        # (each process's own BLAS threading, uncontrolled) must not both be left unbounded.
        env = dict(os.environ)
        for var in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS",
                    "NUMEXPR_NUM_THREADS", "VECLIB_MAXIMUM_THREADS"):
            env[var] = "1"
        proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, env=env)
        handles[key] = proc
        try:
            _, stderr = proc.communicate(timeout=attempt_timeout_sec)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.communicate()
            raise TimeoutError(f"find_transform exceeded {attempt_timeout_sec}s ({label})")

        if proc.returncode != 0:
            raise RuntimeError(f"match worker crashed ({label}): {stderr.decode(errors='replace')}")

        with open(out_path, "rb") as f:
            result = pickle.load(f)

    try:
        transform, matched = _decode_match_result(result)
    except astroalign.MaxIterError as e:
        raise astroalign.MaxIterError(f"{e}, fwhm={label[0]:.1f} match_cap={label[1]}")
    return label, transform, matched


def find_transform_race(work_items, attempt_timeout_sec=60.0, overall_budget_sec=180.0, max_workers=None):
    """Races several (source, target, max_control_points, label) candidates concurrently
    instead of trying them strictly one at a time — confirmed via direct testing that a
    single hard attempt can burn most of a minute, and the old sequential design paid that
    cost once per candidate, in priority order, even when a later candidate would have
    succeeded quickly. `work_items` should already be in priority order (cheapest/likeliest
    first) — ThreadPoolExecutor's own FIFO queue means only `max_workers` run at a time and
    the next queued item starts as a slot frees, so priority order is preserved even though
    several candidates are in flight at once; the win is that a slow candidate no longer
    blocks a fast one from starting.

    Returns the first candidate to *succeed*. Every other still-running worker process is
    killed immediately once a winner is found (or the overall budget expires) — a losing
    subprocess left running would otherwise keep burning CPU in the background, which matters
    in the persistent --server session where it would contend with the *next* file's solve.
    """
    if not work_items:
        raise RuntimeError("No candidates to try.")

    max_workers = max_workers or max(1, min(len(work_items), os.cpu_count() or 4, 8))
    handles = {}
    last_error = None
    winner = None

    with ThreadPoolExecutor(max_workers=max_workers) as ex:
        futures = {
            ex.submit(_run_one_race_attempt, item, attempt_timeout_sec, handles, i): i
            for i, item in enumerate(work_items)
        }
        pending = set(futures)
        deadline = time.time() + overall_budget_sec
        try:
            while pending and winner is None:
                remaining = deadline - time.time()
                if remaining <= 0:
                    last_error = TimeoutError(f"Gave up after {overall_budget_sec:.0f}s across candidates")
                    break
                done, pending = futures_wait(pending, timeout=remaining, return_when=FIRST_COMPLETED)
                for fut in done:
                    try:
                        winner = fut.result()
                        break
                    except Exception as e:
                        last_error = e
        finally:
            # Kill every still-running worker — both losers after a win, and everything if
            # we're bailing out on the overall budget.
            for proc in handles.values():
                if proc.poll() is None:
                    try:
                        proc.kill()
                    except Exception:
                        pass

    if winner is None:
        raise last_error or RuntimeError("No successful match found among candidates.")
    return winner


def find_transform_race_interleaved(fwhm_candidates, detect_fn, match_cap_candidates,
                                     match_catalog_full, attempt_timeout_sec=60.0,
                                     overall_budget_sec=180.0, max_workers=None):
    """Like find_transform_race, but detection for each FWHM candidate is submitted to the
    same executor as the match attempts instead of running serially up front. Confirmed
    directly on real data that eager up-front detection for every FWHM candidate (up to
    ~15s total across 4 values on a real field, since a larger FWHM kernel costs more) was
    serializing ahead of the race and regressing easy sparse fields that used to finish in
    a few seconds by only ever detecting fwhm0. Now fwhm0's detection (typically fastest)
    finishes first, its match attempts start racing immediately, and any later FWHM's
    detection/matching only actually costs wall-clock time if the earlier ones don't win.

    `detect_fn(fwhm)` must return (detected, detected_xy) or raise on failure (e.g. too
    few stars) — same contract as a match attempt failing.

    Returns (label, transform, matched, detected_by_fwhm) — detected_by_fwhm only contains
    entries for FWHM candidates whose detection actually completed (a winner found early
    means later candidates' detections may never even be looked at).
    """
    if not fwhm_candidates:
        raise RuntimeError("No candidates to try.")

    max_workers = max_workers or max(
        1, min(len(fwhm_candidates) * (1 + len(match_cap_candidates)), os.cpu_count() or 4, 8))

    handles = {}
    detected_by_fwhm = {}
    winner = None
    last_error = None

    ex = ThreadPoolExecutor(max_workers=max_workers)
    try:
        detect_futures = {ex.submit(detect_fn, fwhm): fwhm for fwhm in fwhm_candidates}
        pending = set(detect_futures)
        deadline = time.time() + overall_budget_sec

        while pending and winner is None:
            remaining = deadline - time.time()
            if remaining <= 0:
                last_error = TimeoutError(f"Gave up after {overall_budget_sec:.0f}s across candidates")
                break
            done, pending = futures_wait(pending, timeout=remaining, return_when=FIRST_COMPLETED)
            for fut in done:
                if fut in detect_futures:
                    candidate_fwhm = detect_futures[fut]
                    try:
                        detected, detected_xy = fut.result()
                    except Exception as e:
                        last_error = e
                        continue
                    detected_by_fwhm[candidate_fwhm] = (detected, detected_xy)
                    for match_cap in match_cap_candidates:
                        match_detected = detected_xy[:match_cap]
                        match_catalog = match_catalog_full[:match_cap]
                        control_points = max(len(match_detected), len(match_catalog))
                        label = (candidate_fwhm, match_cap)
                        item = (match_detected, match_catalog, control_points, label)
                        key = ("match", candidate_fwhm, match_cap)
                        mf = ex.submit(_run_one_race_attempt, item, attempt_timeout_sec, handles, key)
                        pending.add(mf)
                else:
                    try:
                        winner = fut.result()
                        break
                    except Exception as e:
                        last_error = e
    finally:
        # Kill every still-running match subprocess. Any detection thread still in flight
        # can't be force-killed (plain Python threads), so it's abandoned to finish on its
        # own in the background — harmless (read-only numpy work, no side effects) and far
        # cheaper than blocking the caller's return on it via shutdown(wait=True).
        for proc in handles.values():
            if proc.poll() is None:
                try:
                    proc.kill()
                except Exception:
                    pass
        ex.shutdown(wait=False, cancel_futures=True)

    if winner is None:
        raise last_error or RuntimeError("No successful match found among candidates.")
    label, transform, matched = winner
    return label, transform, matched, detected_by_fwhm


def estimate_pixel_scale_arcsec(header):
    """Estimate arcsec/pixel from FOCALLEN/XPIXSZ header keywords, if present.

    XPIXSZ already reports the effective (post-binning) pixel size in this
    pipeline's headers — confirmed against a real 2x2-binned frame's own
    CD-matrix-derived scale (0.515"/px measured vs 1.03"/px if XBINNING were
    applied again here). Do not multiply by XBINNING.
    """
    focallen_mm = header.get("FOCALLEN")
    xpixsz_um = header.get("XPIXSZ")
    if focallen_mm is None or xpixsz_um is None:
        return None
    return 206265.0 * (xpixsz_um / 1000.0) / focallen_mm


def estimate_fov_radius_deg(header, naxis1: int, naxis2: int):
    """Estimate the image's angular half-diagonal. Returns None if the header
    lacks FOCALLEN/XPIXSZ — callers should fall back to treating the
    position-uncertainty radius as the catalog search radius in that case.
    """
    arcsec_per_px = estimate_pixel_scale_arcsec(header)
    if arcsec_per_px is None:
        return None
    width_deg = naxis1 * arcsec_per_px / 3600.0
    height_deg = naxis2 * arcsec_per_px / 3600.0
    return 0.5 * np.hypot(width_deg, height_deg)


_TZ_OFFSET_RE = re.compile(r"^(?P<naive>.+?)(?P<sign>[+-])(?P<hh>\d{2}):?(?P<mm>\d{2})$")


def parse_obs_epoch(header):
    """Return the observation epoch as a decimal year, or None if no usable
    date field is present.

    MObs headers append a timezone offset to DATE-OBS (e.g. "...-0700"),
    which astropy.time.Time's ISO parser rejects outright — confirmed
    directly (ValueError on every MObs file tested). MObs also provides
    UT-OBS, already UTC-converted but with a trailing "-0000" marker for the
    same reason. Preferring UT-OBS avoids needing to trust the offset math
    for the non-UTC field; either way, any trailing +/-HHMM (or HH:MM) is
    stripped and applied as an explicit shift rather than assumed away.
    """
    date_str = header.get("UT-OBS") or header.get("DATE-OBS")
    if not date_str:
        return None

    m = _TZ_OFFSET_RE.match(date_str)
    if not m:
        return Time(date_str).decimalyear

    t = Time(m.group("naive"), format="isot", scale="utc")
    offset_hours = int(m.group("hh")) + int(m.group("mm")) / 60.0
    if m.group("sign") == "-":
        offset_hours = -offset_hours
    # DATE-OBS/UT-OBS express local = UTC + offset, so UTC = local - offset.
    t = t - TimeDelta(offset_hours / 24.0, format="jd")
    return t.decimalyear


def refine_wcs(initial_wcs, all_world: SkyCoord, detected_xy: np.ndarray,
                max_iterations: int = 5, match_tolerance_px: float = 8.0):
    """Grow the match set beyond astroalign's initial asterism matches.

    Projects every catalog star through the current WCS, finds its nearest
    detected star, keeps only mutual-nearest pairs (closest catalog match per
    detected star, to avoid one detected star absorbing several catalog
    stars in a crowded region), refits, and repeats until the match set
    stops growing/changing.

    Returns (wcs, keep_cat_idx, keep_det_idx) — indices into all_world and
    detected_xy respectively for the final match set.
    """
    detected_tree = cKDTree(detected_xy)
    current_wcs = initial_wcs
    prev_match_count = -1
    keep_cat = keep_det = None

    for _ in range(max_iterations):
        px, py = current_wcs.world_to_pixel(all_world)
        cat_pix = np.column_stack([px, py])
        dist, nearest_det = detected_tree.query(cat_pix, k=1)

        within_tol = np.where(dist < match_tolerance_px)[0]
        if len(within_tol) < 3:
            break

        order = within_tol[np.argsort(dist[within_tol])]
        seen_det = set()
        cat_idx_list, det_idx_list = [], []
        for ci in order:
            di = nearest_det[ci]
            if di in seen_det:
                continue
            seen_det.add(di)
            cat_idx_list.append(ci)
            det_idx_list.append(di)

        if len(cat_idx_list) < 3:
            break

        keep_cat = np.array(cat_idx_list)
        keep_det = np.array(det_idx_list)

        if len(keep_cat) == prev_match_count:
            break
        prev_match_count = len(keep_cat)

        px_fit = detected_xy[keep_det, 0] + 1.0
        py_fit = detected_xy[keep_det, 1] + 1.0
        current_wcs = fit_wcs_from_points((px_fit, py_fit), all_world[keep_cat])

    return current_wcs, keep_cat, keep_det


def try_header_seeded_wcs(ra_hint, dec_hint, naxis1, naxis2, pixel_scale_arcsec,
                           rotation_hint_deg, parity_hint, all_world, detected_xy,
                           min_matches=8, max_rms_px=5.0):
    """Builds a candidate WCS directly from already-known values — position hint, known
    pixel scale, and a previous file's rotation/parity in the same batch session — instead
    of always starting refine_wcs's bootstrap from nothing (a full blind astroalign
    triangle search). Skips straight to refine_wcs's proven nearest-neighbor growth/refit
    loop; only ever used when it clearly worked.

    The CD-matrix construction below is the algebraic inverse of
    compute_solution_summary's own rotation_deg/parity formulas (rotation_deg =
    degrees(atan2(cd2_1, cd1_1)); parity from the CD determinant's sign) — verified to
    round-trip a real solved file's own CD matrix exactly (isotropic pixel scale assumed,
    same as every other value this function is fed).

    Returns (wcs, keep_cat, keep_det, rms_px) on success, or None if the seed doesn't clear
    the quality gate (>= min_matches stars, RMS below max_rms_px) — rotation/parity are
    physically stable within a session, but a rejected seed (e.g. after an undetected
    meridian flip) must never be returned as a degraded result, only as None, so the caller
    always falls through to the normal (unmodified) retry ladder untouched.

    min_matches default lowered from an initial 15 to 8 after a real 138-file WASP-52
    session log showed 56% of files land at a final matched-star count below 15 on the full
    ladder (floor observed: 11) — the field itself doesn't have 15 matchable stars on the
    sparser half, so the old threshold was unsatisfiable there and the shortcut never fired
    (flat ~8s/file the whole batch, no fast files at all). RMS stayed healthy throughout
    (mean 1.72px, max 2.58px, well under max_rms_px) confirming the RMS gate was never the
    bottleneck — only the match-count floor was too high for this field's real density.
    """
    s = pixel_scale_arcsec / 3600.0  # deg/px
    theta = np.radians(rotation_hint_deg)
    k = 1.0 if parity_hint == "flipped/mirrored" else -1.0

    cd1_1 = s * np.cos(theta)
    cd2_1 = s * np.sin(theta)
    cd1_2 = -k * s * np.sin(theta)
    cd2_2 = k * s * np.cos(theta)

    seed_wcs = WCS(naxis=2)
    seed_wcs.wcs.crval = [ra_hint, dec_hint]
    seed_wcs.wcs.crpix = [(naxis1 + 1) / 2.0, (naxis2 + 1) / 2.0]
    seed_wcs.wcs.cd = [[cd1_1, cd1_2], [cd2_1, cd2_2]]
    seed_wcs.wcs.ctype = ["RA---TAN", "DEC--TAN"]

    refined_wcs, keep_cat, keep_det = refine_wcs(seed_wcs, all_world, detected_xy)
    if keep_cat is None or len(keep_cat) < min_matches:
        return None

    fit_x, fit_y = refined_wcs.world_to_pixel(all_world[keep_cat])
    residual_px = np.hypot(fit_x - detected_xy[keep_det, 0], fit_y - detected_xy[keep_det, 1])
    rms_px = float(np.sqrt(np.mean(residual_px ** 2)))
    if rms_px > max_rms_px:
        return None

    return refined_wcs, keep_cat, keep_det, rms_px


def _find_image_hdu_index(hdul) -> int:
    """Index of the first HDU with 2D image data — handles both a plain single-HDU FITS
    file (index 0) and a .fz tile-compressed file transparently: a .fz file's primary HDU
    is just an empty NAXIS=0 shell, with the real image living in a CompImageHDU at index
    1, which astropy decompresses on access to .data exactly like an ordinary image HDU.
    Returns -1 if no 2D image HDU is found.
    """
    for i, hdu in enumerate(hdul):
        if hdu.data is not None and hdu.data.ndim == 2:
            return i
    return -1


def solve(filepath: str, ra_hint: float, dec_hint: float, radius_deg: float,
          max_stars: int = 2000, fwhm: float = None, threshold_sigma: float = 6.0,
          assumed_seeing_arcsec: float = 1.5, fwhm_hint: float = None, match_cap_hint: int = None,
          rotation_hint_deg: float = None, parity_hint: str = None,
          center_hint_ra_deg: float = None, center_hint_dec_deg: float = None):
    with fits.open(filepath) as hdul:
        data = None
        header = None
        idx = _find_image_hdu_index(hdul)
        if idx >= 0:
            data = hdul[idx].data.astype(float)
            header = hdul[idx].header
        if data is None:
            raise ValueError(f"No 2D image data found in {filepath}")

    pixel_scale = estimate_pixel_scale_arcsec(header)

    if fwhm is None:
        base_fwhm = 6.0 if pixel_scale is None else np.clip(assumed_seeing_arcsec / pixel_scale, 3.0, 20.0)
        # A single fixed-seeing estimate doesn't hold across every field —
        # confirmed empirically (a sparse field failed to match at the
        # estimated FWHM but succeeded at ~1.2x it) — so retry progressively
        # larger FWHM values rather than trusting one guess.
        fwhm_candidates = [base_fwhm * m for m in (1.0, 1.3, 1.7, 2.2)]
        # A batch server session (StarFix's --server mode) passes the FWHM that worked for
        # the previous file in the same run as a head-start — same equipment/seeing usually
        # means it works again immediately, skipping the ladder entirely. It's tried FIRST,
        # never as a replacement for the ladder: seeing does genuinely vary between frames
        # (that's the whole reason the ladder exists), so if the hint fails, every candidate
        # above still runs as a fallback exactly as before.
        if fwhm_hint is not None:
            fwhm_candidates = [fwhm_hint] + fwhm_candidates
    else:
        fwhm_candidates = [fwhm]

    fov_radius_deg = estimate_fov_radius_deg(header, data.shape[1], data.shape[0])
    cone_radius_deg = radius_deg if fov_radius_deg is None else fov_radius_deg + radius_deg

    stars = catalog.query_region(ra_hint, dec_hint, cone_radius_deg, max_stars=max_stars)
    if len(stars) < 3:
        raise RuntimeError(f"Only {len(stars)} Gaia stars found in the search region — need at least 3.")
    cat_ids = np.array([s[0] for s in stars])
    cat_ra = np.array([s[1] for s in stars])
    cat_dec = np.array([s[2] for s in stars])
    cat_pmra = np.array([s[3] for s in stars])
    cat_pmdec = np.array([s[4] for s in stars])

    obs_epoch = parse_obs_epoch(header)
    if obs_epoch is not None:
        cat_ra, cat_dec = projection.apply_proper_motion(cat_ra, cat_dec, cat_pmra, cat_pmdec, obs_epoch)

    all_world = SkyCoord(ra=cat_ra, dec=cat_dec, unit="deg", frame="icrs")

    xi, eta = projection.gnomonic_project(cat_ra, cat_dec, ra_hint, dec_hint)
    catalog_xy = np.column_stack([xi, eta])

    # astroalign's triangle matching is O(n^3) in list size — cap what's fed
    # to it. The catalog query circle is wider than the real FOV (it has to
    # be, to cover position-hint uncertainty), so capping catalog_xy by raw
    # brightness rank mostly keeps stars outside the actual frame (this was
    # the original max_control_points bug, just reintroduced by a naive cap
    # here) — filter to stars actually near the hint position first, THEN
    # cap by brightness within that geometric subset.
    dist_from_hint = np.hypot(xi, eta) / 3600.0  # degrees
    near_hint = np.where(dist_from_hint < fov_radius_deg * 1.2)[0] if fov_radius_deg else np.arange(len(catalog_xy))
    match_catalog_full = catalog_xy[near_hint]

    # A small cap (60) is fast and enough for typical sparse fields, but on
    # a dense field the true overlap between the brightest-N catalog stars
    # (Gaia G-band) and brightest-N detected stars (instrumental flux, a
    # different bandpass) can be a small, diluted fraction of each list —
    # confirmed directly on a real dense MObs field (only 11/60 overlapped)
    # — so escalate the cap when a smaller one fails, rather than assuming
    # 60 is always enough.
    match_cap_candidates = [60, 120, 200, 300]
    if match_cap_hint is not None and match_cap_hint not in match_cap_candidates:
        match_cap_candidates = [match_cap_hint] + match_cap_candidates

    # Confirmed directly on real data: a single (fwhm, cap) attempt can take minutes on a
    # dense-but-poorly-overlapping field, and astroalign gives no way to bound that from the
    # calling side — so every attempt runs under a hard per-attempt subprocess timeout, AND
    # the whole retry matrix (up to 4 fwhm x 4 cap = 16 attempts) is bounded by an overall
    # wall-clock budget so one stubborn frame can't stall a batch for arbitrarily long.
    #
    # All candidates race concurrently (find_transform_race_interleaved) instead of running
    # strictly one at a time — the old sequential design paid the full cost of each
    # candidate, in priority order, even when a later candidate would have succeeded
    # quickly. Detection is interleaved with the race rather than run eagerly for every
    # FWHM up front — confirmed directly that up-front detection for all 4 candidates (up
    # to ~15s combined on a real field) regressed easy sparse fields that used to finish in
    # a few seconds by only ever detecting fwhm0.
    attempt_timeout_sec = 60.0
    overall_budget_sec = 180.0

    def _detect_for_fwhm(candidate_fwhm):
        detected = detect.detect_stars(data, fwhm=candidate_fwhm, threshold_sigma=threshold_sigma)
        if len(detected) < 3:
            raise RuntimeError(f"Only {len(detected)} stars detected at fwhm={candidate_fwhm:.1f}")
        detected = detected[:max_stars]
        detected_xy = np.array([(x, y) for x, y, _ in detected])
        return detected, detected_xy

    # Header-seeded shortcut: needs a rotation/parity hint (physically stable within a
    # session), a known pixel scale, AND a center hint — all carried forward by run_server()
    # from a previous successful solve. The center hint deliberately comes from the previous
    # file's own *solved* position, not the raw ra_hint/dec_hint position-uncertainty hint —
    # confirmed directly on real data that ra_hint/dec_hint can be off by ~70 arcsec (a
    # mount-pointing-level hint), while refine_wcs's first-pass match tolerance is only 8px
    # (~2 arcsec at this file's pixel scale) — nowhere close enough to bootstrap from.
    # Consecutive frames of the same target stay far closer than that, so the previous
    # solve's actual center is a tight enough anchor without loosening refine_wcs's proven
    # tolerance (which stays tight to avoid absorbing false matches in dense fields).
    #
    # Detection for this seed reuses fwhm_candidates[0] (the same first guess the ladder
    # would try anyway) — if it fails outright (<3 stars), the seed attempt is simply
    # skipped and the ladder proceeds exactly as if no hint had been given.
    if (rotation_hint_deg is not None and parity_hint is not None and pixel_scale is not None
            and center_hint_ra_deg is not None and center_hint_dec_deg is not None):
        try:
            seed_detected, seed_detected_xy = _detect_for_fwhm(fwhm_candidates[0])
        except Exception:
            seed_detected = None
        if seed_detected is not None:
            seed = try_header_seeded_wcs(
                center_hint_ra_deg, center_hint_dec_deg, data.shape[1], data.shape[0], pixel_scale,
                rotation_hint_deg, parity_hint, all_world, seed_detected_xy)
            if seed is not None:
                seed_wcs, seed_keep_cat, seed_keep_det, seed_rms_px = seed
                matched_ids = cat_ids[seed_keep_cat]
                summary = compute_solution_summary(
                    seed_wcs, header, data.shape[1], data.shape[0], seed_rms_px)
                return {
                    "wcs": seed_wcs,
                    "num_detected": len(seed_detected),
                    "num_catalog": len(stars),
                    "num_matched": len(seed_keep_cat),
                    "matched_gaia_ids": matched_ids.tolist(),
                    "rms_pixels": seed_rms_px,
                    "summary": summary,
                    "fwhm_used": fwhm_candidates[0],
                    "match_cap_used": None,
                }

    (fwhm_used, match_cap_used), transform, (src_matched, tgt_matched), detected_by_fwhm = \
        find_transform_race_interleaved(
            fwhm_candidates, _detect_for_fwhm, match_cap_candidates, match_catalog_full,
            attempt_timeout_sec=attempt_timeout_sec, overall_budget_sec=overall_budget_sec)

    detected, detected_xy = detected_by_fwhm[fwhm_used]

    tree = cKDTree(catalog_xy)
    dist, idx = tree.query(tgt_matched, k=1)
    if np.any(dist > 1e-6):
        raise RuntimeError("Matched target points did not map cleanly back to catalog entries.")

    matched_ra = cat_ra[idx]
    matched_dec = cat_dec[idx]
    matched_ids = cat_ids[idx]

    # photutils centroids are 0-indexed; astropy WCS fitting expects FITS
    # (1,1)-based pixel convention.
    px = src_matched[:, 0] + 1.0
    py = src_matched[:, 1] + 1.0

    world_coords = SkyCoord(ra=matched_ra, dec=matched_dec, unit="deg", frame="icrs")
    wcs = fit_wcs_from_points((px, py), world_coords)

    refined_wcs, keep_cat, keep_det = refine_wcs(wcs, all_world, detected_xy)

    if keep_cat is not None and len(keep_cat) >= len(idx):
        wcs = refined_wcs
        matched_ids = cat_ids[keep_cat]
        fit_x, fit_y = wcs.world_to_pixel(all_world[keep_cat])
        residual_px = np.hypot(fit_x - detected_xy[keep_det, 0], fit_y - detected_xy[keep_det, 1])
        num_matched = len(keep_cat)
    else:
        fit_x, fit_y = wcs.world_to_pixel(world_coords)
        residual_px = np.hypot(fit_x - src_matched[:, 0], fit_y - src_matched[:, 1])
        num_matched = len(idx)

    rms_px = float(np.sqrt(np.mean(residual_px**2)))

    summary = compute_solution_summary(wcs, header, data.shape[1], data.shape[0], rms_px)

    return {
        "wcs": wcs,
        "num_detected": len(detected),
        "num_catalog": len(stars),
        "num_matched": num_matched,
        "matched_gaia_ids": matched_ids.tolist(),
        "rms_pixels": rms_px,
        "summary": summary,
        "fwhm_used": fwhm_used,
        "match_cap_used": match_cap_used,
    }


def compute_solution_summary(wcs, header, naxis1: int, naxis2: int, rms_px: float) -> dict:
    """Derive a human/machine-readable summary of a solved WCS: center
    coordinates, pixel scale, field of view, rotation/parity, and focal
    length — both as reported in the header and as back-derived from the
    actual solved pixel scale (a useful cross-check: a real mismatch between
    the two indicates the header's FOCALLEN/XPIXSZ don't match reality).
    """
    center_x, center_y = (naxis1 + 1) / 2.0, (naxis2 + 1) / 2.0
    center_ra, center_dec = wcs.wcs_pix2world(center_x, center_y, 1)
    center_ra, center_dec = float(center_ra), float(center_dec)
    center_coord = SkyCoord(ra=center_ra, dec=center_dec, unit="deg", frame="icrs")

    cd = wcs.wcs.cd if hasattr(wcs.wcs, "cd") and np.any(wcs.wcs.cd) else wcs.pixel_scale_matrix
    cd1_1, cd1_2 = cd[0, 0], cd[0, 1]
    cd2_1, cd2_2 = cd[1, 0], cd[1, 1]

    scale_x_arcsec = float(np.hypot(cd1_1, cd2_1)) * 3600.0
    scale_y_arcsec = float(np.hypot(cd1_2, cd2_2)) * 3600.0
    pixel_scale_arcsec = (scale_x_arcsec + scale_y_arcsec) / 2.0

    rotation_deg = float(np.degrees(np.arctan2(cd2_1, cd1_1)))
    determinant = cd1_1 * cd2_2 - cd1_2 * cd2_1
    parity = "flipped/mirrored" if determinant > 0 else "normal (not mirrored)"

    fov_width_arcmin = naxis1 * pixel_scale_arcsec / 60.0
    fov_height_arcmin = naxis2 * pixel_scale_arcsec / 60.0

    focal_length_header_mm = header.get("FOCALLEN")
    xpixsz_um = header.get("XPIXSZ")
    focal_length_derived_mm = None
    if xpixsz_um is not None:
        focal_length_derived_mm = 206265.0 * (xpixsz_um / 1000.0) / pixel_scale_arcsec

    return {
        "center_ra_deg": center_ra,
        "center_dec_deg": center_dec,
        "center_ra_hms": center_coord.ra.to_string(unit="hourangle", sep=":", precision=2, pad=True),
        "center_dec_dms": center_coord.dec.to_string(unit="deg", sep=":", precision=1, alwayssign=True, pad=True),
        "pixel_scale_arcsec": pixel_scale_arcsec,
        "pixel_scale_x_arcsec": scale_x_arcsec,
        "pixel_scale_y_arcsec": scale_y_arcsec,
        "fov_width_arcmin": fov_width_arcmin,
        "fov_height_arcmin": fov_height_arcmin,
        "rotation_deg": rotation_deg,
        "parity": parity,
        "focal_length_header_mm": focal_length_header_mm,
        "focal_length_derived_mm": focal_length_derived_mm,
        "rms_arcsec": rms_px * pixel_scale_arcsec,
        # Raw WCS terms, straight from the fitted solution rather than re-derived from the
        # rounded summary fields above — added for ASTAP-compatible mode's .ini sidecar,
        # which needs these exact values, not an approximation reconstructed from them.
        "crpix1": center_x,
        "crpix2": center_y,
        "cd1_1": float(cd1_1),
        "cd1_2": float(cd1_2),
        "cd2_1": float(cd2_1),
        "cd2_2": float(cd2_2),
    }


def format_solution_summary(result: dict) -> str:
    s = result["summary"]
    lines = [
        "=== Plate Solve Summary ===",
        f"Center (RA, Dec):    {s['center_ra_hms']}  {s['center_dec_dms']}",
        f"                     ({s['center_ra_deg']:.6f}, {s['center_dec_deg']:.6f}) deg",
        f"Pixel scale:         {s['pixel_scale_arcsec']:.4f} \"/px "
        f"(x={s['pixel_scale_x_arcsec']:.4f}, y={s['pixel_scale_y_arcsec']:.4f})",
        f"Field of view:       {s['fov_width_arcmin']:.2f}' x {s['fov_height_arcmin']:.2f}'",
        f"Rotation:            {s['rotation_deg']:.2f} deg   Parity: {s['parity']}",
    ]
    if s["focal_length_header_mm"] is not None:
        lines.append(f"Focal length (header): {s['focal_length_header_mm']:.1f} mm")
    if s["focal_length_derived_mm"] is not None:
        lines.append(f"Focal length (derived from solve): {s['focal_length_derived_mm']:.1f} mm")
    lines.extend([
        f"Detected / Catalog / Matched: {result['num_detected']} / {result['num_catalog']} / {result['num_matched']}",
        f"RMS residual:        {result['rms_pixels']:.3f} px  ({s['rms_arcsec']:.3f}\")",
    ])
    return "\n".join(lines)


def write_wcs_to_file(filepath: str, wcs):
    """Write a solved WCS into a FITS file's image header, in place.

    Sets PLTSOLVD=True alongside the WCS keywords — the same flag ASTAP
    writes and the rest of this pipeline (FITS Calibrator, TransitLab) reads
    to determine solved status, so downstream tools recognize the result.

    Writes into whichever HDU actually holds the 2D image data, not blindly HDU 0 — for
    an ordinary single-HDU FITS file that's the same thing, but for a .fz tile-compressed
    file HDU 0 is just an empty shell and the real image (where these keywords need to
    actually live to be found again) is in a CompImageHDU at index 1. Writing to the empty
    shell would silently "succeed" while never actually recording the solve anywhere a
    reader would look.
    """
    with fits.open(filepath, mode="update") as hdul:
        idx = _find_image_hdu_index(hdul)
        if idx < 0:
            raise ValueError(f"No 2D image data found in {filepath}")
        hdul[idx].header.update(wcs.to_header())
        hdul[idx].header["PLTSOLVD"] = True
        hdul.flush()


def _resolve_ra_dec(filepath: str, ra: float, dec: float):
    """Returns (ra, dec) as floats, falling back to the file's own RA/DEC header
    keywords when either is not given. Raises ValueError if neither is available —
    shared by one-shot CLI mode and --server mode so both fall back identically.

    Checks the actual image HDU's header first, then the primary as a fallback — for an
    ordinary single-HDU file those are the same header, but for a .fz file the primary is
    an empty shell and RA/DEC (copied through unchanged by fpack, since they don't collide
    with any of its reserved Z-prefixed keywords) live on the compressed image HDU instead.
    """
    if ra is None or dec is None:
        with fits.open(filepath) as hdul:
            idx = _find_image_hdu_index(hdul)
            header = hdul[idx].header if idx >= 0 else hdul[0].header
            ra = ra if ra is not None else header.get("RA", hdul[0].header.get("RA"))
            dec = dec if dec is not None else header.get("DEC", hdul[0].header.get("DEC"))
        if ra is None or dec is None:
            raise ValueError("No RA/Dec hint given and none found in header (RA/DEC keywords).")
    return float(ra), float(dec)


def _build_json_envelope(result: dict, text: str) -> dict:
    return {
        "summary": result["summary"],
        "text": text,
        "num_detected": result["num_detected"],
        "num_catalog": result["num_catalog"],
        "num_matched": result["num_matched"],
        "rms_pixels": result["rms_pixels"],
        "fwhm_used": result["fwhm_used"],
        "match_cap_used": result["match_cap_used"],
    }


def run_server():
    """Persistent batch mode for StarFix's BatchSolverSession (Services/BatchSolverSession.cs):
    reads one JSON request per line from stdin ({"file","ra","dec","radius"}), solves it,
    writes one JSON response per line to stdout, flushing immediately so the caller sees each
    result without waiting for the whole batch to finish. Keeping this one process alive across
    an entire batch (instead of StarFix launching a fresh solve.exe per file, as it did before)
    is what lets gaia_catalog_lookup.py's module-level _pixel_cache actually pay off across a
    same-target batch — that part is a pure, unconditional win with no downside.

    NOTE: this used to also carry the previous file's winning FWHM/match-cap forward as a hint
    (solve()'s fwhm_hint/match_cap_hint params still support this). Rolled back after a real
    batch showed 20-40s (once 99s) gaps between otherwise-3-8s files. Root cause: unlike
    match_cap_hint (drawn from a small fixed set, deduped against the existing candidates),
    fwhm is a continuous value with no such guard — a hint that doesn't suit the next frame can
    still detect >=3 stars (looking plausible) and then burn through *all 4* match_cap attempts
    (each up to attempt_timeout_sec=60s) failing before the code ever falls through to the
    frame's own correct fwhm candidate — up to ~240s wasted on one bad guess, dwarfing whatever
    a good guess would have saved. The test data used to validate hint-carrying was identical
    copies of one file, which by construction always wants the identical hint — this failure
    mode (a hint that's actively wrong for the next real, different frame) could never have
    shown up there. Every request now gets solve()'s full, independent, unmodified ladder,
    exactly like one-shot CLI mode always has.

    Rotation/parity hint-carrying (below) is a different kind of shortcut and doesn't repeat
    that mistake: unlike FWHM, rotation/parity are physically stable camera+mount properties
    within a session (that stability is *why* the FWHM ladder exists but a rotation ladder
    doesn't need to), and solve()'s try_header_seeded_wcs has an explicit quality gate — a
    wrong hint (e.g. right after an undetected meridian flip) is rejected outright and falls
    through to the full ladder, never returned as a degraded result. Rejection is also cheap
    (one quick nearest-neighbor pass), unlike a bad FWHM guess which could burn a full
    60s-per-attempt ladder before falling back.

    A single bad file must never kill the whole session — unlike one-shot CLI mode (where an
    uncaught exception crashing the process is fine, since there's only ever one file), every
    exception here is caught and reported as {"ok": false, "error": ...} instead of propagating.
    """
    last_rotation_deg = None
    last_parity = None
    last_center_ra_deg = None
    last_center_dec_deg = None

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
            ra, dec = _resolve_ra_dec(req["file"], req.get("ra"), req.get("dec"))
            result = solve(req["file"], ra, dec, float(req["radius"]),
                            rotation_hint_deg=last_rotation_deg, parity_hint=last_parity,
                            center_hint_ra_deg=last_center_ra_deg, center_hint_dec_deg=last_center_dec_deg)

            write_wcs_to_file(req["file"], result["wcs"])
            text = format_solution_summary(result)
            response = {"ok": True, **_build_json_envelope(result, text)}
            last_rotation_deg = result["summary"]["rotation_deg"]
            last_parity = result["summary"]["parity"]
            last_center_ra_deg = result["summary"]["center_ra_deg"]
            last_center_dec_deg = result["summary"]["center_dec_deg"]
        except Exception as e:
            response = {"ok": False, "error": str(e)}

        print(json.dumps(response), flush=True)


def main():
    parser = argparse.ArgumentParser(description="Directed plate solver (MVP, Gaia DR3 + astroalign)")
    parser.add_argument("file", nargs="?", help="Path to FITS file (omit with --server)")
    parser.add_argument("--ra", type=float, help="Hint RA in degrees (defaults to header RA)")
    parser.add_argument("--dec", type=float, help="Hint Dec in degrees (defaults to header DEC)")
    parser.add_argument("-r", "--radius", type=float, default=0.5,
                         help="Position-uncertainty margin in degrees, added to the image's own FOV "
                              "(computed from FOCALLEN/XPIXSZ if present) to size the catalog search")
    parser.add_argument("--dry-run", action="store_true",
                         help="Solve only, do NOT write the WCS back into the file (default: writes "
                              "in place, like ASTAP)")
    parser.add_argument("--fwhm", type=float, default=None,
                         help="Expected stellar FWHM in pixels for detection (default: retry ladder "
                              "auto-estimated from pixel scale, starting at 1.5\" assumed seeing)")
    parser.add_argument("--threshold", type=float, default=6.0, help="Detection threshold in sigma above background")
    parser.add_argument("--json", action="store_true",
                         help="Print one JSON line to stdout instead of the human-readable summary "
                              "(used by StarFix's GUI, which parses stdout) — "
                              '{"summary": {...}, "text": "...", "num_detected", "num_catalog", '
                              '"num_matched", "rms_pixels"}')
    parser.add_argument("--server", action="store_true",
                         help="Persistent batch mode — read JSON requests from stdin, one file "
                              "per line, until EOF (see run_server's docstring). Used by StarFix's "
                              "Batch Solve instead of relaunching this exe per file.")
    args = parser.parse_args()

    if args.server:
        run_server()
        return

    if not args.file:
        print("A FITS file path is required unless --server is given.", file=sys.stderr)
        sys.exit(1)

    try:
        ra_hint, dec_hint = _resolve_ra_dec(args.file, args.ra, args.dec)
    except ValueError as e:
        print(str(e), file=sys.stderr)
        sys.exit(1)

    result = solve(args.file, ra_hint, dec_hint, args.radius,
                    fwhm=args.fwhm, threshold_sigma=args.threshold)

    text = format_solution_summary(result)

    if not args.dry_run:
        write_wcs_to_file(args.file, result["wcs"])

    if args.json:
        print(json.dumps(_build_json_envelope(result, text)))
    else:
        print(text)
        if not args.dry_run:
            print(f"\nWCS written to {args.file}")
        else:
            print("\n[dry run] WCS NOT written to file")


if __name__ == "__main__":
    # Dispatched to before normal argparse-based main() — see _run_match_worker's docstring
    # for why this exe/script re-invokes itself this way instead of shelling out to a
    # separate worker script.
    if len(sys.argv) >= 4 and sys.argv[1] == "--_match-worker":
        _run_match_worker(sys.argv[2], sys.argv[3])
    else:
        main()
