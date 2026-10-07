using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AbsolverModTool.Core;
using Microsoft.Win32;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Structs;

namespace AbsolverModTool.Gui;

public class FieldRowVm : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string PropertyType { get; init; }
    public required bool Editable { get; init; }
    public bool HasAttackChoices { get; init; }
    public IEnumerable<RowItem>? AttackChoices { get; init; }

    string? _resolved;
    public string? Resolved
    {
        get => _resolved;
        set { _resolved = value; OnPropertyChanged(); }
    }

    string _value = "";
    public string Value
    {
        get => _value;
        set { _value = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A DataTable row for the row-selector ComboBox: shows the key plus a best-effort
/// friendly label (e.g. "212  —  Plexus Elbow" in Advanced mode, just "Plexus Elbow" in
/// Simplified mode - DisplayText is computed once at creation time for whichever mode is
/// active, rather than trying to make ToString mode-aware after the fact.</summary>
public record RowItem(string Key, string? Label, string DisplayText)
{
    public override string ToString() => DisplayText;
}

public partial class MainWindow : Window
{
    readonly System.Collections.ObjectModel.ObservableCollection<FieldRowVm> _fieldRows = new();
    readonly Recipe _recipe = new();

    string? _rootDir;
    string? _currentAssetPath;
    string? _currentAssetName;
    UAsset? _currentAsset;
    DataTableExport? _currentTable;
    string? _recipePath;
    Dictionary<string, string> _attackNames = new();
    bool _simpleMode;
    bool _showVerify = true;
    bool _showPendingEdits = true;
    bool _showResolvedColumn = true;
    bool _showDebugLog;
    string? _editOriginalValue;

    // Every line ever logged, normal or debug - kept so toggling "Show Debug Log" in Settings can
    // re-render the console from scratch (showing/hiding debug lines) without losing history, and
    // so Copy Log can hand over the complete picture regardless of what's currently filtered out
    // on screen. Every line also always goes to AppLog's file regardless of this in-memory buffer
    // or the current filter, so a bug report never depends on the console still being open.
    readonly List<(bool Debug, string Text)> _consoleLines = new();

    public MainWindow()
    {
        SettingsIO.LoadAndApply();
        InitializeComponent();
        FieldGrid.ItemsSource = _fieldRows;
        PendingEditsList.ItemsSource = _recipe.Edits;

        ApplyPanelVisibility();
        ApplyResolvedColumnVisibility();
        AnimViewer.Log += m => Dispatcher.Invoke(() => LogDebug(m));
        AnimViewer.RowEdited += ViewerRowEdited;
        AnimViewer.RetimeOptionChanged += ViewerRetimeOption;

        LogDebug($"log file: {AppLog.LogPath}");
        OpenFolder(Config.DefaultVanillaDir);
    }

    /// <summary>Always-visible, user-facing message (an action taken, a result, a recoverable
    /// error) - what the console showed before debug logging existed.</summary>
    void Log(string message)
    {
        _consoleLines.Add((false, message));
        AppLog.Write(message);
        ConsoleBox.AppendText(message + Environment.NewLine);
    }

    /// <summary>Verbose, implementation-level detail (which handler fired, with what value; an
    /// internal state transition) - the kind of thing that's noise for normal use but exactly
    /// what's needed to diagnose a bug without a back-and-forth to reproduce it again. Hidden from
    /// the console by default; toggle "Show Debug Log" in Settings to see it live. Always written
    /// to the log file either way, so asking for the file is equally as good as turning this on.</summary>
    void LogDebug(string message)
    {
        var line = $"[debug] {message}";
        _consoleLines.Add((true, line));
        AppLog.Write(line);
        if (_showDebugLog) ConsoleBox.AppendText(line + Environment.NewLine);
    }

    void LogBlank() => Log("");

    void RerenderConsole()
    {
        ConsoleBox.Text = string.Join(Environment.NewLine, _consoleLines.Where(l => !l.Debug || _showDebugLog).Select(l => l.Text));
        if (ConsoleBox.Text.Length > 0) ConsoleBox.Text += Environment.NewLine;
        ConsoleBox.ScrollToEnd();
    }

    void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        // Copies EVERYTHING, including debug lines, regardless of the current on-screen filter -
        // this is meant for handing the full picture to whoever's debugging it (which, until this
        // tool has a "day two" user, is generally a session where that log gets pasted back to me).
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _consoleLines.Select(l => l.Text)));
            Log("Copied full console log (including debug lines) to clipboard.");
        }
        catch (Exception ex)
        {
            Log($"ERROR copying log to clipboard: {ex.Message}");
        }
    }

    // ===================================================================== asset tree

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder of extracted assets" };
        if (dialog.ShowDialog() == true) OpenFolder(dialog.FolderName);
    }

    async void ExtractGameFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose an empty folder for the extracted game files", InitialDirectory = Config.AppDir };
        if (dialog.ShowDialog() != true) return;
        var destination = dialog.FolderName;
        if (Path.GetFullPath(destination).TrimEnd('\\', '/') == Path.GetFullPath(Config.GamePaksDir).TrimEnd('\\', '/'))
        {
            MessageBox.Show(this, "The extraction folder cannot be the game's Paks folder.", "Extract Game Files", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this,
                "This extracts the installed base game into the chosen empty folder. It can take several minutes and needs substantial disk space. Installed mods are not included. Continue?",
                "Extract Game Files", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        ExtractGameButton.IsEnabled = false;
        try
        {
            Log($"Extracting base game files to {destination}...");
            await Task.Run(() => GameExtractor.ExtractBaseGame(Config.GamePaksDir, destination, m => Dispatcher.Invoke(() => Log(m))));
            OpenFolder(destination);
            Log("Game files extracted and opened.");
        }
        catch (Exception ex)
        {
            Log($"ERROR extracting game files: {ex.Message}");
            AppLog.WriteException("ExtractGameFiles_Click", ex);
        }
        finally { ExtractGameButton.IsEnabled = true; }
    }

    async void LoadModPak_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose a mod pak to inspect and edit",
            Filter = "Absolver pak (*.pak)|*.pak|All files (*.*)|*.*",
            InitialDirectory = _lastPakDir ?? Config.GamePaksDir,
        };
        if (picker.ShowDialog(this) != true) return;
        _lastPakDir = Path.GetDirectoryName(picker.FileName);

        try
        {
            var source = GetPakSource();
            if (source == null) { Log("Can't inspect the pak: the game Paks folder is unavailable."); return; }
            var info = await Task.Run(() => source.Inspect(picker.FileName));
            var inspect = new PakInspectDialog(info, source.IsLoaded(picker.FileName)) { Owner = this };
            if (inspect.ShowDialog() != true || !inspect.OpenForEditing) return;

            if (_recipe.Edits.Count > 0 && MessageBox.Show(this, "Loading a mod pak starts a new recipe. Discard the current pending edits?",
                    "Load Mod Pak", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _recipe.Edits.Clear();
            _recipePath = null;

            // ponytail: timestamped folder avoids deleting prior imported mods; add cleanup UI only if this becomes clutter.
            var name = Path.GetFileNameWithoutExtension(picker.FileName);
            var destination = Path.Combine(Config.WorkDir, "imports", $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}");
            LoadModPakButton.IsEnabled = false;
            Log($"Extracting {info.Name} into an editable local copy...");
            await Task.Run(() => GameExtractor.ExtractPak(picker.FileName, destination, m => Dispatcher.Invoke(() => Log(m))));
            OpenFolder(destination);
            Log($"Opened editable copy of {info.Name}. Finalize creates a new override pak; the original pak is unchanged.");
        }
        catch (Exception ex)
        {
            Log($"ERROR loading mod pak: {ex.Message}");
            AppLog.WriteException("LoadModPak_Click", ex);
        }
        finally { LoadModPakButton.IsEnabled = true; }
    }

    void OpenFolder(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Log($"Directory not found: {dir}");
            return;
        }

        _rootDir = dir;
        AssetTree.Items.Clear();
        var root = MakeDirNode(dir, dir);
        AssetTree.Items.Add(root);
        root.IsExpanded = true;

        RebuildAttackNames();
        if (_attackNames.Count > 0) Log($"indexed {_attackNames.Count} attack name(s) for display");

        Log($"opened {dir}");
    }

    static TreeViewItem MakeDirNode(string path, string label)
    {
        var item = new TreeViewItem { Header = label, Tag = path };
        item.Items.Add(null); // placeholder so the expander arrow shows before we lazy-load
        item.Expanded += DirNode_Expanded;
        return item;
    }

    static void DirNode_Expanded(object sender, RoutedEventArgs e)
    {
        var item = (TreeViewItem)sender;
        if (item.Items.Count != 1 || item.Items[0] != null) return; // already loaded

        item.Items.Clear();
        var path = (string)item.Tag;
        foreach (var dir in Directory.GetDirectories(path).OrderBy(d => d))
            item.Items.Add(MakeDirNode(dir, Path.GetFileName(dir)));
        foreach (var file in Directory.GetFiles(path, "*.uasset").OrderBy(f => f))
            item.Items.Add(new TreeViewItem { Header = Path.GetFileNameWithoutExtension(file), Tag = file });

        e.Handled = true;
    }

    void AssetTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: string path } && path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            LoadAsset(path);
    }

    // ===================================================================== asset / row / grid

    void LoadAsset(string path)
    {
        try
        {
            // LoadAndReplay, not a bare AssetAccess.Load - loading straight from disk here was a
            // real gap: it made every navigation away from an asset and back (via the tree) throw
            // away any live-preview state for it (a clone, a rename, an added slot) and show
            // pristine vanilla instead, even though the recipe itself still had those edits
            // recorded correctly. Re-selecting "attacks" in the tree after cloning+renaming a row
            // is exactly what triggered this - the cloned row would vanish from the row selector
            // until Finalize actually replayed the recipe for real.
            var assetName = Path.GetFileNameWithoutExtension(path);
            _currentAsset = LoadAndReplay(path, assetName);
            _currentAssetPath = path;
            _currentAssetName = assetName;
            _currentTable = AssetAccess.GetTable(_currentAsset);
            LogDebug($"LoadAsset: {_currentAssetName} exports={_currentAsset.Exports.Count} isDataTable={_currentTable != null}");

            AssetLabel.Text = _currentAssetName;
            CloneRowMenuItem.IsEnabled = _currentTable != null;

            PopulateRowSelector();
            if (_currentTable == null) LoadFields(null);
            // else: PopulateRowSelector's SelectedIndex assignment already fired
            // RowSelector_SelectionChanged, which loads the grid.

            Log($"loaded {_currentAssetName}");
        }
        catch (Exception ex)
        {
            Log($"ERROR loading {path}: {ex.Message}");
            AppLog.WriteException($"LoadAsset({path})", ex);
        }
    }

    // ===================================================================== animation viewer

    static string EnsureDir(string d) { Directory.CreateDirectory(d); return d; }

    bool _animViewerOpened;
    readonly List<string> _extraPakPaths = new();
    string? _lastPakDir;

    /// <summary>"Add pak...": pick any .pak, look through it (moves vs the base game, every file) in a dialog, and only then load it into the viewer.</summary>
    async void AddPak_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick a pak to look through",
            Filter = "Absolver pak (*.pak)|*.pak|All files (*.*)|*.*",
            InitialDirectory = _lastPakDir ?? (Directory.Exists(Config.GamePaksDir) ? Config.GamePaksDir : Environment.CurrentDirectory),
        };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FileName;
        _lastPakDir = Path.GetDirectoryName(path);
        AnimStatus.Text = $"reading {Path.GetFileName(path)}...";
        try
        {
            var info = await Task.Run(() => GetPakSource()?.Inspect(path));
            var src = _pakSource;
            if (info == null || src == null) { AnimStatus.Text = "paks unavailable - can't read that pak"; return; }
            Log($"viewer: inspected {info.Name}: {info.FileCount} files, {info.Rows.Count} attacks rows ({info.Rows.Count(r => r.Status == "new")} new, {info.Rows.Count(r => r.Status == "changed")} changed)");
            var dialog = new PakInspectDialog(info, src.IsLoaded(path)) { Owner = this };
            if (dialog.ShowDialog() != true) { AnimStatus.Text = $"{info.Name} not loaded"; return; }

            AnimStatus.Text = $"loading {info.Name}...";
            var result = await Task.Run(() => src.AddPak(path));
            if (!_extraPakPaths.Contains(path, StringComparer.OrdinalIgnoreCase) && !result.Contains("already")) _extraPakPaths.Add(path);
            Log($"viewer: {result}");
            AnimStatus.Text = $"paks: {string.Join(", ", src.MountedPaks.Concat(_extraPakPaths.Select(Path.GetFileName)!))}";
            AnimViewer.LiveResolver ??= ResolveLive;
            await AnimViewer.EnsureLoadedAsync();
            AnimViewer.Reload();      // the page re-reads the move list, which now includes this pak's moves
        }
        catch (Exception ex)
        {
            Log($"ERROR reading pak {path}: {ex.Message}");
            AppLog.WriteException($"AddPak_Click({path})", ex);
            AnimStatus.Text = $"couldn't read {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    AbsolverViewer.Paks.PakAnimationSource? _pakSource;
    readonly object _pakLock = new();

    /// <summary>Mounts the game Paks folder (base paks + any installed mod paks) on first use. Null if that fails; the viewer then falls back to its exported data.</summary>
    AbsolverViewer.Paks.PakAnimationSource? GetPakSource()
    {
        lock (_pakLock)
        {
            if (_pakSource != null) return _pakSource;
            try
            {
                _pakSource = new AbsolverViewer.Paks.PakAnimationSource(Config.GamePaksDir, null, m => Dispatcher.Invoke(() => LogDebug(m)));
                foreach (var extra in _extraPakPaths.ToList())   // paks added with "Add pak..." survive a reload
                {
                    try { _pakSource.AddPak(extra); }
                    catch (Exception ex) { Dispatcher.Invoke(() => Log($"viewer: couldn't re-load {extra}: {ex.Message}")); }
                }
                Dispatcher.Invoke(() => AnimStatus.Text = $"paks: {string.Join(", ", _pakSource.MountedPaks.Concat(_extraPakPaths.Select(Path.GetFileName)!))}");
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => { Log($"viewer: couldn't mount {Config.GamePaksDir}: {ex.Message} - using exported viewer data"); AnimStatus.Text = "paks unavailable - exported data"; });
                AppLog.WriteException("GetPakSource", ex);
            }
            return _pakSource;
        }
    }

    byte[]? ResolveLive(string kind, string? arg)
    {
        var src = GetPakSource();
        if (src == null) return null;
        string? json = kind switch
        {
            "moves" => src.MovesJson(),
            "skeleton" => src.SkeletonJson(),
            "mesh" => src.MeshJson(),
            "anim" when arg != null => src.AnimationJson(arg),
            _ => null,
        };
        return json == null ? null : System.Text.Encoding.UTF8.GetBytes(json);
    }

    /// <summary>Loads the WebView2 viewer the first time its tab is shown (init is slow, and most sessions never need it).</summary>
    async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != MainTabs || !ReferenceEquals(MainTabs.SelectedItem, AnimTab)) return;
        _animViewerOpened = true;
        AnimViewer.LiveResolver ??= ResolveLive;
        await AnimViewer.EnsureLoadedAsync();
        PreviewCurrentRowInViewer();
    }

    /// <summary>Drops the mounted paks and reloads the viewer, so paks installed/changed since (deploy, Absolver Plus, Sifu mod) show up.</summary>
    void ReloadPaks_Click(object sender, RoutedEventArgs e)
    {
        lock (_pakLock) { _pakSource?.Dispose(); _pakSource = null; }
        AnimStatus.Text = "reloading...";
        AnimViewer.Reload();
        Log("viewer: reloading from paks");
    }

    /// <summary>Whole current attacks row as raw property name -> plain value, read from the in-memory asset (so it includes
    /// edits made in this session and is not limited to what Simplified Mode shows in the grid).</summary>
    Dictionary<string, string>? BuildRowDict(string rowKey)
    {
        if (_currentAsset == null || _currentTable == null) return null;
        var d = new Dictionary<string, string>();
        foreach (var (path, p) in AssetAccess.FlattenForEditing(AssetAccess.GetEditableFields(_currentAsset, rowKey)))
        {
            d[path] = p switch
            {
                UAssetAPI.PropertyTypes.Objects.TextPropertyData t => t.CultureInvariantString?.ToString() ?? "",
                UAssetAPI.PropertyTypes.Objects.SoftObjectPropertyData so => so.Value.AssetPath.AssetName?.ToString() ?? "None",
                _ => PropertyFormatting.Format(p, _currentAsset),
            };
        }
        return d;
    }

    /// <summary>Pushes the selected `attacks` row into the viewer: re-renders in place (header, phase bands, animation if m_Anim changed), no reload.</summary>
    void PushRowToViewer(bool alsoSelect)
    {
        if (_suppressViewerPush || !_animViewerOpened || _currentAssetName != "attacks" || RowSelector.SelectedItem is not RowItem r) return;
        var row = BuildRowDict(r.Key);
        if (row == null) return;
        LogDebug($"viewer: pushing attacks row {r.Key} ({row.Count} fields, select={alsoSelect})");
        _ = AnimViewer.SetRowAsync(r.Key, row);
        if (alsoSelect) _ = AnimViewer.PreviewMoveAsync(r.Key);
    }

    void PreviewCurrentRowInViewer() => PushRowToViewer(alsoSelect: true);

    void RowSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LoadFields((RowSelector.SelectedItem as RowItem)?.Key);

    /// <summary>Rebuilds the row list from _currentTable, preserving the current selection where
    /// possible. Called on asset load and whenever Simplified Mode toggles, since the row labels
    /// (key+name vs. name-only) depend on the mode.</summary>
    void PopulateRowSelector()
    {
        if (_currentAsset == null) return;
        var previousKey = (RowSelector.SelectedItem as RowItem)?.Key;

        RowSelector.SelectionChanged -= RowSelector_SelectionChanged;
        RowSelector.Items.Clear();
        if (_currentTable != null)
        {
            foreach (var k in AssetAccess.GetRowKeys(_currentTable))
            {
                var row = _currentTable[k] as StructPropertyData;
                var label = row != null ? AssetAccess.GuessRowLabel(row, _currentAsset) : null;
                RowSelector.Items.Add(MakeRowItem(k, label));
            }
            RowSelector.IsEnabled = true;
        }
        else
        {
            RowSelector.IsEnabled = false;
        }
        RowSelector.SelectionChanged += RowSelector_SelectionChanged;

        var restored = previousKey != null ? RowSelector.Items.OfType<RowItem>().FirstOrDefault(r => r.Key == previousKey) : null;
        if (restored != null) RowSelector.SelectedItem = restored;
        else if (RowSelector.Items.Count > 0) RowSelector.SelectedIndex = 0;
        LogDebug($"PopulateRowSelector: {RowSelector.Items.Count} row(s), previousKey='{previousKey}', restored={restored != null}");
    }

    RowItem MakeRowItem(string key, string? label)
    {
        var display = _simpleMode && label != null ? label : label != null ? $"{key}  —  {label}" : key;
        return new RowItem(key, label, display);
    }

    void LoadFields(string? rowKey)
    {
        if (_currentAsset == null) return;
        try
        {
            var fields = AssetAccess.GetEditableFields(_currentAsset, rowKey);
            _fieldRows.Clear();
            foreach (var (path, p) in AssetAccess.FlattenForEditing(fields))
            {
                if (_simpleMode && !SimpleModeProperties.IsSimple(_currentAssetName, path)) continue;

                // TextPropertyData's own ToString() (what PropertyFormatting.Format falls back to,
                // and what dump-row/diff still want for full diagnostic context) is the whole
                // "Base, attacks, ATTACK_REALNAME_212, Plexus Elbow" debug tuple, not just the
                // display name - editing that whole string and writing it straight back as the new
                // CultureInvariantString is what corrupted a rename into a doubled-up mess. The
                // editable cell only ever shows/writes the actual display text.
                var value = p is UAssetAPI.PropertyTypes.Objects.TextPropertyData textProp
                    ? textProp.CultureInvariantString.ToString() ?? ""
                    : PropertyFormatting.Format(p, _currentAsset);

                // Only offer the attack-picker dropdown for name references on a plain object
                // (a combat deck) - not for a DataTable's own m_Name-style fields, where a
                // "pick from the attacks list" editor wouldn't make sense.
                var hasAttackChoices = _currentTable == null && p is UAssetAPI.PropertyTypes.Objects.NamePropertyData && _attackNames.Count > 0;

                // Resolved is scoped to the exact same condition as the attack-picker dropdown -
                // otherwise a field that just happens to hold a string coincidentally matching an
                // attack row key (an EmoteShop row-name-like field, an attacks-table float such as
                // m_fMovementForwardLength) shows a bogus "resolved" attack name next to it.
                _fieldRows.Add(new FieldRowVm
                {
                    Name = path,
                    PropertyType = p.PropertyType.ToString(),
                    Editable = RecipeEngine.IsSupported(p),
                    Value = value,
                    Resolved = hasAttackChoices && _attackNames.TryGetValue(value, out var friendly) ? friendly : null,
                    HasAttackChoices = hasAttackChoices,
                    AttackChoices = hasAttackChoices
                        ? _attackNames.OrderBy(kv => kv.Key).Select(kv => new RowItem(kv.Key, kv.Value, $"{kv.Key}  —  {kv.Value}")).ToList()
                        : null,
                });
            }
            RefreshIconPreview(fields);
            AddSlotMenuItem.IsEnabled = RecipeEngine.GetArrayProperties(fields).Count > 0;
            RemoveSlotMenuItem.IsEnabled = _currentAssetName != null && _recipe.Edits.Any(ed =>
                ed.Op == "add-array-element" && ed.Asset == _currentAssetName && (ed.Row ?? "") == (rowKey ?? ""));
            RemoveRowMenuItem.IsEnabled = _currentTable != null && _currentAssetName != null && _recipe.Edits.Any(ed =>
                ed.Op == "clone-row" && ed.Asset == _currentAssetName);
            LogDebug($"LoadFields({rowKey}): {_fieldRows.Count} field row(s) shown (simpleMode={_simpleMode})");
            PreviewCurrentRowInViewer();
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            AppLog.WriteException($"LoadFields({_currentAssetName}, {rowKey})", ex);
        }
    }

    /// <summary>Shows the first icon/picto texture reference found on the current row (any
    /// <c>SoftObjectPropertyData</c> field, e.g. <c>m_AttackPicto</c>, <c>m_InventoryIcon</c> - not
    /// hardcoded to specific field names, so this works across attack rows, equipment DataAssets,
    /// etc. without a name whitelist) - decoded via <see cref="TextureDecoder"/>, which only
    /// handles uncompressed inline textures (every real icon in this game, per
    /// docs/asset-format-notes.md), so a field pointing at a real 3D-model texture just silently
    /// finds nothing decodable and moves on to the next candidate, rather than erroring.</summary>
    void RefreshIconPreview(List<UAssetAPI.PropertyTypes.Objects.PropertyData> fields)
    {
        IconPreviewImage.Source = null;
        IconPreviewText.Text = "";
        IconPreviewBorder.Visibility = Visibility.Collapsed;
        if (_rootDir == null || _currentAsset == null) return;

        foreach (var (path, p) in AssetAccess.FlattenForEditing(fields))
        {
            if (p is not UAssetAPI.PropertyTypes.Objects.SoftObjectPropertyData sop) continue;
            try
            {
                var decoded = AssetAccess.TryDecodeIcon(_rootDir, sop);
                if (decoded == null) { LogDebug($"RefreshIconPreview: '{path}' has no decodable icon (unresolved, missing, or unsupported format)"); continue; }

                IconPreviewImage.Source = ToBitmapSource(decoded);
                IconPreviewText.Text = $"{path}  ({decoded.Width}x{decoded.Height})";
                IconPreviewBorder.Visibility = Visibility.Visible;
                return; // first decodable icon on the row wins - a row rarely has more than one
            }
            catch (Exception ex)
            {
                // A field that looks like a texture reference but isn't (or the file it points at
                // is missing/corrupt) shouldn't take down the whole field grid over a preview -
                // log it at debug level and try the next candidate field instead.
                LogDebug($"RefreshIconPreview: '{path}' didn't decode: {ex.Message}");
            }
        }
    }

    static System.Windows.Media.Imaging.BitmapSource ToBitmapSource(TextureDecoder.DecodedTexture t)
    {
        // TextureDecoder returns R,G,B,A byte order; WPF's Bgra32 format wants B,G,R,A per pixel.
        var bgra = new byte[t.Rgba.Length];
        for (int i = 0; i < t.Rgba.Length; i += 4)
        {
            bgra[i] = t.Rgba[i + 2];
            bgra[i + 1] = t.Rgba[i + 1];
            bgra[i + 2] = t.Rgba[i];
            bgra[i + 3] = t.Rgba[i + 3];
        }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
            t.Width, t.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bgra, t.Width * 4);
        bmp.Freeze(); // safe to hand to the UI thread's Image.Source without further synchronization
        return bmp;
    }

    void FieldGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not FieldRowVm row) return;
        if (!row.Editable)
        {
            Log($"'{row.Name}' ({row.PropertyType}) can't be edited yet - unsupported property type for set-prop.");
            e.Cancel = true;
            return;
        }
        // Captured so CellEditEnding can tell a real edit from just opening and leaving a cell
        // (a click that enters edit mode without changing the text) - otherwise every cell you
        // merely looked at gets recorded as a pending edit.
        _editOriginalValue = row.Value;
        LogDebug($"BeginningEdit: {row.Name} original='{_editOriginalValue}'");
    }

    void AttackPickerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The DataGrid's own CellEditEnding is unreliable for a ComboBox hosted inside a
        // DataGridTemplateColumn: the dropdown is a separate Popup, and clicking an item inside it
        // can make the grid think focus left the cell before ever recognizing a "commit" - a
        // selection was visually sticking but never actually getting recorded into the recipe. So
        // this bypasses the grid's edit lifecycle entirely and records the moment a real selection
        // happens.
        //
        // ROOT CAUSE found via diagnostic logging: reading row.Value here (the ViewModel property
        // the TwoWay binding is supposed to keep in sync) was stale - combo.SelectedValue had
        // already updated to the newly-picked item, but the binding hadn't flushed it into
        // FieldRowVm.Value yet by the time this handler ran. So read SelectedValue directly off
        // the ComboBox and push it into the VM ourselves, instead of trusting the binding to have
        // already done that by now.
        if (sender is not ComboBox combo || combo.DataContext is not FieldRowVm row) return;
        if (combo.SelectedValue is not string newValue) return;
        LogDebug($"AttackPickerCombo.SelectionChanged: {row.Name} SelectedValue='{newValue}' (row.Value was '{row.Value}')");
        row.Value = newValue;
        CommitFieldEdit(row);
    }

    void AttackPickerCombo_DropDownClosed(object sender, EventArgs e)
    {
        // Best-effort: also ask the grid to end cell-edit mode cleanly (returns the cell to its
        // read-only template). Harmless no-op if the grid had already dropped out of edit mode on
        // its own - the actual recording happens in AttackPickerCombo_SelectionChanged above,
        // regardless of whether this succeeds.
        FieldGrid.CommitEdit(DataGridEditingUnit.Cell, true);
    }

    void FieldGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.Item is FieldRowVm debugRow)
            LogDebug($"CellEditEnding: {debugRow.Name} EditAction={e.EditAction} row.Value='{debugRow.Value}'");
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not FieldRowVm row) return;

        // The Value column is a DataGridTemplateColumn (to support the attack-picker ComboBox
        // editor for combat decks), so e.EditingElement is the template's root Grid, not the
        // TextBox/ComboBox inside it - reading row.Value directly instead, which both editors
        // already keep current via their own TwoWay bindings (TextBox with
        // UpdateSourceTrigger=PropertyChanged, ComboBox.SelectedValue updates immediately on
        // selection) well before this handler ever runs. For the ComboBox editor this is usually
        // a no-op by the time it runs (AttackPickerCombo_SelectionChanged already committed it),
        // which CommitFieldEdit's own no-op check below handles safely either way.
        CommitFieldEdit(row);
    }

    /// <summary>Records row.Value as a pending set-prop edit (unless it's unchanged from the last
    /// baseline), mutates the live in-memory asset to match, and refreshes anything derived from
    /// it. Shared by the plain TextBox editor's normal CellEditEnding commit and the attack-picker
    /// ComboBox's own SelectionChanged-driven commit (see the notes on each above for why the
    /// latter can't just rely on CellEditEnding).</summary>
    void CommitFieldEdit(FieldRowVm row)
    {
        if (_currentAssetName == null)
        {
            LogDebug("CommitFieldEdit: _currentAssetName is null, bailing");
            return;
        }

        var newValue = row.Value;
        LogDebug($"CommitFieldEdit: {row.Name} newValue='{newValue}' vs baseline='{_editOriginalValue}'");
        if (newValue == _editOriginalValue) return; // nothing actually changed - don't record a no-op edit
        _editOriginalValue = newValue; // re-baseline, in case this fires again for the same cell (e.g. a second pick)

        var rowKey = _currentTable != null ? (RowSelector.SelectedItem as RowItem)?.Key ?? "" : "";
        ApplyFieldEdit(rowKey, row.Name, newValue, row, fromViewer: false, retime: _retimeNotifies);
    }

    /// <summary>Records one set-prop edit (recipe + live in-memory asset). Shared by the property grid and the animation viewer's edit panel.</summary>
    void ApplyFieldEdit(string rowKey, string fieldName, string newValue, FieldRowVm? row, bool fromViewer, bool retime)
    {
        if (_currentAssetName == null || _currentAsset == null) return;
        RecipeBuilder.AddSetProp(_recipe, _currentAssetName, rowKey, fieldName, newValue);
        Log($"recorded: set-prop {_currentAssetName} [{rowKey}] {fieldName} = {newValue}{(fromViewer ? "  (from animation viewer)" : "")}");

        try
        {
            var fields = AssetAccess.GetEditableFields(_currentAsset, _currentTable != null ? rowKey : null);
            var prop = AssetAccess.ResolvePath(fields, fieldName);
            if (prop != null) RecipeEngine.SetValue(prop, newValue, _currentAsset);
            else LogDebug($"ApplyFieldEdit: ResolvePath returned null for '{fieldName}' - live preview skipped, but the recipe edit above was still recorded");
        }
        catch (Exception ex)
        {
            Log($"(couldn't live-preview this edit: {ex.Message})");
            AppLog.WriteException($"ApplyFieldEdit live-preview for {_currentAssetName}[{rowKey}].{fieldName}", ex);
        }

        // A timeline edit on an attacks row: have packing move the animation's baked notifies with it (docs/attack-row-and-notify-map.md).
        if (_currentAssetName == "attacks" && retime && AnimRetime.IsTimelineField(fieldName) && RecipeBuilder.AddRetimeAnim(_recipe, "attacks", rowKey))
            Log($"recorded: retime-anim attacks [{rowKey}] - the animation's notifies will be moved onto this row's timeline when packing");

        if (row != null)
            row.Resolved = row.HasAttackChoices && _attackNames.TryGetValue(newValue, out var friendly) ? friendly : null;

        if (_currentAssetName == "attacks")
        {
            // A rename touches the row selector label and every attack-picker; other fields don't, and rebuilding the
            // selector would re-select the row (and restart the viewer's playback) for an edit the viewer already shows.
            if (!fromViewer || fieldName == "m_RealAttackName")
            {
                RebuildAttackNames();
                PopulateRowSelector();
            }
            if (!fromViewer) PushRowToViewer(alsoSelect: false);
        }
    }

    /// <summary>An edit made in the animation viewer's panel: mirror it into the grid (if that row is shown) and the recipe.</summary>
    bool _suppressViewerPush;

    bool _retimeNotifies = true;

    /// <summary>The viewer's "Retime with the row" checkbox: remembered for later edits, and applied to the move it was toggled on.</summary>
    void ViewerRetimeOption(bool on, string id) => Dispatcher.Invoke(() =>
    {
        _retimeNotifies = on;
        if (string.IsNullOrEmpty(id)) return;
        if (!on) { if (RecipeBuilder.RemoveRetimeAnim(_recipe, "attacks", id)) Log($"removed retime-anim for attacks [{id}]"); return; }
        bool edited = _recipe.Edits.Any(e => e.Op == "set-prop" && e.Asset == "attacks" && e.Row == id && AnimRetime.IsTimelineField(e.Property));
        if (edited && RecipeBuilder.AddRetimeAnim(_recipe, "attacks", id)) Log($"recorded: retime-anim attacks [{id}]");
    });

    void ViewerRowEdited(string id, string field, string value, bool retime) => Dispatcher.Invoke(() =>
    {
        if (_currentAssetName != "attacks" || _currentTable == null)
        {
            // The viewer edits the attacks table; open it in the editor (without letting that re-select a row in the viewer).
            var file = AssetAccess.FindAssetFile(_rootDir ?? Config.DefaultVanillaDir, "attacks");
            if (file == null) { Log("viewer edit ignored: attacks table not found under the open folder"); return; }
            _suppressViewerPush = true;
            try { LoadAsset(file); }
            finally { _suppressViewerPush = false; }
            if (_currentAssetName != "attacks" || _currentTable == null) return;
            Log("opened the attacks table for an edit made in the animation viewer");
        }
        FieldRowVm? gridRow = null;
        var item = RowSelector.Items.OfType<RowItem>().FirstOrDefault(r => r.Key == id);
        if (item != null && !ReferenceEquals(RowSelector.SelectedItem, item))
        {
            _suppressViewerPush = true;
            try { RowSelector.SelectedItem = item; }
            finally { _suppressViewerPush = false; }
        }
        if (RowSelector.SelectedItem is RowItem sel && sel.Key == id)
        {
            gridRow = _fieldRows.FirstOrDefault(r => r.Name == field);
            if (gridRow != null) gridRow.Value = value;
        }
        _retimeNotifies = retime;
        ApplyFieldEdit(id, field, value, gridRow, fromViewer: true, retime: retime);
    });

    // ===================================================================== clone row

    void CloneRow_Click(object sender, RoutedEventArgs e)
    {
        if (_currentAsset == null || _currentTable == null || _currentAssetName == null) return;

        CloneRowDialog dialog;
        try
        {
            var rows = RowSelector.Items.OfType<RowItem>().ToList();
            dialog = new CloneRowDialog(_currentAssetName, rows) { Owner = this };
        }
        catch (Exception ex)
        {
            Log($"ERROR opening Clone Row dialog: {ex}");
            AppLog.WriteException("CloneRow_Click (opening dialog)", ex);
            return;
        }
        bool? result;
        try
        {
            result = dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            Log($"ERROR showing Clone Row dialog: {ex}");
            AppLog.WriteException("CloneRow_Click (showing dialog)", ex);
            return;
        }
        if (result != true) return;

        try
        {
            // Mutates the SAME in-memory _currentAsset/_currentTable (not a throwaway copy) so
            // the new row is immediately visible/editable in this session - selecting it below
            // would otherwise fail, since the view has nothing else to load it from. The recipe
            // (not this in-memory state) is still what actually gets applied, fresh from vanilla,
            // at Create Mod time - this is purely a live preview. No patch logic lives here: this
            // calls the exact same RecipeEngine.ApplyCloneRow the CLI uses.
            var probeEdit = new Edit { Op = "clone-row", Asset = _currentAssetName, SourceRow = dialog.SourceRowKey, NewRow = dialog.NewRowKey };
            RecipeEngine.ApplyCloneRow(_currentAsset, _currentTable, probeEdit);

            var newStructRow = (StructPropertyData)_currentTable[dialog.NewRowKey]!;
            var warnings = AbsolverValidation.CheckNewRow(_currentAssetName, newStructRow);
            var label = AssetAccess.GuessRowLabel(newStructRow, _currentAsset);

            RecipeBuilder.AddCloneRow(_recipe, _currentAssetName, dialog.SourceRowKey, dialog.NewRowKey);
            Log($"recorded: clone-row {_currentAssetName} [{dialog.SourceRowKey}] -> [{dialog.NewRowKey}]");
            foreach (var w in warnings) Log($"WARNING: {w}");

            // A newly-cloned attack needs to show up in every other asset's attack-picker
            // dropdown right away, not just after a reload.
            if (_currentAssetName == "attacks") RebuildAttackNames();

            var item = MakeRowItem(dialog.NewRowKey, label);
            RowSelector.Items.Add(item);
            RowSelector.SelectedItem = item; // jump straight to it so it's immediately visible
        }
        catch (Exception ex)
        {
            Log($"ERROR cloning row: {ex.Message}");
            AppLog.WriteException($"CloneRow_Click applying to {_currentAssetName}", ex);
        }
    }

    void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (_currentAsset == null || _currentTable == null || _currentAssetName == null) return;

        var candidateKeys = _recipe.Edits
            .Where(ed => ed.Op == "clone-row" && ed.Asset == _currentAssetName)
            .Select(ed => ed.NewRow!)
            .Distinct()
            .ToList();

        if (candidateKeys.Count == 0)
        {
            Log("No rows were added (via Clone Row) in this recipe for the current asset - nothing to remove.");
            return;
        }

        var rowItems = candidateKeys.Select(key =>
        {
            var row = _currentTable[key] as StructPropertyData;
            var label = row != null ? AssetAccess.GuessRowLabel(row, _currentAsset) : null;
            return MakeRowItem(key, label);
        }).ToList();

        var dialog = new RemoveRowDialog(_currentAssetName, rowItems) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedRowKey == null) return;

        var rowKey = dialog.SelectedRowKey;
        var cloneEdit = _recipe.Edits.First(ed => ed.Op == "clone-row" && ed.Asset == _currentAssetName && ed.NewRow == rowKey);
        RemoveEditAndDependents(cloneEdit);
    }

    // ===================================================================== add array slot

    void AddSlot_Click(object sender, RoutedEventArgs e)
    {
        if (_currentAsset == null || _currentAssetName == null) return;

        var rowKey = (RowSelector.SelectedItem as RowItem)?.Key;
        List<(string Name, int Count)> arrays;
        try
        {
            arrays = RecipeEngine.GetArrayProperties(AssetAccess.GetEditableFields(_currentAsset, rowKey));
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            AppLog.WriteException($"AddSlot_Click getting array properties for {_currentAssetName}[{rowKey}]", ex);
            return;
        }
        if (arrays.Count == 0)
        {
            Log($"{_currentAssetName} has no growable array properties to append to.");
            return;
        }

        var dialog = new AddArrayElementDialog(_currentAssetName, arrays) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            // Same pattern as Clone Row: mutate the live in-memory asset so the new element is
            // immediately visible, while the recipe (applied fresh from vanilla at Create Mod
            // time) stays the actual source of truth. Calls the same Core method the CLI uses.
            var edit = new Edit { Op = "add-array-element", Asset = _currentAssetName, Row = rowKey, Property = dialog.ArrayProperty, Value = dialog.Value };
            RecipeEngine.ApplyAddArrayElement(_currentAsset, edit);

            RecipeBuilder.AddArrayElement(_recipe, _currentAssetName, rowKey ?? "", dialog.ArrayProperty, dialog.Value);
            Log($"recorded: add-array-element {_currentAssetName} [{rowKey}] {dialog.ArrayProperty} += {dialog.Value}");

            LoadFields(rowKey); // refresh the grid so the new slot shows up right away
        }
        catch (Exception ex)
        {
            Log($"ERROR adding array element: {ex.Message}");
            AppLog.WriteException($"AddSlot_Click applying to {_currentAssetName}[{rowKey}]", ex);
        }
    }

    void RemoveSlot_Click(object sender, RoutedEventArgs e)
    {
        if (_currentAsset == null || _currentAssetName == null) return;
        var rowKey = (RowSelector.SelectedItem as RowItem)?.Key;

        // Only arrays this recipe actually added to, for the current asset/row - this can never
        // remove an original vanilla element, only undo an Add Slot from this session.
        var candidates = _recipe.Edits
            .Where(ed => ed.Op == "add-array-element" && ed.Asset == _currentAssetName && (ed.Row ?? "") == (rowKey ?? ""))
            .GroupBy(ed => ed.Property!)
            .Select(g => (PropertyName: g.Key, AddedCount: g.Count()))
            .ToList();

        if (candidates.Count == 0)
        {
            Log("No array slots were added in this recipe for the current row - nothing to remove.");
            return;
        }

        var dialog = new RemoveSlotDialog(_currentAssetName, candidates) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedProperty == null) return;

        try
        {
            // Undo the most recently added edit for that property (LIFO - Add Slot only ever
            // appends, so the last one added is always the last element of the array).
            var lastEdit = _recipe.Edits.LastOrDefault(ed =>
                ed.Op == "add-array-element" && ed.Asset == _currentAssetName &&
                (ed.Row ?? "") == (rowKey ?? "") && ed.Property == dialog.SelectedProperty);
            if (lastEdit == null) return;

            RemoveEditAndDependents(lastEdit);
        }
        catch (Exception ex)
        {
            Log($"ERROR removing slot: {ex.Message}");
            AppLog.WriteException($"RemoveSlot_Click for {_currentAssetName}[{rowKey}]", ex);
        }
    }

    // ===================================================================== recipe save/load

    void NewRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (_recipe.Edits.Count > 0)
        {
            var confirm = MessageBox.Show(this, $"Discard {_recipe.Edits.Count} pending edit(s) and start a new recipe?",
                "New Recipe", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }
        _recipe.Edits.Clear();
        _recipePath = null;
        Log("started a new recipe");
        RefreshAllAfterRecipeReplaced();
    }

    void PendingEditsList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete) RemoveSelectedPendingEdit();
    }

    void RemovePendingEdit_Click(object sender, RoutedEventArgs e) => RemoveSelectedPendingEdit();

    void RemoveSelectedPendingEdit()
    {
        if (PendingEditsList.SelectedItem is not Edit edit) return;
        RemoveEditAndDependents(edit);
    }

    /// <summary>Removes one pending edit - and, if it's a clone-row, every other edit in the
    /// recipe that targets the row it created (a rename, an added array slot). Those would
    /// otherwise dangle against a row that no longer exists once the recipe replays from vanilla:
    /// removing just the clone-row edit by itself (e.g. via the Pending Edits panel, rather than
    /// the dedicated Remove Row dialog) used to leave them behind, and one dangling edit aborted
    /// every replay of that asset from then on - breaking the attack-name lookup and live preview
    /// for edits that had nothing to do with the row that was removed.</summary>
    void RemoveEditAndDependents(Edit edit)
    {
        _recipe.Edits.Remove(edit);
        Log($"removed pending edit: {edit}");

        if (edit.Op == "clone-row" && edit.NewRow != null)
        {
            var dependents = _recipe.Edits.Where(ed => ed.Asset == edit.Asset && ed.Op != "clone-row" && ed.Row == edit.NewRow).ToList();
            foreach (var dep in dependents) _recipe.Edits.Remove(dep);
            if (dependents.Count > 0)
                Log($"also removed {dependents.Count} pending edit(s) that targeted the now-gone row '{edit.NewRow}'");
        }

        RefreshAfterRecipeChange(edit.Asset);
    }

    void SaveRecipe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Recipe JSON (*.json)|*.json", InitialDirectory = EnsureDir(Config.RecipesDir) };
        if (_recipePath != null) dialog.FileName = _recipePath;
        if (dialog.ShowDialog() != true) return;

        _recipePath = dialog.FileName;
        RecipeIO.Save(_recipePath, _recipe);
        Log($"saved recipe -> {_recipePath} ({_recipe.Edits.Count} edit(s))");
    }

    void LoadRecipe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Recipe JSON (*.json)|*.json", InitialDirectory = EnsureDir(Config.RecipesDir) };
        if (dialog.ShowDialog() != true) return;

        var loaded = RecipeIO.Load(dialog.FileName);
        _recipe.Edits.Clear();
        foreach (var edit in loaded.Edits) _recipe.Edits.Add(edit);
        _recipePath = dialog.FileName;
        Log($"loaded recipe <- {_recipePath} ({_recipe.Edits.Count} edit(s))");
        RefreshAllAfterRecipeReplaced();
    }

    // ===================================================================== live-preview refresh helpers
    //
    // Both the currently-open asset's in-memory state and the attack-name lookup (used by every
    // combat deck's dropdown and the Resolved column) are derived from the recipe, not the other
    // way around - whenever the recipe changes out from under them (an edit removed, a whole
    // recipe swapped), reload fresh from vanilla and replay what's left, the same mechanism
    // Finalize uses, just scoped to one asset for an instant in-GUI refresh.

    UAsset LoadAndReplay(string assetPath, string assetName)
    {
        var asset = AssetAccess.Load(assetPath);
        var table = AssetAccess.GetTable(asset);
        // Each edit is replayed independently - a bad/dangling one (e.g. a leftover rename that
        // still targets a row whose clone-row edit is gone) is skipped and logged, not allowed to
        // abort every other, unrelated edit queued after it in the same asset.
        RecipeEngine.ReplayEdits(asset, table, assetName, _recipe.Edits,
            (edit, ex) => Log($"WARNING: couldn't replay '{edit}' for {assetName}: {ex.Message}"));
        return asset;
    }

    void ReloadCurrentAssetFromRecipe()
    {
        if (_currentAssetPath == null || _currentAssetName == null) return;
        _currentAsset = LoadAndReplay(_currentAssetPath, _currentAssetName);
        _currentTable = AssetAccess.GetTable(_currentAsset);
        LogDebug($"ReloadCurrentAssetFromRecipe: {_currentAssetName} reloaded fresh + replayed");
    }

    void RebuildAttackNames()
    {
        if (_rootDir == null) return;
        try
        {
            var attacksPath = AssetAccess.FindAssetFile(_rootDir, "attacks");
            _attackNames = attacksPath != null ? AssetAccess.BuildAttackNameLookup(LoadAndReplay(attacksPath, "attacks")) : new();
            LogDebug($"RebuildAttackNames: {_attackNames.Count} name(s) indexed");
        }
        catch (Exception ex)
        {
            _attackNames = new();
            Log($"(couldn't rebuild attack names: {ex.Message})");
            AppLog.WriteException("RebuildAttackNames", ex);
        }
    }

    /// <summary>Called after one recipe edit affecting <paramref name="affectedAsset"/> is added
    /// or removed - refreshes the attack-name lookup if that asset was "attacks", and reloads the
    /// currently-open asset if it's the one that changed (so a removed clone-row's ghost row
    /// disappears from the row selector, an undone Add Slot actually shrinks back, etc).</summary>
    void RefreshAfterRecipeChange(string affectedAsset)
    {
        LogDebug($"RefreshAfterRecipeChange({affectedAsset}): currentAsset={_currentAssetName}");
        if (affectedAsset == "attacks") RebuildAttackNames();

        if (_currentAssetName == affectedAsset && _currentAssetPath != null)
        {
            ReloadCurrentAssetFromRecipe();
            PopulateRowSelector();
            LoadFields((RowSelector.SelectedItem as RowItem)?.Key);
        }
    }

    /// <summary>Same idea as <see cref="RefreshAfterRecipeChange"/>, but for when the whole
    /// recipe was just replaced (New/Load Recipe) rather than one edit added or removed.</summary>
    void RefreshAllAfterRecipeReplaced()
    {
        RebuildAttackNames();
        if (_currentAssetName != null)
        {
            ReloadCurrentAssetFromRecipe();
            PopulateRowSelector();
            LoadFields((RowSelector.SelectedItem as RowItem)?.Key);
        }
    }

    // ===================================================================== finalize (create -> verify -> deploy)

    void Finalize_Click(object sender, RoutedEventArgs e)
    {
        if (_recipe.Edits.Count == 0)
        {
            Log("No edits recorded yet - nothing to build.");
            return;
        }

        var dialog = new FinalizeDialog(_recipe, _rootDir ?? Config.DefaultVanillaDir) { Owner = this };
        dialog.ShowDialog();

        // Mirror the dialog's final verify result into the main window's own Verify panel so it's
        // still there for reference after the Finalize window closes.
        if (dialog.LastVerifyChecks != null)
        {
            VerifyList.Items.Clear();
            foreach (var c in dialog.LastVerifyChecks)
                VerifyList.Items.Add(new ListBoxItem { Content = c.Message, Foreground = c.Ok ? Brushes.Green : Brushes.Red });
            Log(dialog.LastPakPath != null ? $"Finalize complete: {dialog.LastPakPath}" : "Finalize: verify failed.");
        }
    }

    // ===================================================================== diff preview

    void PreviewDiff_Click(object sender, RoutedEventArgs e)
    {
        if (_recipe.Edits.Count == 0)
        {
            Log("No edits recorded yet - nothing to preview.");
            return;
        }

        try
        {
            var previewDir = Path.Combine(Path.GetTempPath(), "absolvermodtool_diffpreview");
            if (Directory.Exists(previewDir)) Directory.Delete(previewDir, true);

            var recipePath = Path.Combine(Path.GetTempPath(), "absolvermodtool_diffpreview.recipe.json");
            RecipeIO.Save(recipePath, _recipe);
            RecipeEngine.Apply(recipePath, previewDir, _rootDir ?? Config.DefaultVanillaDir);

            LogBlank();
            Log("=== Preview: what this recipe changes vs. vanilla ===");
            var (summary, results) = DiffEngine.Run(_rootDir ?? Config.DefaultVanillaDir, previewDir);
            Log($"{summary.Total} asset(s) touched: {summary.Added} added, {summary.Changed} changed, " +
                $"{summary.ByteOnly} byte-only, {summary.Unchanged} unchanged");
            foreach (var r in results)
            {
                Log($"{r.Kind,-20} {r.Path}");
                if (r.Rows != null)
                    foreach (var row in r.Rows)
                    {
                        Log($"  row {row.Kind,-8} {row.RowKey}");
                        foreach (var c in row.Changes) Log($"    {c.Property}: {c.Old} -> {c.New}");
                    }
                if (r.Properties != null)
                    foreach (var c in r.Properties) Log($"  {c.Property}: {c.Old} -> {c.New}");
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR previewing diff: {ex.Message}");
            AppLog.WriteException("PreviewDiff_Click", ex);
        }
    }

    // ===================================================================== installed mods / settings

    void InstalledMods_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InstalledModsDialog { Owner = this };
        dialog.ShowDialog();
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_simpleMode, _showVerify, _showPendingEdits, _showResolvedColumn, _showDebugLog) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _showVerify = dialog.ShowVerify;
        _showPendingEdits = dialog.ShowPendingEdits;
        _showResolvedColumn = dialog.ShowResolvedColumn;
        ApplyPanelVisibility();
        ApplyResolvedColumnVisibility();
        if (dialog.SimpleMode != _simpleMode) SetSimpleMode(dialog.SimpleMode);

        if (dialog.ShowDebugLog != _showDebugLog)
        {
            _showDebugLog = dialog.ShowDebugLog;
            RerenderConsole();
        }

        Log("settings saved");
    }

    // ===================================================================== simplified/advanced mode
    //
    // Simplified Mode, the Verify panel, the Pending Edits panel, and the Resolved column are all
    // configured from the Settings dialog now (decluttering the toolbar) - the dialog itself
    // enforces that Simplified Mode and Verify are mutually exclusive, same as before.

    void SetSimpleMode(bool on)
    {
        _simpleMode = on;
        ApplyPanelVisibility();
        PopulateRowSelector();
        LoadFields((RowSelector.SelectedItem as RowItem)?.Key);
    }

    void ApplyPanelVisibility()
    {
        bool showConsole = !_simpleMode;
        ConsoleGroupBox.Visibility = showConsole ? Visibility.Visible : Visibility.Collapsed;
        VerifyGroupBox.Visibility = _showVerify ? Visibility.Visible : Visibility.Collapsed;
        PendingEditsGroupBox.Visibility = _showPendingEdits ? Visibility.Visible : Visibility.Collapsed;
        ConsoleColumn.Width = showConsole ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        VerifyColumn.Width = _showVerify ? new GridLength(260) : new GridLength(0);
        PendingEditsColumn.Width = _showPendingEdits ? new GridLength(260) : new GridLength(0);
    }

    void ApplyResolvedColumnVisibility() =>
        ResolvedColumn.Visibility = _showResolvedColumn ? Visibility.Visible : Visibility.Collapsed;
}
