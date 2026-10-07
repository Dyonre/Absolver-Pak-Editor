using System.IO;
using System.Windows;
using AbsolverModTool.Core;

namespace AbsolverModTool.Gui;

/// <summary>Create -> Verify -> Deploy in one place, so it's always unambiguous which pak Deploy
/// actually deploys: whatever this dialog's own Create step just built, never an older leftover.</summary>
public partial class FinalizeDialog : Window
{
    readonly Recipe _recipe;
    readonly string _srcDir;
    string? _workDir;
    string? _pakPath;

    public List<VerifyCheck>? LastVerifyChecks { get; private set; }
    public string? LastPakPath { get; private set; }

    public FinalizeDialog(Recipe recipe, string srcDir)
    {
        InitializeComponent();
        _recipe = recipe;
        _srcDir = srcDir;
    }

    void Log(string message) => ConsoleBox.AppendText(message + Environment.NewLine);

    ModFolder.Layout? _layout;

    void Create_Click(object sender, RoutedEventArgs e)
    {
        ModFolder.Layout layout;
        try { layout = ModFolder.For(ModNameBox.Text); }
        catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "Finalize Mod"); return; }

        CreateButton.IsEnabled = false;
        UndoButton.IsEnabled = true;
        ModNameBox.IsEnabled = false;
        _layout = layout;

        try
        {
            Directory.CreateDirectory(layout.Root);
            Log($"mod folder: {layout.Root}");
            RecipeIO.Save(layout.Recipe, _recipe);
            Log($"saved recipe -> {layout.Recipe}");

            if (Directory.Exists(layout.Build)) Directory.Delete(layout.Build, true);
            _workDir = layout.Build;
            var steps = RecipeEngine.Apply(layout.Recipe, layout.Build, _srcDir);
            foreach (var s in steps)
                Log(s.Op == "write" ? $"wrote    {s.Message}" : $"applied  {s.Op,-10} {s.Asset}{(s.Message is "applied" or "" ? "" : "  " + s.Message)}");

            _pakPath = layout.Pak;
            Log("");
            var result = Packer.Pack(layout.Build, layout.Pak, Log);

            LastVerifyChecks = result.Verify.Checks;
            Log("");
            foreach (var c in result.Verify.Checks) Log($"{(c.Ok ? "PASS" : "FAIL")}  {c.Message}");

            if (result.Ok)
            {
                LastPakPath = result.PakPath;
                DeployButton.IsEnabled = true;
                Log($"Mod built and verified: {result.PakPath}");
                ModFolder.Zip(layout, _recipe, Log);
                Log($"Release zip: {layout.Zip}");
            }
            else
            {
                Log("Verify failed - click Undo, fix the recipe in the main window, then try again.");
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            Log("Click Undo to reset and try again.");
        }
    }

    void Undo_Click(object sender, RoutedEventArgs e)
    {
        try { if (_layout != null) ModFolder.Clean(_layout); }
        catch (Exception ex) { Log($"(cleanup warning: {ex.Message})"); }

        _layout = null;
        _workDir = null;
        _pakPath = null;
        LastVerifyChecks = null;
        LastPakPath = null;
        CreateButton.IsEnabled = true;
        UndoButton.IsEnabled = false;
        DeployButton.IsEnabled = false;
        ModNameBox.IsEnabled = true;
        Log("--- undone - ready to create again (the recipe file was kept) ---");
    }

    void Deploy_Click(object sender, RoutedEventArgs e)
    {
        if (LastPakPath == null) return;
        try
        {
            Packer.Deploy(LastPakPath, Log);
            Log("Deployed. Launch Absolver to check in-game.");
        }
        catch (Exception ex)
        {
            Log($"ERROR deploying: {ex.Message}");
        }
    }
}
