using System.Windows.Controls;

namespace SLiveTranslator.UI.Pages;

public partial class TranslationPage : Page
{
    public TranslationPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as TranslationViewModel)?.PageShown();
        Unloaded += (_, _) => (DataContext as TranslationViewModel)?.PageHidden();
    }
}
