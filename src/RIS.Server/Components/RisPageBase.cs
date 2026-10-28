using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Localization;
using MudBlazor;
using RIS.Jobs.Quartz;
using RIS.Persistence;
using RIS.Server.Resources;
using RIS.Server.Services;
using RIS.Server.Ui;

namespace RIS.Server.Components;

/// <summary>
/// Base of RIS pages: loads data, refreshes it live when a job starts/finishes or the configuration changes
/// (throttled), and periodically (next run times move on).
/// </summary>
public abstract class RisPageBase : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _disposed = new();
    private Timer? _debounce;
    private PeriodicTimer? _periodic;

    [Inject] protected IStringLocalizer<UiText> Localizer { get; set; } = null!;
    [Inject] protected JobQueryService Queries { get; set; } = null!;
    [Inject] protected JobOperationsService Operations { get; set; } = null!;
    [Inject] protected ISnackbar Snackbar { get; set; } = null!;
    [Inject] protected IDialogService Dialogs { get; set; } = null!;
    [Inject] protected NavigationManager Navigation { get; set; } = null!;
    [Inject] private RunningJobs RunningJobs { get; set; } = null!;
    [Inject] private DbConfigurationStore ConfigurationStore { get; set; } = null!;

    [CascadingParameter] protected UiPreferences Prefs { get; set; } = new();
    [CascadingParameter] protected Task<AuthenticationState>? AuthenticationState { get; set; }

    protected bool Loading { get; private set; } = true;
    protected string? UserName { get; private set; }
    protected bool CanOperate { get; private set; }
    protected bool CanAdminister { get; private set; }

    /// <summary>Period of the background refresh; null = only event driven.</summary>
    protected virtual TimeSpan? RefreshInterval => TimeSpan.FromSeconds(30);

    protected CancellationToken Disposed => _disposed.Token;

    protected abstract Task LoadAsync();

    protected override async Task OnInitializedAsync()
    {
        if (AuthenticationState is not null)
        {
            var user = (await AuthenticationState).User;
            UserName = user.Identity?.Name;
            CanOperate = user.IsInRole(RisRoles.Operator) || user.IsInRole(RisRoles.Admin);
            CanAdminister = user.IsInRole(RisRoles.Admin);
        }

        RunningJobs.Changed += OnChanged;
        ConfigurationStore.Reloaded += OnChanged;
        await ReloadAsync();
        if (RefreshInterval is { } interval) _ = RefreshPeriodicallyAsync(interval);
    }

    protected async Task ReloadAsync()
    {
        try
        {
            await LoadAsync();
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>Shows the outcome of a bulk operation: one line when everything succeeded, otherwise the failures.</summary>
    protected void ShowResults(IReadOnlyList<BulkItemResult> results, string doneKey)
    {
        var failed = results.Where(r => !r.Succeeded).ToList();
        if (failed.Count == 0)
        {
            Snackbar.Add(Localizer[doneKey, results.Count], Severity.Success);
            return;
        }
        Snackbar.Add(Localizer["Bulk_PartlyFailed", results.Count - failed.Count, failed.Count] + " " +
                     string.Join("; ", failed.Select(f => $"{f.Job}: {f.Error}")), Severity.Warning,
            options => options.RequireInteraction = true);
    }

    protected async Task RunJobsAsync(IReadOnlyList<JobRef> jobs)
    {
        if (jobs.Count > 1 && !await ConfirmAsync(Localizer["Run_ConfirmTitle", jobs.Count], Localizer["Run_ConfirmText"], Localizer["Action_Run"], Color.Primary, jobs))
        {
            return;
        }
        ShowResults(await Operations.RunAsync(jobs, UserName), "Action_Started");
        await ReloadAsync();
    }

    protected async Task PauseJobsAsync(IReadOnlyList<JobRef> jobs)
    {
        var dialog = await Dialogs.ShowAsync<Shared.PauseDialog>(Localizer["Action_Pause"],
            new DialogParameters<Shared.PauseDialog> { { d => d.Jobs, jobs.Select(j => j.Key).ToList() } },
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        var result = await dialog.Result;
        if (result is null || result.Canceled) return;
        ShowResults(await Operations.PauseAsync(jobs, result.Data as string, UserName), "Action_Paused");
        await ReloadAsync();
    }

    protected async Task ResumeJobsAsync(IReadOnlyList<JobRef> jobs)
    {
        ShowResults(await Operations.ResumeAsync(jobs, UserName), "Action_Resumed");
        await ReloadAsync();
    }

    protected async Task<bool> DeleteJobsAsync(IReadOnlyList<JobRef> jobs)
    {
        if (!await ConfirmAsync(Localizer["Delete_ConfirmTitle", jobs.Count], Localizer["Delete_ConfirmText"], Localizer["Common_Delete"], Color.Error, jobs))
        {
            return false;
        }
        var results = await Operations.DeleteAsync(jobs, UserName);
        ShowResults(results, "Action_Deleted");
        await ReloadAsync();
        return results.All(r => r.Succeeded);
    }

    protected Task<bool> ConfirmAsync(string title, string message, string confirm, Color colour, IEnumerable<JobRef>? items = null) =>
        ConfirmAsync(title, message, confirm, colour, items?.Select(i => i.Key).ToList() ?? []);

    protected async Task<bool> ConfirmAsync(string title, string message, string confirm, Color colour, IReadOnlyList<string> items)
    {
        var dialog = await Dialogs.ShowAsync<Shared.ConfirmDialog>(title, new DialogParameters<Shared.ConfirmDialog>
        {
            { d => d.Title, title },
            { d => d.Message, message },
            { d => d.ConfirmText, confirm },
            { d => d.ConfirmColor, colour },
            { d => d.Items, items }
        }, new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        var result = await dialog.Result;
        return result is { Canceled: false };
    }

    private void OnChanged()
    {
        // Several events arrive together (start of many jobs, reload + resync): refresh once.
        _debounce?.Dispose();
        _debounce = new Timer(_ => _ = InvokeAsync(async () =>
        {
            if (_disposed.IsCancellationRequested) return;
            await LoadAsync();
            StateHasChanged();
        }), null, TimeSpan.FromSeconds(1.5), Timeout.InfiniteTimeSpan);
    }

    private async Task RefreshPeriodicallyAsync(TimeSpan interval)
    {
        _periodic = new PeriodicTimer(interval);
        try
        {
            while (await _periodic.WaitForNextTickAsync(_disposed.Token))
            {
                await InvokeAsync(async () =>
                {
                    await LoadAsync();
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        RunningJobs.Changed -= OnChanged;
        ConfigurationStore.Reloaded -= OnChanged;
        _disposed.Cancel();
        _debounce?.Dispose();
        _periodic?.Dispose();
        GC.SuppressFinalize(this);
    }
}
