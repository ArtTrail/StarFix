"""Gnomonic (tangent-plane) projection — converts catalog RA/Dec into a flat
local coordinate system comparable to image pixel coordinates, so astroalign
can match star patterns between the two without knowing the pixel scale or
rotation in advance.
"""
import numpy as np

ARCSEC_PER_RAD = 206264.80624709636
GAIA_DR3_REF_EPOCH = 2016.0


def apply_proper_motion(ra_deg, dec_deg, pmra_mas_yr, pmdec_mas_yr, obs_decimalyear):
    """Propagate Gaia DR3 (epoch 2016.0) positions to the observation epoch.

    pmra is Gaia's pmra*cos(dec) convention (mas/yr), so no extra cos(dec)
    factor is applied when converting it to a delta; dividing the resulting
    RA delta by cos(dec) is what converts it back from a great-circle
    distance into a change in the RA coordinate itself.
    """
    dt_years = obs_decimalyear - GAIA_DR3_REF_EPOCH
    dec_rad = np.radians(dec_deg)

    dra_deg = (pmra_mas_yr / 1000.0 / 3600.0) * dt_years / np.cos(dec_rad)
    ddec_deg = (pmdec_mas_yr / 1000.0 / 3600.0) * dt_years

    return ra_deg + dra_deg, dec_deg + ddec_deg


def gnomonic_project(ra_deg, dec_deg, ra0_deg, dec0_deg):
    """Project RA/Dec (degrees, arrays or scalars) onto the tangent plane at
    (ra0_deg, dec0_deg). Returns (xi, eta) in arcsec.
    """
    ra = np.radians(ra_deg)
    dec = np.radians(dec_deg)
    ra0 = np.radians(ra0_deg)
    dec0 = np.radians(dec0_deg)

    d_ra = ra - ra0
    cos_c = np.sin(dec0) * np.sin(dec) + np.cos(dec0) * np.cos(dec) * np.cos(d_ra)

    xi = np.cos(dec) * np.sin(d_ra) / cos_c
    eta = (np.cos(dec0) * np.sin(dec) - np.sin(dec0) * np.cos(dec) * np.cos(d_ra)) / cos_c

    return xi * ARCSEC_PER_RAD, eta * ARCSEC_PER_RAD
