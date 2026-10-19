using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stringline.App.Services;

namespace Stringline.App.ViewModels;

/// <summary>
/// The main window's panels (#9): which are shown (View ▸ Panels, or a double-click on a splitter), the sizes the
/// splitters were left at, and View ▸ Reset layout. The window applies <see cref="Current"/> to its grid and reports
/// sizes back with <see cref="Update"/>; this class holds no WPF types.
/// </summary>
internal sealed partial class LayoutViewModel : ObservableObject
{
    public LayoutViewModel(WindowLayout? saved = null)
    {
        Current = (saved ?? new WindowLayout()).Checked();
        ShowTrainList = Current.ShowTrainList;
        ShowSidePanel = Current.ShowSidePanel;
        ShowTabs = Current.ShowTabs;
    }

    /// <summary>Raised when the whole layout changes at once (Reset layout), for the window to apply it again.</summary>
    public event EventHandler? Reset;

    /// <summary>The layout as last applied or reported, with the panel switches.</summary>
    public WindowLayout Current { get; private set; }

    /// <summary>The train list on the left.</summary>
    [ObservableProperty]
    public partial bool ShowTrainList { get; set; }

    /// <summary>The train detail and tabs on the right.</summary>
    [ObservableProperty]
    public partial bool ShowSidePanel { get; set; }

    /// <summary>The Conflicts, Regulation, Delays and History tabs under the train detail.</summary>
    [ObservableProperty]
    public partial bool ShowTabs { get; set; }

    partial void OnShowTrainListChanged(bool value) => Current = Current with { ShowTrainList = value };

    partial void OnShowSidePanelChanged(bool value) => Current = Current with { ShowSidePanel = value };

    partial void OnShowTabsChanged(bool value) => Current = Current with { ShowTabs = value };

    /// <summary>
    /// The window's sizes, as the splitters left them; the window works out the size a hidden panel would have, so
    /// showing it again brings back its old share. The panel switches stay as they are here.
    /// </summary>
    public void Update(WindowLayout seen)
    {
        ArgumentNullException.ThrowIfNull(seen);
        Current = seen.Checked() with { ShowTrainList = ShowTrainList, ShowSidePanel = ShowSidePanel, ShowTabs = ShowTabs };
    }

    /// <summary>A double-click on the splitter beside the train list.</summary>
    [RelayCommand]
    private void ToggleTrainList() => ShowTrainList = !ShowTrainList;

    /// <summary>A double-click on the splitter beside the side panel.</summary>
    [RelayCommand]
    private void ToggleSidePanel() => ShowSidePanel = !ShowSidePanel;

    /// <summary>A double-click on the splitter between the train detail and the tabs: the tabs collapse (Fred).</summary>
    [RelayCommand]
    private void ToggleTabs() => ShowTabs = !ShowTabs;

    /// <summary>View ▸ Reset layout: every panel shown at its first size. The window stays where it is.</summary>
    [RelayCommand]
    private void ResetLayout()
    {
        Current = new WindowLayout { Window = Current.Window };
        ShowTrainList = ShowSidePanel = ShowTabs = true;
        Reset?.Invoke(this, EventArgs.Empty);
    }
}
