using System.Windows;

namespace AbsolverModTool.Gui;

record RemovableArray(string PropertyName, int AddedCount)
{
    public override string ToString() => $"{PropertyName} ({AddedCount} slot(s) added this recipe)";
}

public partial class RemoveSlotDialog : Window
{
    public string? SelectedProperty { get; private set; }

    /// <param name="candidates">Array properties that had at least one add-array-element edit
    /// recorded for the current asset/row in this recipe - never a vanilla array with nothing
    /// added, so this can't be used to remove an original element.</param>
    public RemoveSlotDialog(string assetName, IEnumerable<(string PropertyName, int AddedCount)> candidates)
    {
        InitializeComponent();
        PromptText.Text = $"Undo the most recently added slot in {assetName}:";
        foreach (var c in candidates) ArrayBox.Items.Add(new RemovableArray(c.PropertyName, c.AddedCount));
        if (ArrayBox.Items.Count > 0) ArrayBox.SelectedIndex = 0;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ArrayBox.SelectedItem is not RemovableArray selected)
        {
            MessageBox.Show(this, "Choose an array to remove a slot from.", "Remove Slot");
            return;
        }
        SelectedProperty = selected.PropertyName;
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
