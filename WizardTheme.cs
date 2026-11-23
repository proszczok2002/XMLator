using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace XMLator;

internal sealed partial class Wizard
{
    private static readonly Color DarkSurface = Color.FromArgb(28, 29, 32);
    private static readonly Color DarkInput = Color.FromArgb(44, 46, 51);
    private static readonly Color DarkText = Color.FromArgb(244, 245, 247);
    private static readonly Color DarkBorder = Color.FromArgb(108, 112, 122);
    private static readonly Color DarkHighlight = Color.FromArgb(61, 79, 107);
    private readonly ToolStripRenderer darkMenu = new ToolStripProfessionalRenderer(new DarkMenuColors());
    private readonly ToolStripRenderer systemMenu = new ToolStripSystemRenderer();

    private void ChangeTheme(AppTheme theme) => ChangeInterfacePreferences(preferences.Language, theme);

    private void SetNativeTheme() => Application.SetColorMode(
        UseDarkTheme() && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            ? SystemColorMode.Dark : SystemColorMode.Classic);

    private bool UseDarkTheme()
    {
        if (SystemInformation.HighContrast || preferences.Theme == AppTheme.Light)
            return false;
        return preferences.Theme == AppTheme.Dark ||
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
    }

    private void ApplyTheme(Form? dialog = null)
    {
        var form = dialog ?? this;
        bool dark = UseDarkTheme();
        ApplyColors(form, dark);
        if (dialog is null)
        {
            optionHelp.OwnerDraw = dark;
            menu.Renderer = dark ? darkMenu : systemMenu;
            foreach (ToolStripItem item in menu.Items)
                ColorMenuItem(item, dark);
        }
        if (form.IsHandleCreated)
            SetTitleBar(form, dark);
        else if (dialog is not null)
            dialog.HandleCreated += (_, _) => SetTitleBar(dialog, dark);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetTitleBar(this, UseDarkTheme());
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        if (IsHandleCreated)
            ApplyTheme();
    }

    private static void ApplyColors(Control control, bool dark)
    {
        bool input = control is TextBoxBase or ComboBox or ListBox or UpDownBase;
        control.BackColor = dark ? input || control is Button ? DarkInput : DarkSurface :
            input ? SystemColors.Window : SystemColors.Control;
        control.ForeColor = dark ? DarkText : input ? SystemColors.WindowText : SystemColors.ControlText;
        if (control is Button button)
        {
            button.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
            button.UseVisualStyleBackColor = !dark;
            button.FlatAppearance.BorderColor = DarkBorder;
            button.FlatAppearance.MouseOverBackColor = DarkHighlight;
            button.FlatAppearance.MouseDownBackColor = DarkHighlight;
        }
        foreach (Control child in control.Controls)
            ApplyColors(child, dark);
    }

    private static void ColorMenuItem(ToolStripItem item, bool dark)
    {
        item.ForeColor = dark ? DarkText : SystemColors.MenuText;
        item.BackColor = dark ? DarkSurface : SystemColors.Menu;
        if (item is ToolStripDropDownItem dropDown)
            foreach (ToolStripItem child in dropDown.DropDownItems)
                ColorMenuItem(child, dark);
    }

    private static void SetTitleBar(Form form, bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            return;
        const int useImmersiveDarkMode = 20;
        int mode = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(form.Handle, useImmersiveDarkMode, ref mode, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int bytes);

    private void DrawOptionHelp(object? sender, DrawToolTipEventArgs e)
    {
        using var background = new SolidBrush(DarkInput);
        using var border = new Pen(DarkBorder);
        e.Graphics.FillRectangle(background, e.Bounds);
        e.Graphics.DrawRectangle(border, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
        TextRenderer.DrawText(e.Graphics, e.ToolTipText, e.Font ?? Font, Rectangle.Inflate(e.Bounds, -2, -1),
            DarkText, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => DarkSurface;
        public override Color MenuStripGradientEnd => DarkSurface;
        public override Color ToolStripDropDownBackground => DarkSurface;
        public override Color MenuItemSelected => DarkHighlight;
        public override Color MenuItemSelectedGradientBegin => DarkHighlight;
        public override Color MenuItemSelectedGradientEnd => DarkHighlight;
        public override Color MenuItemPressedGradientBegin => DarkHighlight;
        public override Color MenuItemPressedGradientMiddle => DarkHighlight;
        public override Color MenuItemPressedGradientEnd => DarkHighlight;
        public override Color MenuItemBorder => DarkBorder;
        public override Color MenuBorder => DarkBorder;
        public override Color ImageMarginGradientBegin => DarkSurface;
        public override Color ImageMarginGradientMiddle => DarkSurface;
        public override Color ImageMarginGradientEnd => DarkSurface;
        public override Color SeparatorDark => DarkBorder;
        public override Color SeparatorLight => DarkSurface;
    }
}
