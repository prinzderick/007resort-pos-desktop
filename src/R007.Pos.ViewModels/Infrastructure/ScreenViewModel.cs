using R007.Pos.Core.Api;
using R007.Pos.Core.Offline;

namespace R007.Pos.ViewModels.Infrastructure;

/// <summary>Base for every screen and dialog: busy flag, operator-facing error/info line, uniform error mapping.</summary>
public abstract class ScreenViewModel : ObservableObject
{
    private bool _isBusy;
    private string? _error;
    private string? _info;

    public bool IsBusy
    {
        get => _isBusy;
        protected set => SetProperty(ref _isBusy, value);
    }

    public string? Error
    {
        get => _error;
        protected set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public string? Info
    {
        get => _info;
        protected set
        {
            if (SetProperty(ref _info, value))
            {
                OnPropertyChanged(nameof(HasInfo));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public bool HasInfo => !string.IsNullOrEmpty(_info);

    /// <summary>Called when the screen becomes visible (refresh data).</summary>
    public virtual Task ActivateAsync() => Task.CompletedTask;

    /// <summary>Maps any exception to something an operator can act on. Never leaks stack traces or tokens.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        ApiException { Code: "concurrency_conflict" } => "This changed on another device. It has been refreshed; please try again.",
        ApiException { Code: "balance_changed" } => "The balance changed. Please check the amount and try again.",
        ApiException { Code: "cash_session_required" } => "Open a cash session before taking cash.",
        ApiException { Code: "slot_unavailable" } => "That slot was just taken. Pick another.",
        ApiException { Code: "hold_expired" } => "The hold expired. Start the booking again.",
        ApiException { Code: "invalid_credentials" } => "Those credentials were not recognised.",
        ApiException { Code: "device_revoked" or "device_not_registered" } => "This terminal is not registered or was revoked. Contact IT.",
        ApiException { Code: "account_locked" } => "This account is locked. See a supervisor.",
        ApiException { IsPermissionDenied: true } => "You do not have permission to do that.",
        ApiException api => api.UserMessage,
        ApiUnavailableException => "Cannot reach the server. Check the network and try again.",
        OfflineQueueBlockedException blocked => blocked.Message,
        OperationCanceledException => "Cancelled.",
        _ => "Something went wrong. Please try again; if it persists, tell a supervisor.",
    };

    /// <summary>Runs an async action with the busy flag and uniform error handling. Returns true on success.</summary>
    protected async Task<bool> RunAsync(Func<Task> action)
    {
        Error = null;
        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = Describe(ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected void SetError(Exception ex) => Error = Describe(ex);
}

/// <summary>A dialog shown over the current screen. <see cref="Closed"/> completes when it closes.</summary>
public abstract class ModalViewModel : ScreenViewModel
{
    private readonly TaskCompletionSource<bool> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public abstract string Title { get; }

    public Task<bool> Closed => _closed.Task;

    /// <summary>Raised so the host can pop this dialog.</summary>
    public event EventHandler? CloseRequested;

    protected void Close(bool result)
    {
        _closed.TrySetResult(result);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Shows dialogs. Implemented by the shell; substituted in tests.</summary>
public interface INavigator
{
    /// <summary>Shows the modal and returns when it is closed (true = confirmed).</summary>
    Task<bool> ShowModalAsync(ModalViewModel modal);
}
