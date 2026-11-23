namespace XMLator;

internal sealed class SelectionComboBox : ComboBox
{
    private const int ValuePropertyId = 30045;
    private string selectedText = "";

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        string previous = selectedText;
        string current = GetItemText(SelectedItem) ?? "";
        selectedText = current;
        base.OnSelectedIndexChanged(e);
        if (IsHandleCreated && Focused && !DroppedDown && current == selectedText && current != previous)
        {
            WinFormsAutomation.RaisePropertyChange(AccessibilityObject, ValuePropertyId, previous, current);
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        }
    }
}
