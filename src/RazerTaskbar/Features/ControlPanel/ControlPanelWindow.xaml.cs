//! The settings + history window (port of viewer.rs + settings.rs, merged
//! into one NavigationView window). Dark/light follows the system theme.
//!
//! Lifetime (docs/agent-architecture.md): this window is DISPOSABLE. Closing
//! it destroys it — the page tree, its ~98 MB of private bytes and its XAML
//! handles are handed back — while the process keeps running on the host's
//! keep-alive window. `ControlPanelFeature` builds a fresh one on the next
//! open. There is deliberately no hide-on-close hook here any more.

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RazerTaskbar.Core;

namespace RazerTaskbar.Features.ControlPanel;

public sealed partial class ControlPanelWindow : Window
{
    private ControlPanelPage _page = ControlPanelPage.History;

    public ControlPanelWindow()
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

        SyncLanguage();
        ShowPage(_page);
    }

    /// <summary>Open the window on the given tab.</summary>
    public void OpenPage(ControlPanelPage page) => ShowPage(page);

    public void SyncLanguage()
    {
        // The title is just the project name — it does not advertise which
        // tab is open (the nav pane highlights that).
        TitleText.Text = "Razer Taskbar";
        NavHistory.Content = I18n.Tr("Battery history");
        NavSettings.Content = I18n.Tr("Settings");
        if (ContentFrame.Content is HistoryPage history)
        {
            history.Localize();
        }
        else if (ContentFrame.Content is SettingsPage settings)
        {
            settings.Localize();
        }
        UpdateWindowTitle();
    }

    private void ShowPage(ControlPanelPage page)
    {
        _page = page;
        NavigationViewItem target = page == ControlPanelPage.Settings ? NavSettings : NavHistory;
        if (!ReferenceEquals(Nav.SelectedItem, target))
        {
            Nav.SelectedItem = target; // SelectionChanged does the navigation
        }
        else
        {
            Navigate(target);
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
        UpdateWindowTitle();
    }

    private void Navigate(NavigationViewItem item)
    {
        if (ReferenceEquals(item, NavHistory))
        {
            if (ContentFrame.Content is not HistoryPage)
            {
                ContentFrame.Navigate(typeof(HistoryPage));
            }
        }
        else
        {
            if (ContentFrame.Content is not SettingsPage)
            {
                ContentFrame.Navigate(typeof(SettingsPage));
            }
        }
    }

    /// <summary>Alt-tab / taskbar caption follows the open tab: the tray and
    /// widget menus open this one window as either "Battery history…" or
    /// "Settings…", and the caption is invisible in-app anyway
    /// (ExtendsContentIntoTitleBar) — the zh map carries the composed
    /// strings.</summary>
    private void UpdateWindowTitle()
        => Title = ContentFrame.Content is SettingsPage
            ? I18n.Tr("Settings — Razer Taskbar")
            : I18n.Tr("Battery history — Razer Taskbar");
}
