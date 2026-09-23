using System.Collections.ObjectModel;
using R007.Pos.Core.Api;
using R007.Pos.ViewModels.Infrastructure;

namespace R007.Pos.ViewModels.Screens;

/// <summary>Find a member by name or membership number (<c>GET /memberships?q=</c>). Permission: <c>membership.view</c>.</summary>
public sealed class CustomerLookupViewModel : ModalViewModel
{
    private readonly PosContext _ctx;
    private string _query = string.Empty;

    public CustomerLookupViewModel(PosContext ctx)
    {
        _ctx = ctx;
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsBusy, SetError);
        SelectCommand = new RelayCommand(p =>
        {
            if (p is Membership m)
            {
                Selected = m;
                Close(true);
            }
        });
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public override string Title => "Find a member";

    public string Query
    {
        get => _query;
        set => SetProperty(ref _query, value);
    }

    public ObservableCollection<Membership> Results { get; } = [];

    public Membership? Selected { get; private set; }

    public AsyncRelayCommand SearchCommand { get; }

    public RelayCommand SelectCommand { get; }

    public RelayCommand CancelCommand { get; }

    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            Error = "Type a name or membership number.";
            return;
        }

        await RunAsync(async () =>
        {
            var found = await _ctx.Api.SearchMembershipsAsync(Query.Trim()).ConfigureAwait(true);
            Results.Clear();
            foreach (var m in found)
            {
                Results.Add(m);
            }

            Info = found.Count == 0 ? "No members found." : null;
        }).ConfigureAwait(true);
    }
}
