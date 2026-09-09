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
        // assets/app.ico is copied next to the exe (csproj None item); the
        // taskbar / alt-tab button falls back to a generic icon otherwise.
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico"));
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

        // Tray-resident app: this is the only XAML window, and destroying it
        // would shut down the XAML dispatcher — Application.Start's loop
        // returns and the process exits, taking the widget threads with it
        // (the Rust build's GDI settings window just destroys itself). Hide
        // instead; the tray menu / widget menu reopen it.
        AppWindow.Closing += (sender, args) =>
        {
            args.Cancel = true;
            sender.Hide();
        };
    }

    /// <summary>Open the window on the given tab; no-op when already there.</summary>
    public void OpenTab(bool selectSettings)
    {
        NavigationViewItem target = selectSettings ? NavSettings : NavHistory;
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
        UpdateWindowTitle();
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
        UpdateWindowTitle();
    }

    /// <summary>Alt-tab / taskbar caption follows the open tab: the tray and
    /// widget menus open this one window as either "Battery history…" or
    /// "Settings…", and the caption is invisible in-app anyway
    /// (ExtendsContentIntoTitleBar) — the zh map carries the composed
    /// strings.</summary>
    private void UpdateWindowTitle()
        => Title = ContentFrame.Content is Views.SettingsPage
            ? I18n.Tr("Settings — Razer Taskbar")
            : I18n.Tr("Battery history — Razer Taskbar");
}
