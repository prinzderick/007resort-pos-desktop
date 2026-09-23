namespace R007.Pos.Core.Http;

public enum ConnectivityState
{
    Unknown,
    Online,
    Offline,
}

/// <summary>
/// Tracks whether the site API is reachable, fed by the HTTP pipeline (<see cref="ConnectivityHandler"/>) and an
/// optional periodic probe. Raises <see cref="Restored"/> on Offline -> Online so the emergency queue can drain.
/// </summary>
public sealed class ConnectivityMonitor
{
    public event EventHandler? Changed;

    public event EventHandler? Restored;

    public ConnectivityState State { get; private set; } = ConnectivityState.Unknown;

    public bool IsOffline => State == ConnectivityState.Offline;

    public void ReportSuccess() => Set(ConnectivityState.Online);

    public void ReportFailure() => Set(ConnectivityState.Offline);

    private void Set(ConnectivityState next)
    {
        var previous = State;
        if (previous == next)
        {
            return;
        }

        State = next;
        Changed?.Invoke(this, EventArgs.Empty);
        if (previous == ConnectivityState.Offline && next == ConnectivityState.Online)
        {
            Restored?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>Outermost handler: reports reachability to the <see cref="ConnectivityMonitor"/>.</summary>
public sealed class ConnectivityHandler(ConnectivityMonitor monitor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is 502 or 503 or 504)
            {
                monitor.ReportFailure();
            }
            else
            {
                monitor.ReportSuccess();
            }

            return response;
        }
        catch (HttpRequestException)
        {
            monitor.ReportFailure();
            throw;
        }
    }
}
