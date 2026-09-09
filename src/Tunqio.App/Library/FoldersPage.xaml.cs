using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tunqio.App.Controls;

namespace Tunqio.App.Library;

/// <summary>Code-behind for Library › Folders.</summary>
public sealed partial class FoldersPage : Page, ILibraryRefreshable
{
    private readonly LibraryFreshness _freshness = new();

    public FoldersPage()
    {
        ViewModel = App.Services.GetRequiredService<FoldersViewModel>();
        InitializeComponent();
    }

    public FoldersViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_freshness.ShouldLoad(e))
        {
            RefreshLibrary();
        }
    }

    public void RefreshLibrary()
    {
        _freshness.MarkLoaded();
        ViewModel.LoadAsync().Forget("Folders load");
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FolderRow row)
        {
            ViewModel.Open(row);
        }
    }
}
