namespace StarFix.Services;

/// <summary>A source file counts as "already solved" either way: its own header carries
/// PLTSOLVD=T (the overwrite-mode case — the source itself was written into directly), or a
/// "&lt;name&gt;_solved_N" copy of it already exists alongside it (the new-file-mode case — the
/// source is never touched, so its own header can never reflect this). Checking only the first
/// case missed the second entirely, confirmed against a real batch that kept re-solving sources
/// into new numbered copies every run. Shared by BatchSolveViewModel's folder-browse pre-filter
/// and BatchSolveService's authoritative pre-solve check, so both agree on the same definition.</summary>
public static class AlreadySolvedService
{
    public static bool IsAlreadySolved(string sourcePath)
    {
        if (OutputPathService.HasExistingSolvedCopy(sourcePath))
            return true;

        try
        {
            return FitsHeaderService.Read(sourcePath).GetBool("PLTSOLVD");
        }
        catch
        {
            return false;
        }
    }
}
