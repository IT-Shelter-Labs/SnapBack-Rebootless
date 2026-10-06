using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using SnapBack.App.Localization;
using SnapBack.App.Mvvm;
using SnapBack.App.Services;
using SnapBack.Core;
using SnapBack.Core.Baselines;
using SnapBack.Core.Grouping;
using SnapBack.Core.Localization;
using SnapBack.Core.Profiles;

namespace SnapBack.App.ViewModels;

/// <summary>
/// Create, rename, update, delete and edit profiles ("Clean", "Work", "Gaming", ...). Every profile
/// is user data; nothing here closes anything — switching happens through the normal preview.
/// </summary>
public sealed class ProfilesViewModel : ObservableObject
{
    private readonly SnapBackEngine _engine;
    private readonly IDialogService _dialogs;
    private readonly Func<Analysis?> _analysis;
    private ProfileItemViewModel? _selected;
    private RunningAppOption? _selectedAddable;
    private bool _isBusy;
    private bool _loading;

    public ProfilesViewModel(SnapBackEngine engine, IDialogService dialogs, Func<Analysis?> analysis)
    {
        _engine = engine;
        _dialogs = dialogs;
        _analysis = analysis;

        NewCommand = new AsyncRelayCommand(NewAsync, () => !_isBusy && Profiles.Count < ProfileSet.MaxProfiles);
        UpdateCommand = new AsyncRelayCommand(UpdateAsync, () => !_isBusy && Selected is not null);
        RenameCommand = new RelayCommand(Rename, () => !_isBusy && Selected is not null);
        DeleteCommand = new RelayCommand(Delete, () => !_isBusy && Selected is not null);
        SetDefaultCommand = new RelayCommand(SetDefault, () => !_isBusy && Selected is { IsDefault: false });
        UseCommand = new RelayCommand(Use, () => !_isBusy && Selected is { IsCurrent: false });
        RemoveAppCommand = new RelayCommand(p => RemoveApp(p as ProfileAppViewModel), _ => !_isBusy);
        AddAppCommand = new RelayCommand(AddApp, () => !_isBusy && SelectedAddable is not null && Selected is not null);

        Reload(engine.Profiles.Current?.Id);
    }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    public ObservableCollection<ProfileAppViewModel> Apps { get; } = [];

    public ObservableCollection<RunningAppOption> AddableApps { get; } = [];

    public ICommand NewCommand { get; }

    public ICommand UpdateCommand { get; }

    public ICommand RenameCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand SetDefaultCommand { get; }

    public ICommand UseCommand { get; }

    public ICommand RemoveAppCommand { get; }

    public ICommand AddAppCommand { get; }

    public bool IsEmpty => Profiles.Count == 0;

    public bool HasSelection => Selected is not null;

