using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using XMLator;

internal static partial class SelectionAudit
{
    // Run on an active isolated desktop. The captures include the rendered native controls.
    internal static void DarkTheme(string binary, string output)
    {
        Directory.CreateDirectory(output);
        var fixture = Directory.CreateDirectory(Path.Combine(output, Guid.NewGuid().ToString("N")));
        try
        {
            string exe = Path.Combine(fixture.FullName, "XMLator.exe");
            File.Copy(binary, exe);
            string config = Path.Combine(fixture.FullName, "config.ini");
            new InterfacePreferences(config) { Language = "pl", Theme = AppTheme.Dark, AutomaticUpdates = false }.Save();
            using var process = Process.Start(exe)!;
            try
            {
                var wizard = WaitForWizard(process);
                var version = Named(wizard, ControlType.ComboBox, "Który Windows chcesz zainstalować?");
                FocusWhenReady(version);
                Capture(wizard, "01-dark-wizard");
                var bounds = wizard.Current.BoundingRectangle;
                if (!SetCursorPos((int)bounds.Left + 2, (int)bounds.Top + 2))
                    throw new InvalidOperationException("Could not move the pointer away from the opened list.");
                VerifyChange(version, "Windows 11", "Windows 10");
                var popup = (ExpandCollapsePattern)version.GetCurrentPattern(ExpandCollapsePattern.Pattern);
                popup.Expand();
                Capture(wizard, "02-dark-open-list");
                popup.Collapse();
                var architecture = Named(wizard, ControlType.ComboBox, "Architektura obrazu instalacyjnego");
                VerifyChange(architecture, "x64 (AMD64)", "x86 (32-bit)");
                var extra = Named(wizard, ControlType.CheckBox, "Dodaj architekturę x64 (AMD64) do tego samego XML");
                VerifyCheckBox(extra, false);
                Capture(wizard, "03-dark-checked");
                VerifyCheckBox(extra, true);

                var settings = Named(wizard, ControlType.Button, "Ustawienia…");
                ChangeAppearance("Ciemny", "Jasny", Keys.Up, AppTheme.Light);
                if (ReadDialogBackground(process.MainWindowHandle).GetBrightness() < 0.65)
                    throw new InvalidOperationException("The main window did not repaint in light mode.");
                Capture(wizard, "05-light-wizard");
                VerifyNativeBackground(version, dark: false);
                VerifyNativeBackground(settings, dark: false);
                ChangeAppearance("Jasny", "Ciemny", Keys.Down, AppTheme.Dark);
                if (ReadDialogBackground(process.MainWindowHandle).ToArgb() != Wizard.DarkSurface.ToArgb())
                    throw new InvalidOperationException("The main window did not repaint in dark mode.");
                if (ReadValue(version) != "Windows 10" || ReadValue(architecture) != "x86 (32-bit)")
                    throw new InvalidOperationException("Changing appearance lost the current wizard choices.");
                FocusWhenReady(version);
                popup.Expand();
                Capture(wizard, "06-dark-open-list-after-switch");
                popup.Collapse();
                VerifyNativeBackground(version, dark: true);
                VerifyNativeBackground(settings, dark: true);

                var support = Named(wizard, ControlType.Button, "Wsparcie i kontakt…");
                FocusWhenReady(support);
                PostKey(support, Keys.Space);
                var contact = WaitForOwnedDialog(wizard, "Wsparcie i kontakt");
                Capture(contact, "07-dark-support");
                PostKey(Named(contact, ControlType.Button, "Wesprzyj przez PayPal"), Keys.Escape);
                if (!SpinWait.SpinUntil(() => support.Current.HasKeyboardFocus, 4000))
                    throw new InvalidOperationException("Closing Support did not restore keyboard focus.");
                Console.WriteLine("Dark → light → dark, saved settings, retained selections, native drop-down, checkbox events and dialog focus verified.");

                void ChangeAppearance(string current, string next, Keys key, AppTheme mode)
                {
                    FocusWhenReady(settings);
                    PostKey(settings, Keys.Space);
                    var dialog = WaitForOwnedDialog(wizard, "Ustawienia");
                    var themes = Named(dialog, ControlType.ComboBox, "Motyw interfejsu XMLatora");
                    FocusWhenReady(themes);
                    if (mode == AppTheme.Light)
                        Capture(dialog, "04-dark-settings");
                    VerifyChange(themes, current, next, key);
                    var ok = Named(dialog, ControlType.Button, "OK");
                    FocusWhenReady(ok);
                    PostKey(ok, Keys.Space);
                    if (!SpinWait.SpinUntil(() => new InterfacePreferences(config).Theme == mode &&
                        settings.Current.HasKeyboardFocus, 5000))
                        throw new InvalidOperationException("The appearance choice was not saved or focus did not return to Settings.");
                }
            }
            finally { CloseReleaseFixture(process); }
        }
        finally { fixture.Delete(recursive: true); }

        void Capture(AutomationElement window, string name)
        {
            Thread.Sleep(500);
            var bounds = window.Current.BoundingRectangle;
            using var bitmap = new Bitmap((int)bounds.Width, (int)bounds.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen((int)bounds.Left, (int)bounds.Top, 0, 0, bitmap.Size);
            bitmap.Save(Path.Combine(output, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }

        static void VerifyNativeBackground(AutomationElement element, bool dark)
        {
            Thread.Sleep(240);
            var bounds = element.Current.BoundingRectangle;
            using var bitmap = new Bitmap(1, 1);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen((int)bounds.Left + 6, (int)bounds.Bottom - 5, 0, 0, bitmap.Size);
            float brightness = bitmap.GetPixel(0, 0).GetBrightness();
            if (dark ? brightness > 0.45f : brightness < 0.65f)
                throw new InvalidOperationException($"{element.Current.Name} retained the previous native theme.");
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
