using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tunqio.App.Controls;

namespace Tunqio.App.Library;

/// <summary>Code-behind for Library › Genres.</summary>
public sealed partial class GenresPage : Page, ILibraryRefreshable
{
    private readonly LibraryFreshness _freshness = new();

    public GenresPage()
    {
        ViewModel = App.Services.GetRequiredService<GenresViewModel>();
        InitializeComponent();
    }

    public GenresViewModel ViewModel { get; }

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
        ViewModel.LoadAsync().Forget("Genres load");
    }

    private void OnTagClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GenreTag tag)
        {
            ViewModel.Open(tag);
        }
    }
}
