using System.Windows;
using System.Windows.Controls;
using AbsolverViewer.Paks;

namespace AbsolverModTool.Gui;

/// <summary>Shows what is inside a .pak (its moves compared with the base game, and every file) before it is loaded into the viewer.
/// DialogResult = true means "Load into viewer" was pressed.</summary>
public partial class PakInspectDialog : Window
{
    record MoveVm(string Id, string Name, string Status, string Anim, string AnimText);

    readonly PakInfo _info;
    readonly List<MoveVm> _moves;
    public bool OpenForEditing { get; private set; }

    public PakInspectDialog(PakInfo info, bool alreadyLoaded)
    {
        _info = info;
        InitializeComponent();
        TitleText.Text = info.Name;
        SummaryText.Text = $"{info.SizeBytes / 1024.0 / 1024.0:0.0} MB · {info.FileCount} files · {info.AnimationFiles} animation assets · "
                         + $"{info.Rows.Count} rows in its attacks table ({info.Rows.Count(r => r.Status == "new")} new, {info.Rows.Count(r => r.Status == "changed")} changed, {info.Rows.Count(r => r.Status == "same")} same as the base game)\n{info.Path}";
        NoteText.Text = info.Note;
        NoteText.Visibility = string.IsNullOrEmpty(info.Note) ? Visibility.Collapsed : Visibility.Visible;
        _moves = info.Rows.Select(r => new MoveVm(r.Id, r.Name, r.Status, r.Anim.Split('/').LastOrDefault()?.Split('.')[0] ?? "", r.AnimInPak ? "yes" : (r.Anim.Length == 0 ? "" : "no (base game's)"))).ToList();
        LoadButton.IsEnabled = !alreadyLoaded && info.FileCount > 0;
        StateText.Text = alreadyLoaded ? "Already loaded." : info.FileCount == 0 ? "Nothing readable in this pak." : "Not loaded yet - this is only a preview.";
        RefreshMoves(); RefreshFiles();
    }

    void RefreshMoves()
    {
        if (!IsInitialized || MoveList == null) return;
        var mode = (StatusFilter.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var q = MoveSearch.Text.Trim();
        var rows = _moves.Where(m => mode switch
        {
            "New only" => m.Status == "new",
            "Changed only" => m.Status == "changed",
            "Everything" => true,
            _ => m.Status != "same",
        }).Where(m => q.Length == 0 || $"{m.Id} {m.Name} {m.Anim}".Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        MoveList.ItemsSource = rows;
        MoveCount.Text = $"{rows.Count} of {_moves.Count}";
    }

    void RefreshFiles()
    {
        if (!IsInitialized || FileList == null) return;
        var q = FileSearch.Text.Trim();
        var files = _info.Files.Where(f => q.Length == 0 || f.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(5000).ToList();
        FileList.ItemsSource = files;
        FileCount.Text = $"{files.Count}{(files.Count == 5000 ? "+" : "")} of {_info.FileCount}";
    }

    void Filter_Changed(object sender, RoutedEventArgs e) => RefreshMoves();
    void FileSearch_Changed(object sender, TextChangedEventArgs e) => RefreshFiles();
    void Load_Click(object sender, RoutedEventArgs e) { DialogResult = true; }
    void Edit_Click(object sender, RoutedEventArgs e) { OpenForEditing = true; DialogResult = true; }
    void Close_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
}
