using System.Windows.Controls;

namespace SLiveTranslator.UI.Pages;

public partial class HotkeysPage : Page
{
    public HotkeysPage()
    {
        InitializeComponent();
        // Another program may have taken a gesture since the page last showed.
        Loaded += (_, _) => (DataContext as HotkeysViewModel)?.PageShown();
    }
}
