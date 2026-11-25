using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace XMLator;

internal sealed partial class Wizard
{
    internal static Color DarkSurface { get; } = Color.FromArgb(26, 39, 56);
    private static readonly Color DarkInput = Color.FromArgb(12, 20, 31);
    private static readonly Color DarkText = Color.FromArgb(244, 248, 252);
    private static readonly Color DarkBorder = Color.FromArgb(126, 146, 169);
    private static readonly Color DarkHighlight = Color.FromArgb(50, 75, 102);
    private readonly ToolStripRenderer darkMenu = new ToolStripProfessionalRenderer(new DarkMenuColors());
    private readonly ToolStripRenderer systemMenu = new ToolStripSystemRenderer();
    private bool? appliedDarkTheme;

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
        bool dark = UseDarkTheme();
        if (dialog is not null)
        {
            dialog.HandleCreated += (_, _) => SetTitleBar(dialog, UseDarkTheme());
            dialog.SystemColorsChanged += (_, _) =>
            {
                bool current = UseDarkTheme();
                ApplyColors(dialog, current, refreshNativeTheme: current != dark);
                SetTitleBar(dialog, current);
                dark = current;
            };
            ApplyColors(dialog, dark);
            return;
        }

        bool refreshNativeTheme = appliedDarkTheme is bool previous && previous != dark;
        appliedDarkTheme = dark;
        ApplyColors(this, dark, refreshNativeTheme);
        optionHelp.OwnerDraw = dark;
        menu.Renderer = dark ? darkMenu : systemMenu;
        foreach (ToolStripItem item in menu.Items)
            ColorMenuItem(item, dark);
        if (IsHandleCreated)
            SetTitleBar(this, dark);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetTitleBar(this, UseDarkTheme());
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        if (IsHandleCreated && !IsDisposed)
        {
            SetNativeTheme();
            ApplyTheme();
        }
    }

    protected override void WndProc(ref Message message)
    {
        const int settingChange = 0x001A;
        bool immersiveColorsChanged = message.Msg == settingChange && message.LParam != IntPtr.Zero &&
            Marshal.PtrToStringUni(message.LParam) == "ImmersiveColorSet";
        base.WndProc(ref message);
        if (immersiveColorsChanged && preferences.Theme == AppTheme.System)
            OnSystemColorsChanged(EventArgs.Empty);
    }

    private static void ApplyColors(Control control, bool dark, bool refreshNativeTheme = false)
    {
        bool input = control is TextBoxBase or ComboBox or ListBox or UpDownBase;
        if (control is ButtonBase and not CheckBox || control is ProgressBar)
        {
            // Keep the native control's rendering for focus, default, pressed and disabled states.
            control.BackColor = SystemColors.Control;
            control.ForeColor = SystemColors.ControlText;
            if (control is ButtonBase button)
                button.UseVisualStyleBackColor = true;
        }
        else
        {
            control.BackColor = dark ? input ? DarkInput : DarkSurface :
                input ? SystemColors.Window : SystemColors.Control;
            control.ForeColor = dark ? DarkText : input ? SystemColors.WindowText : SystemColors.ControlText;
        }
        if (refreshNativeTheme && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && control.IsHandleCreated)
            RefreshNativeTheme(control, dark);
        control.Invalidate();
        foreach (Control child in control.Controls)
            ApplyColors(child, dark, refreshNativeTheme);
    }

    private static void RefreshNativeTheme(Control control, bool dark)
    {
        // WinForms applies these themes when creating HWNDs. Refresh existing HWNDs in place.
        string? explorer = dark ? "DarkMode_Explorer" : null;
        if (control is ComboBox combo)
        {
            _ = SetWindowTheme(combo.Handle, dark ? "DarkMode_CFD" : null, null);
            var info = new ComboBoxInfo { Size = Marshal.SizeOf<ComboBoxInfo>() };
            if (GetComboBoxInfo(combo.Handle, ref info))
                _ = SetWindowTheme(info.ListHandle, explorer, null);
        }
        else if (control is ButtonBase or TextBoxBase or ListBox or UpDownBase or ProgressBar or ScrollableControl { AutoScroll: true })
            _ = SetWindowTheme(control.Handle, explorer, null);
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

    [DllImport("uxtheme.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr window, string? application, string? identifiers);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComboBoxInfo(IntPtr window, ref ComboBoxInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct ComboBoxInfo
    {
        internal int Size;
        internal Rectangle ItemRectangle;
        internal Rectangle ButtonRectangle;
        internal int ButtonState;
        internal IntPtr ComboHandle;
        internal IntPtr EditHandle;
        internal IntPtr ListHandle;
    }

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
