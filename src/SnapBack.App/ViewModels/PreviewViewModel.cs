using System.Windows.Input;
using System.Windows.Media;
using SnapBack.App.Localization;
using SnapBack.App.Mvvm;
using SnapBack.App.Services;
using SnapBack.Core;
using SnapBack.Core.Environments;
using SnapBack.Core.Execution;
using SnapBack.Core.Insights;
using SnapBack.Core.Planning;
using SnapBack.Core.Profiles;
using SnapBack.Core.Safety;
using SnapBack.Core.Text;

namespace SnapBack.App.ViewModels;

/// <summary>
/// Shows every action SnapBack is about to take - and everything it will leave alone - before
/// anything happens. The dry run evaluates the same live safety checks without acting.
/// </summary>
public sealed class PreviewViewModel : ObservableObject
{
    private readonly SnapBackEngine _engine;
    private readonly Analysis _analysis;
    private readonly EnvironmentService? _environments;
    private string _dryRunSummary = "";
    private bool _isChecking;
    private bool _isLoadingEnvironments;

    public PreviewViewModel(SnapBackEngine engine, Analysis analysis, IReadOnlyList<PlanItem> selected, EnvironmentService? environments = null)
    {
        _engine = engine;
        _analysis = analysis;
        _environments = environments;
        Selected = selected;
        Items = selected.Select(i => new PreviewItemViewModel(i)).ToList();
        DryRunCommand = new AsyncRelayCommand(DryRunAsync, () => !_isChecking);
        Impact = ImpactEstimate.Compute(analysis, selected);

        if (environments is not null)
        {
            var profile = analysis.Profile;
            EnvironmentOptions = EnvironmentDetector.Detect(analysis.Snapshot)
                .Where(e => e.IsRunning)
                .Select(e => new EnvironmentOptionViewModel(e, PolicyFor(profile, e.Kind) == EnvironmentPolicy.Stop))
                .ToList();
        }
    }

    public IReadOnlyList<PlanItem> Selected { get; }

    // ---- Expected impact (an estimate made before anything happens) ----------------------------

    public ImpactEstimate Impact { get; }

    public string ImpactTitle => Tr.Format("Preview_Impact", Tr.Get("Impact_" + Impact.Level));

    public string ImpactText => ImpactFormatter.Describe(Impact);

    // ---- WSL / Docker ------------------------------------------------------------------------

    public IReadOnlyList<EnvironmentOptionViewModel> EnvironmentOptions { get; } = [];

    public bool HasEnvironmentOptions => EnvironmentOptions.Count > 0;

    /// <summary>The confirm button waits until the environments have been inspected.</summary>
    public bool CanConfirm => !_isLoadingEnvironments;

    /// <summary>Inspects running environments with their own read-only commands (to show what would stop).</summary>
    public async Task LoadEnvironmentDetailsAsync()
    {
        if (_environments is null || EnvironmentOptions.Count == 0)
        {
            return;
        }

        _isLoadingEnvironments = true;
        OnPropertyChanged(nameof(CanConfirm));
        try
        {
            foreach (var option in EnvironmentOptions)
            {
                option.SetDetails(await _environments.InspectAsync(option.Kind));
            }
        }
        finally
        {
            _isLoadingEnvironments = false;
            OnPropertyChanged(nameof(CanConfirm));
        }
    }

    /// <summary>
    /// After the preview was confirmed: if environments are ticked, shows their warning once more and
    /// returns the confirmations, an empty list when none is ticked, or <c>null</c> when the user
    /// declined (then nothing at all happens).
    /// </summary>
    public IReadOnlyList<EnvironmentStopConfirmation>? ConfirmEnvironmentStops(IDialogService dialogs)
    {
        var chosen = EnvironmentOptions.Where(o => o.IsChecked && o.Details is { IsRunning: true, CanStop: true }).ToList();
        if (chosen.Count == 0)
        {
            return [];
        }

        var text = string.Join("\n\n", chosen.Select(o => o.Details!.Name + ":\n" + o.Details.Warning));
        if (!dialogs.Confirm(Tr.Get("Preview_Env_Confirm_Title"), text, dangerous: true))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        return EnvironmentStopConfirmation.InStopOrder(chosen.Select(o => EnvironmentStopConfirmation.FromUserConfirmation(o.Details!, now)));
    }

    private static EnvironmentPolicy PolicyFor(Profile? profile, EnvironmentKind kind) =>
        profile is null ? EnvironmentPolicy.Keep : kind == EnvironmentKind.Wsl ? profile.Wsl : profile.Docker;

    public IReadOnlyList<PreviewItemViewModel> Items { get; }

    public ICommand DryRunCommand { get; }

    public string Header => Tr.Format("Preview_Header", Selected.Count);

    public string SubHeader => Tr.Get("Preview_SubHeader");

    public string MemoryText => Tr.Format("Preview_Memory", ByteSize.Format(Selected.Sum(s => s.Group.ActionablePrivateWorkingSet)));

