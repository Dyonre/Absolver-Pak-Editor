using System.Windows;

namespace AbsolverModTool.Gui;

/// <summary>Undoes a Clone Row. Only ever lists rows this recipe itself added via clone-row for
/// the current asset - never a vanilla row - so this can't be used to delete original game
/// data, mirroring how Remove Slot can only undo an Add Slot.</summary>
public partial class RemoveRowDialog : Window
{
    public string? SelectedRowKey { get; private set; }

    public RemoveRowDialog(string assetName, IEnumerable<RowItem> addedRows)
    {
        InitializeComponent();
        PromptText.Text = $"Remove a row added in this recipe from {assetName}:";
        foreach (var r in addedRows) RowBox.Items.Add(r);
        if (RowBox.Items.Count > 0) RowBox.SelectedIndex = 0;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (RowBox.SelectedItem is not RowItem selected)
        {
            MessageBox.Show(this, "Choose a row to remove.", "Remove Row");
            return;
        }
        SelectedRowKey = selected.Key;
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
