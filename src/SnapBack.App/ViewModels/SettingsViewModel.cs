using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using SnapBack.App.Mvvm;
using SnapBack.Core;
using SnapBack.Core.Persistence;
using SnapBack.Windows;

namespace SnapBack.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SnapBackEngine _engine;
    private readonly IDialogService _dialogs;
    private int _timeout;
    private bool _preselectCaution;
    private bool _autoBaseline;
    private int _minDelay;
    private int _quiet;
    private int _historyLimit;

    public SettingsViewModel(SnapBackEngine engine, IDialogService dialogs)
    {
        _engine = engine;
        _dialogs = dialogs;
        var s = engine.Settings;
        _timeout = s.GracefulTimeoutSeconds;
        _preselectCaution = s.PreselectCautionItems;
        _autoBaseline = s.AutoBaselineOnSignIn;
        _minDelay = s.AutoBaselineMinDelaySeconds;
        _quiet = s.AutoBaselineQuietSeconds;
        _historyLimit = s.HistoryLimit;
        foreach (var k in s.KeepList.OrderBy(k => k.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            KeepList.Add(k);
        }

        RemoveKeepCommand = new RelayCommand(p =>
        {
            if (p is KeepRule rule)
            {
                KeepList.Remove(rule);
            }
        });
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        DeleteBaselineCommand = new RelayCommand(DeleteBaseline, () => _engine.Baseline is not null);
    }

    public ObservableCollection<KeepRule> KeepList { get; } = [];

    public ICommand RemoveKeepCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

    public ICommand DeleteBaselineCommand { get; }

    public int GracefulTimeoutSeconds
    {
        get => _timeout;
        set => Set(ref _timeout, Math.Clamp(value, 5, 120));
    }

    public bool PreselectCautionItems
    {
        get => _preselectCaution;
        set => Set(ref _preselectCaution, value);
    }

    public bool AutoBaselineOnSignIn
    {
        get => _autoBaseline;
        set => Set(ref _autoBaseline, value);
    }

    public int AutoBaselineMinDelaySeconds
    {
        get => _minDelay;
        set => Set(ref _minDelay, Math.Clamp(value, 0, 3600));
    }

    public int AutoBaselineQuietSeconds
    {
        get => _quiet;
        set => Set(ref _quiet, Math.Clamp(value, 10, 600));
    }

    public int HistoryLimit
    {
        get => _historyLimit;
        set => Set(ref _historyLimit, Math.Clamp(value, 1, 500));
    }

    public string DataFolder => Core.Persistence.AppPaths.Default().Root;

    /// <summary>Applies the settings. Returns false (and tells the user) if something could not be saved.</summary>
    public bool Save()
    {
        try
        {
            _engine.UpdateSettings(s => s with
            {
                GracefulTimeoutSeconds = GracefulTimeoutSeconds,
                PreselectCautionItems = PreselectCautionItems,
                AutoBaselineOnSignIn = AutoBaselineOnSignIn,
                AutoBaselineMinDelaySeconds = AutoBaselineMinDelaySeconds,
                AutoBaselineQuietSeconds = AutoBaselineQuietSeconds,
                HistoryLimit = HistoryLimit,
                KeepList = KeepList.ToList(),
            });

            var exe = Environment.ProcessPath;
            if (AutoBaselineOnSignIn && exe is not null)
            {
                AutostartRegistration.Enable(exe);
            }
            else
            {
                AutostartRegistration.Disable();
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _dialogs.Inform("Settings", "The settings could not be saved: " + ex.Message);
            return false;
        }
    }

    private void OpenDataFolder()
    {
        Directory.CreateDirectory(DataFolder);
        Process.Start(new ProcessStartInfo(DataFolder) { UseShellExecute = true });
    }

    private void DeleteBaseline()
    {
        if (_dialogs.Confirm("Delete baseline", "Delete the current baseline? SnapBack will not suggest closing anything until you create a new one."))
        {
            _engine.DeleteBaseline();
        }
    }
}
