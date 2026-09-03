using StarFix.Models;
using StarFix.Services;

namespace StarFix.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly AppConfig _cfg;

    public AppConfig Config => _cfg;

    public SolveViewModel Solve { get; }
    public ResultsViewModel Results { get; }

    public MainWindowViewModel()
    {
        _cfg = ConfigService.Load();
        if (string.IsNullOrWhiteSpace(_cfg.GaiaCatalogPath))
        {
            _cfg.GaiaCatalogPath = System.IO.Path.Combine(ConfigService.AppDataDir, "gaia_catalog");
            ConfigService.Save(_cfg);
        }

        Results = new ResultsViewModel();
        Solve = new SolveViewModel(_cfg) { OnSolved = Results.Add, OnJobStarting = Results.Clear };
    }
}
