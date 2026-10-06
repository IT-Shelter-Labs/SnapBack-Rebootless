using System.Windows.Input;
using System.Windows.Media;
using SnapBack.App.Mvvm;
using SnapBack.App.Services;
using SnapBack.Core;
using SnapBack.Core.Execution;
using SnapBack.Core.Planning;
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
    private string _dryRunSummary = "";
    private bool _isChecking;

    public PreviewViewModel(SnapBackEngine engine, Analysis analysis, IReadOnlyList<PlanItem> selected)
    {
        _engine = engine;
        _analysis = analysis;
        Selected = selected;
        Items = selected.Select(i => new PreviewItemViewModel(i)).ToList();
        DryRunCommand = new AsyncRelayCommand(DryRunAsync, () => !_isChecking);
    }

    public IReadOnlyList<PlanItem> Selected { get; }

    public IReadOnlyList<PreviewItemViewModel> Items { get; }

    public ICommand DryRunCommand { get; }

    public string Header => $"Close {Selected.Count} app{(Selected.Count == 1 ? "" : "s")}?";

    public string SubHeader => "Preview — nothing has been changed yet. This is exactly what SnapBack will do:";

    public string MemoryText => "These apps use about " + ByteSize.Format(Selected.Sum(s => s.Group.ActionablePrivateWorkingSet)) + " of memory.";

    public string TimeoutText =>
        $"Each app is asked to close the normal way and gets up to {_engine.Settings.GracefulTimeoutSeconds} seconds. "
        + "Apps can ask you to save your work. Nothing is force-terminated: if an app does not close, you decide afterwards.";

    public string NotTouchedText
    {
        get
        {
            var plan = _analysis.Plan!;
            var unselected = plan.Candidates.Count - Selected.Count;
            var protectedProcesses = _analysis.Snapshot.Processes.Count
                - plan.Candidates.Concat(plan.Kept).Sum(i => i.Group.ActionableMembers.Count());
            return $"Not touched: {plan.Kept.Count} app(s) from your baseline or keep list, {unselected} new app(s) you did not select, "
                + $"and {Math.Max(0, protectedProcesses)} system, service, elevated or other protected processes.";
        }
    }

    public string ConfirmText => $"Close {Selected.Count} app{(Selected.Count == 1 ? "" : "s")}";

    public string DryRunSummary
    {
        get => _dryRunSummary;
        private set => Set(ref _dryRunSummary, value);
    }

    private async Task DryRunAsync()
    {
        _isChecking = true;
        DryRunSummary = "Checking…";
        try
        {
            var report = await Task.Run(() => _engine.DryRun(_analysis, Selected));
            foreach (var item in report.Items)
            {
                Items.FirstOrDefault(i => i.Item.Group.Id == item.Item.Group.Id)?.ApplyDryRun(item);
            }

            DryRunSummary = $"Dry run: {report.WouldActOn} of {report.Items.Count} app(s) would be asked to close. Nothing was changed.";
        }
        finally
        {
            _isChecking = false;
            CommandManager.InvalidateRequerySuggested();
        }
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
        DryRunText = result.ItemDecision.Allowed ? null : "Would be skipped: " + result.ItemDecision.Message;
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
            : decision.Allowed ? "✓ passes the live safety checks" : "✗ " + decision.Message;
}
