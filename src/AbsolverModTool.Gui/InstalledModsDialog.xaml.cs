using System.IO;
using System.Windows;
using AbsolverModTool.Core;

namespace AbsolverModTool.Gui;

record InstalledModRow(string Name, string SizeText, string ModifiedText, string FullPath);

public partial class InstalledModsDialog : Window
{
    public InstalledModsDialog()
    {
        InitializeComponent();
        FolderText.Text = $"Mods in {Config.GamePaksDir} (the game's own pakchunk*.pak files are hidden)";
        Refresh();
    }

    void Refresh()
    {
        ModsList.Items.Clear();
        if (!Directory.Exists(Config.GamePaksDir))
        {
            FolderText.Text = $"Folder not found: {Config.GamePaksDir} - check Settings.";
            return;
        }

        var paks = Directory.GetFiles(Config.GamePaksDir, "*.pak")
            .Where(p => !Path.GetFileName(p).StartsWith("pakchunk", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => File.GetLastWriteTime(p));

        foreach (var p in paks)
        {
            var info = new FileInfo(p);
            ModsList.Items.Add(new InstalledModRow(
                info.Name,
                $"{info.Length / 1024.0:N0} KB",
                info.LastWriteTime.ToString("g"),
                info.FullName));
        }
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ModsList.SelectedItem is not InstalledModRow row) return;

        var confirm = MessageBox.Show(this, $"Remove {row.Name} (and its .sig, if any) from the Paks folder?",
            "Remove Mod", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            File.Delete(row.FullPath);
            var sig = Path.ChangeExtension(row.FullPath, ".sig");
            if (File.Exists(sig)) File.Delete(sig);
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't remove {row.Name}: {ex.Message}", "Remove Mod", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
