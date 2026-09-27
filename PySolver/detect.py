"""Star detection on a FITS light frame via photutils DAOStarFinder."""
import numpy as np
from astropy.stats import sigma_clipped_stats
from photutils.detection import DAOStarFinder


def detect_stars(data: np.ndarray, fwhm: float = 3.0, threshold_sigma: float = 5.0):
    """Detect stars in a 2D image array.

    Returns a list of (x, y, flux) tuples, sorted brightest first.
    """
    mean, median, std = sigma_clipped_stats(data, sigma=3.0)
    finder = DAOStarFinder(fwhm=fwhm, threshold=threshold_sigma * std)
    sources = finder(data - median)
    if sources is None:
        return []

    sources.sort("flux")
    sources.reverse()
    return [(float(row["xcentroid"]), float(row["ycentroid"]), float(row["flux"])) for row in sources]