    public ProfileItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                LoadSelected();
                OnPropertyChanged(nameof(HasSelection));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public RunningAppOption? SelectedAddable
    {
        get => _selectedAddable;
        set
        {
            Set(ref _selectedAddable, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool StopWsl
    {
        get => Selected?.Profile.Wsl == EnvironmentPolicy.Stop;
        set => SetPolicy(value, null);
    }

    public bool StopDocker
    {
        get => Selected?.Profile.Docker == EnvironmentPolicy.Stop;
        set => SetPolicy(null, value);
    }

    public string AppsHeader => Selected is { } s ? Tr.Format("Profiles_Apps", s.Profile.Name, Apps.Count) : "";

    private void Reload(Guid? select)
    {
        var set = _engine.Profiles;
        Profiles.Clear();
        foreach (var p in set.Profiles)
        {
            Profiles.Add(new ProfileItemViewModel(p, p.Id == set.DefaultProfileId, p.Id == set.Current?.Id));
        }

        Selected = Profiles.FirstOrDefault(p => p.Profile.Id == select) ?? Profiles.FirstOrDefault();
        LoadSelected();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void LoadSelected()
    {
        _loading = true;
        try
        {
            Apps.Clear();
            AddableApps.Clear();
            if (Selected is not { } item)
            {
                return;
            }

            foreach (var app in item.Profile.Baseline.Apps.Where(a => !a.WasProtected))
            {
                Apps.Add(new ProfileAppViewModel(app));
            }

            var inProfile = item.Profile.Baseline.Apps.Select(a => a.AppKey).ToHashSet(StringComparer.Ordinal);
            var groups = _analysis()?.Groups ?? [];
            foreach (var g in groups.Where(g => !g.IsProtected && !inProfile.Contains(g.AppKey)).DistinctBy(g => g.AppKey)
                         .OrderBy(g => g.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                AddableApps.Add(new RunningAppOption(g));
            }
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(StopWsl));
        OnPropertyChanged(nameof(StopDocker));
        OnPropertyChanged(nameof(AppsHeader));
    }

    private async Task NewAsync()
    {
        var name = _dialogs.Prompt(Tr.Get("Profiles_New_Title"), Tr.Get("Profiles_New_Text"), Suggestion(), Suggestions());
        if (name is null)
        {
            return;
        }

        await Busy(async () =>
        {
            var p = await Task.Run(() => _engine.CreateProfile(name, BaselineOrigin.Manual));
            Reload(p.Id);
        });
    }

    private async Task UpdateAsync()
    {
        if (Selected is not { } item
            || !_dialogs.Confirm(Tr.Get("Confirm_ReplaceBaseline_Title"), Tr.Format("Confirm_ReplaceProfile_Text", item.Profile.Name)))
        {
            return;
        }

        await Busy(async () =>
        {
            await Task.Run(() => _engine.UpdateProfile(item.Profile.Id, BaselineOrigin.Manual));
            Reload(item.Profile.Id);
        });
    }

    private void Rename()
    {
        if (Selected is not { } item)
        {
            return;
        }

        var name = _dialogs.Prompt(Tr.Get("Profiles_Rename_Title"), Tr.Get("Profiles_Rename_Text"), item.Profile.Name, Suggestions());
        if (name is null)
        {
            return;
        }

        _engine.RenameProfile(item.Profile.Id, name);
        Reload(item.Profile.Id);
    }

    private void Delete()
    {
        if (Selected is not { } item
            || !_dialogs.Confirm(Tr.Get("Profiles_Delete_Title"), Tr.Format("Profiles_Delete_Text", item.Profile.Name), dangerous: true))
        {
            return;
        }

        _engine.DeleteProfile(item.Profile.Id);
        Reload(null);
    }

    private void SetDefault()
    {
        if (Selected is { } item)
        {
            _engine.SetDefaultProfile(item.Profile.Id);
            Reload(item.Profile.Id);
        }
    }

    private void Use()
    {
        if (Selected is { } item)
        {
            _engine.SelectProfile(item.Profile.Id);
            Reload(item.Profile.Id);
        }
    }

    private void RemoveApp(ProfileAppViewModel? app)
    {
        if (Selected is not { } item || app is null)
        {
            return;
        }

        _engine.RemoveAppFromProfile(item.Profile.Id, app.App.AppKey);
        Reload(item.Profile.Id);
    }

    private void AddApp()
    {
        if (Selected is not { } item || SelectedAddable is not { } option)
        {
            return;
        }

        var groups = (_analysis()?.Groups ?? []).Where(g => g.AppKey == option.AppKey).ToList();
        _engine.AddAppsToProfile(item.Profile.Id, groups);
        Reload(item.Profile.Id);
    }

    private void SetPolicy(bool? wsl, bool? docker)
    {
        if (_loading || Selected is not { } item)
        {
            return;
        }

        var p = item.Profile;
        var updated = _engine.SetEnvironmentPolicy(p.Id,
            wsl is { } w ? (w ? EnvironmentPolicy.Stop : EnvironmentPolicy.Keep) : p.Wsl,
            docker is { } d ? (d ? EnvironmentPolicy.Stop : EnvironmentPolicy.Keep) : p.Docker);
        item.Profile = updated;
        OnPropertyChanged(nameof(StopWsl));
        OnPropertyChanged(nameof(StopDocker));
    }

    private async Task Busy(Func<Task> action)
    {
        _isBusy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await action();
        }
        catch (InvalidOperationException ex)
        {
            _dialogs.Inform(Tr.Get("Profiles_Title"), ex.Message);
        }
        finally
        {
            _isBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Editable suggestions for new profile names (nothing is created from them automatically).</summary>
    private static IReadOnlyList<string> Suggestions() =>
        new[] { "Profile_Suggest_Clean", "Profile_Suggest_Work", "Profile_Suggest_Gaming", "Profile_Suggest_Streaming" }
            .Select(Tr.Get).ToList();

    private string Suggestion()
    {
        var taken = Profiles.Select(p => p.Profile.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        return Suggestions().FirstOrDefault(s => !taken.Contains(s)) ?? Tr.Get("Profile_Suggest_Custom");
    }
}

public sealed class ProfileItemViewModel(Profile profile, bool isDefault, bool isCurrent) : ObservableObject
{
    private Profile _profile = profile;

    public Profile Profile
    {
        get => _profile;
        set
        {
            _profile = value;
            OnPropertyChanged(string.Empty);
        }
    }

    public bool IsDefault { get; } = isDefault;

    public bool IsCurrent { get; } = isCurrent;

    public string Name => Profile.Name;

    public string Details
    {
        get
        {
            var text = Tr.Format("Profiles_Details", Profile.AppCount, Profile.UpdatedAtUtc.ToLocalTime().ToString("g", Loc.Culture));
            var tags = new List<string>();
            if (IsDefault)
            {
                tags.Add(Tr.Get("Profiles_Default"));
            }

            if (IsCurrent)
            {
                tags.Add(Tr.Get("Profiles_Current"));
            }

            return tags.Count == 0 ? text : text + " · " + string.Join(" · ", tags);
        }
    }

    /// <summary>Used by screen readers (UI Automation name of the list item).</summary>
    public override string ToString() => Name;
}

public sealed class ProfileAppViewModel(BaselineApp app)
{
    public BaselineApp App { get; } = app;

    public string Name => App.DisplayName;

    public string? Path => App.ExecutablePath;

    public ImageSource? Icon => IconCache.Get(App.ExecutablePath);

    public override string ToString() => Name;
}

public sealed class RunningAppOption(AppGroup group)
{
    public string AppKey { get; } = group.AppKey;

    public string Name { get; } = group.DisplayName;

    public override string ToString() => Name;
}
