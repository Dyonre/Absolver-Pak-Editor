using System.Windows;
using AbsolverModTool.Core;
using Microsoft.Win32;

namespace AbsolverModTool.Gui;

public partial class SettingsDialog : Window
{
    bool _updatingModeToggles;

    public bool SimpleMode { get; private set; }
    public bool ShowVerify { get; private set; }
    public bool ShowPendingEdits { get; private set; }
    public bool ShowResolvedColumn { get; private set; }
    public bool ShowDebugLog { get; private set; }

    public SettingsDialog(bool simpleMode, bool showVerify, bool showPendingEdits, bool showResolvedColumn, bool showDebugLog)
    {
        InitializeComponent();
        UnrealPakBox.Text = Config.UnrealPakPath;
        GamePaksBox.Text = Config.GamePaksDir;
        WorkDirBox.Text = Config.WorkDir;

        SimpleModeCheck.IsChecked = simpleMode;
        ShowVerifyCheck.IsChecked = showVerify;
        ShowPendingEditsCheck.IsChecked = showPendingEdits;
        ShowResolvedColumnCheck.IsChecked = showResolvedColumn;
        ShowDebugLogCheck.IsChecked = showDebugLog;
    }

    void BrowseUnrealPak_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "UnrealPak.exe|UnrealPak.exe|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true) UnrealPakBox.Text = dialog.FileName;
    }

    void BrowseGamePaks_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the game's Paks folder" };
        if (dialog.ShowDialog() == true) GamePaksBox.Text = dialog.FolderName;
    }

    void BrowseWorkDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a working folder for recipes/paks" };
        if (dialog.ShowDialog() == true) WorkDirBox.Text = dialog.FolderName;
    }

    // Simplified Mode and the Verify panel are mutually exclusive, same as before this round -
    // enabling one is treated as asking to disable the other. _updatingModeToggles guards
    // against the programmatic IsChecked change below re-triggering the same logic.

    void SimpleModeCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingModeToggles) return;
        _updatingModeToggles = true;
        ShowVerifyCheck.IsChecked = false;
        _updatingModeToggles = false;
    }

    void ShowVerifyCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingModeToggles) return;
        _updatingModeToggles = true;
        SimpleModeCheck.IsChecked = false;
        _updatingModeToggles = false;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        Config.UnrealPakPath = UnrealPakBox.Text.Trim();
        Config.GamePaksDir = GamePaksBox.Text.Trim();
        Config.WorkDir = Config.FromApp(string.IsNullOrWhiteSpace(WorkDirBox.Text) ? "work" : WorkDirBox.Text.Trim());
        SettingsIO.Save(SettingsIO.CurrentAsSettings());

        SimpleMode = SimpleModeCheck.IsChecked == true;
        ShowVerify = ShowVerifyCheck.IsChecked == true;
        ShowPendingEdits = ShowPendingEditsCheck.IsChecked == true;
        ShowResolvedColumn = ShowResolvedColumnCheck.IsChecked == true;
        ShowDebugLog = ShowDebugLogCheck.IsChecked == true;

        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
