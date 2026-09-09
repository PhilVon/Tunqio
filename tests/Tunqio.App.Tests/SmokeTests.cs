using Tunqio.App.Controls;
using Tunqio.Core.Library;

namespace Tunqio.App.Tests;

public class SmokeTests
{
    [Fact]
    public async Task An_incremental_items_source_pages_without_a_window()
    {
        var source = new IncrementalItemsSource<int>((after, _) => Task.FromResult<IReadOnlyList<int>>([after + 1, after + 2]), 2, take: 3);
        await source.LoadAllAsync();
        source.Should().Equal(1, 2, 3);
        source.HasMoreItems.Should().BeFalse();
    }
}
