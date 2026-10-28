using System.Windows.Controls;

namespace SLiveTranslator.UI.Pages;

public partial class GeneralPage : Page
{
    public GeneralPage()
    {
        InitializeComponent();
        // The Run value may have been changed or gone stale while the page was away.
        Loaded += (_, _) => (DataContext as GeneralViewModel)?.PageShown();
    }
}
