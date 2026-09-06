//! The merged history + settings window (viewer.rs + settings.rs live on
//! separate GDI windows in the Rust build; WinUI3's NavigationView sidebar
//! merges them into one window). Dark/light follows the system theme.

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RazerTaskbar.Core;

namespace RazerTaskbar;

public sealed partial class MainWindow : Window
{

    public MainWindow()
    {
        InitializeComponent();
        // Acrylic backdrop. Works on Windows 11; where unsupported (Win10 /
        // older builds) the backdrop is ignored and falls back to a solid
        // color (docs: system-backdrops).
        SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Reasonable minimums (viewer.rs: 920x660 default, 860x600 min).
        AppWindow.Resize(new Windows.Graphics.SizeInt32(980, 680));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 860;
            presenter.PreferredMinimumHeight = 600;
        }

        NavHistory.Content ??= I18n.Tr("Battery history");
        NavSettings.Content ??= I18n.Tr("Settings");
        SyncLanguage();

        Nav.SelectedItem = NavHistory;
        ContentFrame.Navigate(typeof(Views.HistoryPage));
    }

    /// <summary>Open the window on the given tab; no-op when already there.</summary>
    public void OpenTab(bool selectSettings)
    {
        var target = selectSettings ? (NavigationViewItem)NavSettings : (NavigationViewItem)NavHistory;
        if (!ReferenceEquals(Nav.SelectedItem, target))
        {
            Nav.SelectedItem = target;
        }
        else
        {
            Navigate(target);
        }
    }

    public void SyncLanguage()
    {
        // The title is just the project name — it does not advertise which
        // tab is open (the nav pane highlights that).
        TitleText.Text = "Razer Taskbar";
        NavHistory.Content = I18n.Tr("Battery history");
        NavSettings.Content = I18n.Tr("Settings");
        if (ContentFrame.Content is Views.HistoryPage history)
        {
            history.Localize();
        }
        else if (ContentFrame.Content is Views.SettingsPage settings)
        {
            settings.Localize();
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }
        Navigate(item);
    }

    private void Navigate(NavigationViewItem item)
    {
        if (ReferenceEquals(item, NavHistory))
        {
            if (ContentFrame.Content is not Views.HistoryPage)
            {
                ContentFrame.Navigate(typeof(Views.HistoryPage));
            }
        }
        else
        {
            if (ContentFrame.Content is not Views.SettingsPage)
            {
                ContentFrame.Navigate(typeof(Views.SettingsPage));
            }
        }
    }
}
