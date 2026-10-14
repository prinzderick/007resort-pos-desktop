using R007.Pos.Core.Api;
using R007.Pos.ViewModels.Services;
using Xunit;

namespace R007.Pos.Tests;

public sealed class PrintQueueTests
{
    private sealed class Source(params PrintJob[] jobs) : IPrintJobSource
    {
        public List<Guid> Marked { get; } = [];

        public bool Offline { get; set; }

        public Task<IReadOnlyList<PrintJob>> ListQueuedAsync(CancellationToken ct) =>
            Offline ? throw new ApiUnavailableException("down") : Task.FromResult<IReadOnlyList<PrintJob>>(jobs);

        public Task MarkPrintedAsync(Guid jobId, CancellationToken ct)
        {
            Marked.Add(jobId);
            return Task.CompletedTask;
        }
    }

    private static PrintJob Job(int copies = 1) => new(Guid.NewGuid(), Guid.NewGuid(), copies, "QUEUED");

    [Fact]
    public async Task Prints_each_queued_receipt_the_requested_number_of_times_and_marks_it_printed()
    {
        var a = Job(2);
        var b = Job();
        var source = new Source(a, b);
        var printed = new List<(Guid Receipt, int Copies)>();
        var c = new PrintQueueCoordinator(source, (r, n, _) => { printed.Add((r, n)); return Task.FromResult(true); });

        var done = await c.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, done);
        Assert.Equal([(a.ReceiptId, 2), (b.ReceiptId, 1)], printed);
        Assert.Equal([a.Id, b.Id], source.Marked);
    }

    [Fact]
    public async Task A_printer_problem_keeps_the_job_queued_and_stops_the_pass()
    {
        var source = new Source(Job(), Job());
        var c = new PrintQueueCoordinator(source, (_, _, _) => Task.FromResult(false));

        Assert.Equal(0, await c.RunOnceAsync(CancellationToken.None));
        Assert.Empty(source.Marked);
    }

    [Fact]
    public async Task No_connection_is_not_an_error()
    {
        var source = new Source(Job()) { Offline = true };
        var c = new PrintQueueCoordinator(source, (_, _, _) => Task.FromResult(true));

        Assert.Equal(0, await c.RunOnceAsync(CancellationToken.None));
    }
}
