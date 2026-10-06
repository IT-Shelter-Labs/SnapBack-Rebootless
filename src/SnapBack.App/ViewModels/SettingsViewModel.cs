using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using SnapBack.App.Localization;
using SnapBack.App.Mvvm;
using SnapBack.Core.Localization;
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
    private LanguageOption _language;

    public SettingsViewModel(SnapBackEngine engine, IDialogService dialogs)
    {
        _engine = engine;
        _dialogs = dialogs;
        var s = engine.Settings;
        OriginalLanguage = s.Language;
        _timeout = s.GracefulTimeoutSeconds;
        _preselectCaution = s.PreselectCautionItems;
        _autoBaseline = s.AutoBaselineOnSignIn;
        _minDelay = s.AutoBaselineMinDelaySeconds;
        _quiet = s.AutoBaselineQuietSeconds;
        _historyLimit = s.HistoryLimit;
        LanguageOptions = new[] { Languages.System }.Concat(Languages.Supported).Select(code => new LanguageOption(code)).ToList();
        _language = LanguageOptions.FirstOrDefault(o => o.Code == s.Language) ?? LanguageOptions[0];
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

    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    /// <summary>The selected language. Applied immediately, so the user sees the effect without restarting.</summary>
    public LanguageOption Language
    {
        get => _language;
        set
        {
            if (value is not null && Set(ref _language, value))
            {
                LanguageManager.Apply(value.Code);
            }
        }
    }

    /// <summary>The language that was active when the dialog opened (restored on Cancel).</summary>
    public string OriginalLanguage { get; } = "";

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
                Language = Language.Code,
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
            _dialogs.Inform(Tr.Get("Settings_Title"), Tr.Format("Settings_SaveFailed", ex.Message));
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
        if (_dialogs.Confirm(Tr.Get("Confirm_DeleteBaseline_Title"), Tr.Get("Confirm_DeleteBaseline_Text")))
        {
            _engine.DeleteBaseline();
        }
    }
}

/// <summary>An entry of the language picker; "System default" shows which language that resolves to.</summary>
public sealed class LanguageOption(string code)
{
    public string Code { get; } = code;

    public string Name => Code == Languages.System
        ? Tr.Format("Settings_LanguageSystem", Languages.NativeName(Languages.FromSystem(LanguageManager.SystemUiCulture)))
        : Languages.NativeName(Code);

    public override string ToString() => Name;
}
