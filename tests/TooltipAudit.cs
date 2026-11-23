using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using XMLator;

internal static class TooltipAudit
{
    internal static void Run()
    {
        using var wizard = new Wizard(null);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var tooltip = (ToolTip)typeof(Wizard).GetField("optionHelp", flags)!.GetValue(wizard)!;
        var bounds = Screen.FromPoint(Cursor.Position).WorkingArea;
        wizard.StartPosition = FormStartPosition.Manual;
        wizard.Location = Cursor.Position.Y < bounds.Top + bounds.Height / 2
            ? new Point(bounds.Left, bounds.Bottom - wizard.Height) : bounds.Location;
        wizard.Show();
        Application.DoEvents();
        var content = (Panel)typeof(Wizard).GetField("content", flags)!.GetValue(wizard)!;
        var combo = (ComboBox)wizard.ActiveControl!;
        var center = combo.PointToScreen(new Point(combo.ClientSize.Width / 2, combo.ClientSize.Height / 2));
        var pointer = Cursor.Position;
        wizard.Location = new Point(wizard.Left + pointer.X - center.X, wizard.Top + pointer.Y - center.Y);
        wizard.SelectNextControl(combo, true, true, true, true);

        int popups = 0;
        tooltip.Popup += (_, e) =>
        {
            if (!e.Cancel && e.AssociatedControl == combo)
                popups++;
        };
        var keyboard = new byte[256];
        if (!GetKeyboardState(keyboard))
            throw new InvalidOperationException("Could not read the test thread keyboard state.");
        var tabPressed = (byte[])keyboard.Clone();
        tabPressed[(int)Keys.Tab] |= 0x80;
        if (!SetKeyboardState(tabPressed))
            throw new InvalidOperationException("Could not set the test thread keyboard state for Tab.");
        try
        {
            if (!content.SelectNextControl(null, true, true, true, false) || wizard.ActiveControl != combo)
                throw new InvalidOperationException("Tab did not select the Windows list.");
            if (!combo.RectangleToScreen(combo.ClientRectangle).Contains(Cursor.Position))
                throw new InvalidOperationException("The pointer must remain over the list while testing keyboard focus.");
        }
        finally
        {
            SetKeyboardState(keyboard);
        }
        combo.SelectedIndex = 1;

        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 850)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        if (popups != 0)
            throw new InvalidOperationException("Keyboard focus over a stationary pointer opened a tooltip.");
        try
        {
            Cursor.Position = new Point(pointer.X + 1, pointer.Y);
            typeof(Control).GetMethod("OnMouseMove", flags)!.Invoke(combo, [new MouseEventArgs(MouseButtons.None, 0, 1, 1, 0)]);
            typeof(Control).GetMethod("OnMouseHover", flags)!.Invoke(combo, [EventArgs.Empty]);
            Application.DoEvents();
        }
        finally { Cursor.Position = pointer; }
        if (popups != 1)
            throw new InvalidOperationException("The mouse hover did not display the tooltip over the list.");
        Console.WriteLine("Tab remained silent with the pointer over the field. The native tooltip appeared on mouse hover.");
        wizard.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState([Out] byte[] keys);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] keys);
}
