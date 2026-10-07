using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using SnapBack.App.Localization;
using SnapBack.App.Mvvm;
using SnapBack.Core;
using SnapBack.Core.Localization;
using SnapBack.Core.Persistence;
using SnapBack.Core.Rules;
using SnapBack.Windows;

namespace SnapBack.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SnapBackEngine _engine;
    private readonly IDialogService _dialogs;
    private int _timeout;
    private bool _preselectCaution;
    private bool _startWithWindows;
    private bool _startMinimized;
    private bool _autoBaseline;
    private int _minDelay;
    private int _quiet;
    private int _historyLimit;
    private bool _closeToTray;
    private bool _showNotifications;
    private LanguageOption _language;
    private ProfileOption? _autoBaselineProfile;

    public SettingsViewModel(SnapBackEngine engine, IDialogService dialogs)
    {
        _engine = engine;
        _dialogs = dialogs;
        var s = engine.Settings;
        OriginalLanguage = s.Language;
        _timeout = s.GracefulTimeoutSeconds;
        _preselectCaution = s.PreselectCautionItems;
        _startWithWindows = s.StartWithWindows;
        _startMinimized = s.StartMinimizedToTray;
        _autoBaseline = s.AutoBaselineOnSignIn;
        _minDelay = s.AutoBaselineMinDelaySeconds;
        _quiet = s.AutoBaselineQuietSeconds;
        _historyLimit = s.HistoryLimit;
        _closeToTray = s.CloseButton == CloseButtonAction.MinimizeToTray;
        _showNotifications = s.ShowNotifications;
        LanguageOptions = new[] { Languages.System }.Concat(Languages.Supported).Select(code => new LanguageOption(code)).ToList();
        _language = LanguageOptions.FirstOrDefault(o => o.Code == s.Language) ?? LanguageOptions[0];

        // "Default profile" (Guid.Empty) follows whichever profile is the default; it is stored as null.
        var profiles = engine.Profiles;
        ProfileOptions = new[] { new ProfileOption(Guid.Empty, Tr.Get("Settings_DefaultProfileOption"), false) }
            .Concat(profiles.Profiles.Select(p => new ProfileOption(p.Id, p.Name, p.Id == profiles.DefaultProfileId)))
            .ToList();
        _autoBaselineProfile = ProfileOptions.FirstOrDefault(o => o.Id == s.AutoBaselineProfileId) ?? ProfileOptions[0];

        foreach (var r in s.Rules.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            Rules.Add(new RuleItemViewModel(r));
        }

        RemoveRuleCommand = new RelayCommand(p =>
        {
            if (p is RuleItemViewModel rule)
            {
                Rules.Remove(rule);
            }
        });
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
    }

    public ObservableCollection<RuleItemViewModel> Rules { get; } = [];

    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    public IReadOnlyList<ProfileOption> ProfileOptions { get; }

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

    public ICommand RemoveRuleCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

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

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => Set(ref _startWithWindows, value);
    }

    public bool StartMinimizedToTray
    {
        get => _startMinimized;
        set => Set(ref _startMinimized, value);
    }

    public bool AutoBaselineOnSignIn
    {
        get => _autoBaseline;
        set => Set(ref _autoBaseline, value);
    }

    public ProfileOption? AutoBaselineProfile
    {
        get => _autoBaselineProfile;
        set => Set(ref _autoBaselineProfile, value);
    }

    public bool HasProfiles => ProfileOptions.Count > 1;

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

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (Set(ref _closeToTray, value))
            {
                OnPropertyChanged(nameof(ExitOnClose));
            }
        }
    }

    public bool ExitOnClose
    {
        get => !_closeToTray;
        set => CloseToTray = !value;
    }

    public bool ShowNotifications
    {
        get => _showNotifications;
        set => Set(ref _showNotifications, value);
    }

    public int HistoryLimit
    {
        get => _historyLimit;
        set => Set(ref _historyLimit, Math.Clamp(value, 1, 500));
    }

    /// <summary>Shown when Windows' own Startup apps settings switched SnapBack off.</summary>
    public string? StartupDisabledNote
    {
        get
        {
            try
            {
                return StartWithWindows && AutostartRegistration.IsDisabledInWindowsSettings() ? Tr.Get("Settings_StartupDisabledByWindows") : null;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                return null;
            }
        }
    }

    public string DataFolder => AppPaths.Default().Root;

    /// <summary>Applies the settings. Returns false (and tells the user) if something could not be saved.</summary>
    public bool Save()
    {
        try
        {
            _engine.UpdateSettings(s => s with
            {
                GracefulTimeoutSeconds = GracefulTimeoutSeconds,
                PreselectCautionItems = PreselectCautionItems,
                StartWithWindows = StartWithWindows,
                StartMinimizedToTray = StartMinimizedToTray,
                AutoBaselineOnSignIn = AutoBaselineOnSignIn,
                AutoBaselineProfileId = AutoBaselineProfile is { Id: var id } && id != Guid.Empty ? id : null,
                AutoBaselineMinDelaySeconds = AutoBaselineMinDelaySeconds,
                AutoBaselineQuietSeconds = AutoBaselineQuietSeconds,
                CloseButton = CloseToTray ? CloseButtonAction.MinimizeToTray : CloseButtonAction.Exit,
                ShowNotifications = ShowNotifications,
                HistoryLimit = HistoryLimit,
                Language = Language.Code,
                Rules = Rules.Select(r => r.ToRule()).ToList(),
            });

            if (Environment.ProcessPath is { } exe)
            {
                AutostartRegistration.Sync(StartWithWindows, exe);
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

/// <summary>A standing per-app rule, editable in Settings.</summary>
public sealed class RuleItemViewModel : ObservableObject
{
    private readonly AppRule _rule;
    private RuleKindOption _kind;

    public RuleItemViewModel(AppRule rule)
    {
        _rule = rule;
        _kind = KindOptions.First(o => o.Kind == rule.Kind);
    }

    public static IReadOnlyList<RuleKindOption> KindOptions { get; } =
        Enum.GetValues<AppRuleKind>().Select(k => new RuleKindOption(k)).ToList();

    public IReadOnlyList<RuleKindOption> Options => KindOptions;

    public string DisplayName => _rule.DisplayName;

    public string AppKey => _rule.AppKey;

    public RuleKindOption Kind
    {
        get => _kind;
        set
        {
            if (value is not null)
            {
                Set(ref _kind, value);
            }
        }
    }

    public AppRule ToRule() => _kind.Kind == _rule.Kind ? _rule : _rule with { Kind = _kind.Kind, UpdatedAtUtc = DateTime.UtcNow };
}

public sealed class RuleKindOption(AppRuleKind kind)
{
    public AppRuleKind Kind { get; } = kind;

    public string Name => Tr.Get("Rule_" + Kind);

    public override string ToString() => Name;
}
