using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StarFix.Models;
using StarFix.Services;

namespace StarFix.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppConfig _cfg;

    public SettingsViewModel(AppConfig cfg)
    {
        _cfg = cfg;
        OverwriteExisting = cfg.OverwriteExisting;
        DefaultSearchRadiusDeg = cfg.DefaultSearchRadiusDeg;
        GaiaCatalogPath = cfg.GaiaCatalogPath;
    }

    public Action? CloseCallback { get; set; }

    [ObservableProperty] private bool _overwriteExisting;
    [ObservableProperty] private double _defaultSearchRadiusDeg;
    [ObservableProperty] private string _gaiaCatalogPath = "";

    [RelayCommand]
    private void Save()
    {
        _cfg.OverwriteExisting = OverwriteExisting;
        _cfg.DefaultSearchRadiusDeg = DefaultSearchRadiusDeg;
        _cfg.GaiaCatalogPath = GaiaCatalogPath;
        ConfigService.Save(_cfg);
        SessionLogService.Write("[Settings] Saved.");
        CloseCallback?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseCallback?.Invoke();
}
