using System.Windows.Controls;

namespace SLiveTranslator.UI.Pages;

public partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        // Google's block ends, MyMemory's day turns over: nothing tells about those, so the page reads them when shown.
        Loaded += (_, _) => (DataContext as HomeViewModel)?.Refresh();
    }
}
