using System.Collections.Specialized;
using Tunqio.Core.Library;

namespace Tunqio.Core.Tests;

/// <summary>E3-S3: the page-appending list every virtualised view binds to.</summary>
public class IncrementalListTests
{
    /// <summary>A loader over a fixed row set that counts its calls and can be held open.</summary>
    private sealed class FakeLoader
    {
        private readonly int[] _rows;
        private readonly int _pageSize;

        public FakeLoader(int rows, int pageSize)
        {
            _rows = Enumerable.Range(1, rows).ToArray();
            _pageSize = pageSize;
        }

        public int Calls { get; private set; }

        public TaskCompletionSource? Gate { get; set; }

        public Exception? Throw { get; set; }

        public async Task<IReadOnlyList<int>> LoadAsync(int after, CancellationToken ct)
        {
            Calls++;
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
            }

            if (Throw is { } e)
            {
                Throw = null;
                throw e;
            }

            return _rows.Where(r => r > after).Take(_pageSize).ToList();
        }
    }

    [Fact]
    public async Task Pages_are_appended_in_order_and_a_short_page_ends_the_list_Async()
    {
        var loader = new FakeLoader(450, 200);
        var list = new IncrementalList<int>(loader.LoadAsync, 200);
        int adds = 0;
        list.CollectionChanged += (_, e) => adds += e.Action == NotifyCollectionChangedAction.Add ? e.NewItems!.Count : 0;

        (await list.LoadMoreAsync()).Should().Be(200);
        list.HasMore.Should().BeTrue();
        (await list.LoadMoreAsync()).Should().Be(200);
        (await list.LoadMoreAsync()).Should().Be(50);
        list.HasMore.Should().BeFalse();
        (await list.LoadMoreAsync()).Should().Be(0, "a complete list does not call the loader again");

        loader.Calls.Should().Be(3);
        list.Should().Equal(Enumerable.Range(1, 450));
        adds.Should().Be(450, "every row is announced");
        list.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task An_exact_multiple_of_the_page_size_needs_one_empty_page_to_finish_Async()
    {
        var loader = new FakeLoader(400, 200);
        var list = new IncrementalList<int>(loader.LoadAsync, 200);

        await list.LoadMoreAsync();
        await list.LoadMoreAsync();
        list.HasMore.Should().BeTrue("a full page cannot prove the end");
        (await list.LoadMoreAsync()).Should().Be(0);
        list.HasMore.Should().BeFalse();
        list.Count.Should().Be(400);
    }

    [Fact]
    public async Task The_take_cap_trims_the_last_page_and_ends_the_list_Async()
    {
        var loader = new FakeLoader(10_000, 200);
        var list = new IncrementalList<int>(loader.LoadAsync, 200, take: 500);

        await list.LoadAllAsync();

        list.Count.Should().Be(500);
        list.HasMore.Should().BeFalse();
        loader.Calls.Should().Be(3);
        list.Take.Should().Be(500);
    }

    [Fact]
    public async Task A_second_call_while_a_page_loads_waits_and_adds_nothing_of_its_own_Async()
    {
        var loader = new FakeLoader(450, 200) { Gate = new TaskCompletionSource() };
        var list = new IncrementalList<int>(loader.LoadAsync, 200);

        Task<int> first = list.LoadMoreAsync();
        Task<int> second = list.LoadMoreAsync();
        list.IsLoading.Should().BeTrue();
        loader.Gate.SetResult();

        (await first).Should().Be(200);
        (await second).Should().Be(0);
        loader.Calls.Should().Be(1, "the loader is not re-entered");
        list.Count.Should().Be(200);
        list.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task Reset_discards_a_page_that_was_still_loading_Async()
    {
        var loader = new FakeLoader(450, 200) { Gate = new TaskCompletionSource() };
        var list = new IncrementalList<int>(loader.LoadAsync, 200);
        int resets = 0;
        list.CollectionChanged += (_, e) => resets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;

        Task<int> stale = list.LoadMoreAsync();
        list.Reset();
        loader.Gate.SetResult();

        (await stale).Should().Be(0);
        list.Should().BeEmpty("the page belonged to the query before the reset");
        list.HasMore.Should().BeTrue();
        list.IsLoading.Should().BeFalse();
        resets.Should().Be(1);

        loader.Gate = null;
        (await list.LoadMoreAsync()).Should().Be(200, "the list starts again from the top");
        list[0].Should().Be(1);
    }

    [Fact]
    public async Task A_failed_load_propagates_and_leaves_the_list_loadable_Async()
    {
        var loader = new FakeLoader(450, 200) { Throw = new InvalidOperationException("database locked") };
        var list = new IncrementalList<int>(loader.LoadAsync, 200);

        Func<Task> act = () => list.LoadMoreAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("database locked");

        list.LastError.Should().BeOfType<InvalidOperationException>();
        list.IsLoading.Should().BeFalse();
        list.HasMore.Should().BeTrue();
        (await list.LoadMoreAsync()).Should().Be(200, "the next call retries");
        list.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_propagates_and_leaves_the_list_loadable_Async()
    {
        var loader = new FakeLoader(450, 200) { Gate = new TaskCompletionSource() };
        var list = new IncrementalList<int>(loader.LoadAsync, 200);
        using var cts = new CancellationTokenSource();

        Task<int> load = list.LoadMoreAsync(cts.Token);
        await cts.CancelAsync();
        OperationCanceledException? thrown = null;
        try
        {
            await load;
        }
        catch (OperationCanceledException e)
        {
            thrown = e;
        }

        thrown.Should().NotBeNull();

        list.IsLoading.Should().BeFalse();
        list.HasMore.Should().BeTrue();
        loader.Gate = null;
        (await list.LoadMoreAsync()).Should().Be(200);
    }

    [Fact]
    public async Task A_load_never_completes_synchronously_even_when_the_loader_does_Async()
    {
        // Microsoft.Data.Sqlite completes inline; a page landing inside a ListView measure pass re-enters it.
        int callerThread = Environment.CurrentManagedThreadId;
        int loaderThread = 0;
        var list = new IncrementalList<int>(
            (after, _) =>
            {
                loaderThread = Environment.CurrentManagedThreadId;
                return Task.FromResult<IReadOnlyList<int>>([after + 1, after + 2]);
            },
            pageSize: 2);

        Task<int> load = list.LoadMoreAsync();

        load.IsCompleted.Should().BeFalse("the loader is queued to the pool, not run inline");
        (await load).Should().Be(2);
        loaderThread.Should().NotBe(callerThread);
    }

    [Fact]
    public async Task A_request_made_while_a_page_is_being_appended_waits_for_that_page_Async()
    {
        // The ListView reacts to every Add; if it asks for more mid-page it must not start a second load.
        var loader = new FakeLoader(450, 200);
        var list = new IncrementalList<int>(loader.LoadAsync, 200);
        Task<int>? nested = null;
        list.CollectionChanged += (_, _) => nested ??= list.LoadMoreAsync();

        (await list.LoadMoreAsync()).Should().Be(200);
        (await nested!).Should().Be(0, "it joined the load that was appending");
        loader.Calls.Should().Be(1);
        list.Count.Should().Be(200);
    }

    [Fact]
    public async Task State_changes_are_announced_Async()
    {
        var loader = new FakeLoader(50, 200);
        var list = new IncrementalList<int>(loader.LoadAsync, 200);
        var changed = new List<string>();
        ((System.ComponentModel.INotifyPropertyChanged)list).PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        await list.LoadMoreAsync();

        changed.Should().Contain("IsLoading").And.Contain("HasMore").And.Contain("Count");
    }

    [Fact]
    public void Arguments_are_checked()
    {
        var loader = new FakeLoader(1, 1);
        FluentActions.Invoking(() => new IncrementalList<int>(loader.LoadAsync, 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new IncrementalList<int>(loader.LoadAsync, 10, take: -1)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new IncrementalList<int>(null!, 10)).Should().Throw<ArgumentNullException>();
    }
}