    public string TimeoutText => Tr.Format("Preview_Timeout", _engine.Settings.GracefulTimeoutSeconds);

    public string NotTouchedText
    {
        get
        {
            var plan = _analysis.Plan!;
            var unselected = plan.Candidates.Count - Selected.Count;
            var protectedProcesses = _analysis.Snapshot.Processes.Count
                - plan.Candidates.Concat(plan.Kept).Sum(i => i.Group.ActionableMembers.Count());
            return Tr.Format("Preview_NotTouched", plan.Kept.Count, unselected, Math.Max(0, protectedProcesses));
        }
    }

    public string ConfirmText => Tr.Format("Preview_Confirm", Selected.Count);

    public string DryRunSummary
    {
        get => _dryRunSummary;
        private set => Set(ref _dryRunSummary, value);
    }

    private async Task DryRunAsync()
    {
        _isChecking = true;
        DryRunSummary = Tr.Get("Preview_Checking");
        try
        {
            var report = await Task.Run(() => _engine.DryRun(_analysis, Selected));
            foreach (var item in report.Items)
            {
                Items.FirstOrDefault(i => i.Item.Group.Id == item.Item.Group.Id)?.ApplyDryRun(item);
            }

            DryRunSummary = Tr.Format("Preview_DryRunResult", report.WouldActOn, report.Items.Count);
        }
        finally
        {
            _isChecking = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

/// <summary>A running WSL / Docker environment offered in the preview, with what stopping it would end.</summary>
public sealed class EnvironmentOptionViewModel : ObservableObject
{
    private bool _isChecked;
    private EnvironmentDetails? _details;

    public EnvironmentOptionViewModel(EnvironmentPresence presence, bool suggested)
    {
        Presence = presence;
        _isChecked = suggested;
    }

    public EnvironmentPresence Presence { get; }

    public EnvironmentKind Kind => Presence.Kind;

    public EnvironmentDetails? Details => _details;

    public string Label => Tr.Format(Kind == EnvironmentKind.Wsl ? "Preview_Env_Wsl" : "Preview_Env_Docker",
        Presence.MemoryBytes > 0 ? ByteSize.Format(Presence.MemoryBytes) : "–");

    public bool IsChecked
    {
        get => _isChecked && CanStop;
        set => Set(ref _isChecked, value);
    }

    public bool CanStop => _details is { CanStop: true };

    public string StatusText => _details switch
    {
        null => Tr.Get("Env_Checking"),
        { IsRunning: false } d => Tr.Format("Env_Result_NotRunning", d.Name),
        { CanStop: false } d => d.CannotStopReason ?? "",
        { } d => d.Warning,
    };

    public void SetDetails(EnvironmentDetails details)
    {
        _details = details;
        OnPropertyChanged(string.Empty);
    }
}

public sealed class PreviewItemViewModel : ObservableObject
{
    private string? _dryRunText;

    public PreviewItemViewModel(PlanItem item)
    {
        Item = item;
        (Badge, BadgeText) = AppRowViewModel.BadgeFor(item);
        Steps = item.Steps.Select(s => new PreviewStepViewModel(s)).ToList();
    }

    public PlanItem Item { get; }

    public string Name => Item.Group.DisplayName;

    public string MemoryText => ByteSize.Format(Item.Group.ActionablePrivateWorkingSet);

    public BadgeKind Badge { get; }

    public string BadgeText { get; }

    public ImageSource? Icon => IconCache.Get(Item.Group.ExecutablePath);

    public IReadOnlyList<PreviewStepViewModel> Steps { get; }

    public IReadOnlyList<string> Notes => Item.Notes;

    public string? DryRunText
    {
        get => _dryRunText;
        private set => Set(ref _dryRunText, value);
    }

    public void ApplyDryRun(DryRunItem result)
    {
        DryRunText = result.ItemDecision.Allowed ? null : Tr.Format("Preview_WouldSkip", result.ItemDecision.Message);
        foreach (var step in result.Steps)
        {
            Steps.FirstOrDefault(s => s.Step == step.Step)?.Apply(step.Decision);
        }
    }
}

public sealed class PreviewStepViewModel : ObservableObject
{
    private string _check = "";

    public PreviewStepViewModel(PlannedStep step)
    {
        Step = step;
    }

    public PlannedStep Step { get; }

    public string Text => Step.Description;

    public string Glyph => Step.Kind switch
    {
        StepKind.CloseWindows => "",      // Cancel (X)
        StepKind.ConsoleInterrupt => "",  // CommandPrompt
        StepKind.RestartManager => "",    // PowerButton
        StepKind.ExitsWithApp => "",      // ChevronRight
        StepKind.NoGracefulMethod => "",  // Warning
        _ => "",                          // Lock
    };

    public string Check
    {
        get => _check;
        private set => Set(ref _check, value);
    }

    public void Apply(GuardDecision decision) =>
        Check = Step.Kind == StepKind.LeftAlone
            ? ""
            : decision.Allowed ? "✓ " + Tr.Get("Preview_CheckPassed") : "✗ " + decision.Message;
}
