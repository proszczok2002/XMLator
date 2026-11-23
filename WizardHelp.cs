namespace XMLator;

internal sealed partial class Wizard
{
    private readonly ToolTip optionHelp = new();
    private Control? visibleHelp;

    private T Explain<T>(T control, string polishText, string englishText, Func<string?>? details = null) where T : Control
    {
        string fallback = L(polishText, englishText);
        string Description() => details?.Invoke() ?? ((control as ComboBox)?.SelectedItem as IChoiceDescription)?.Description ?? fallback;
        void AttachMouseHelp(Control target)
        {
            target.Tag = (Func<string>)Description;
            Point lastPointer = Cursor.Position;
            bool moved = false;
            target.MouseMove += (_, _) =>
            {
                Point position = Cursor.Position;
                moved |= position != lastPointer;
                lastPointer = position;
            };
            target.MouseHover += (_, _) =>
            {
                if (!moved || !target.IsHandleCreated || !target.ClientRectangle.Contains(target.PointToClient(Cursor.Position)))
                    return;
                moved = false;
                visibleHelp = target;
                // Manual display avoids registering a second tooltip for keyboard focus.
                optionHelp.Show(Description(), target, 0, target.Height, 9000);
            };
            void Clear()
            {
                moved = false;
                lastPointer = Cursor.Position;
                if (visibleHelp != target)
                    return;
                visibleHelp = null;
                if (target.IsHandleCreated)
                    optionHelp.Hide(target);
            }
            target.MouseLeave += (_, _) => Clear();
            target.Disposed += (_, _) => Clear();
        }
        AttachMouseHelp(control);
        if (control is NumericUpDown)
            foreach (TextBox editor in control.Controls.OfType<TextBox>())
                AttachMouseHelp(editor);
        return control;
    }

    private string KeyboardHelp()
    {
        char previous = Mnemonic(L("&Wstecz", "&Back"), 'B');
        char following = Mnemonic(L("&Dalej", "&Next"), 'N');
        return LF($"Tab i Shift+Tab przechodzą między polami. Strzałki zmieniają wybór, a Spacja przełącza zaznaczenie. Alt+{previous} wraca, Alt+{following} przechodzi dalej. Ctrl+S zapisuje XML, Ctrl+Shift+S zapisuje profil INI, Ctrl+O otwiera profil, a Ctrl+End otwiera podsumowanie.",
            $"Tab and Shift+Tab move between fields. Arrow keys change the selection and Space toggles checkboxes. Alt+{previous} goes back and Alt+{following} continues. Ctrl+S saves the XML file, Ctrl+Shift+S saves an INI profile, Ctrl+O opens a profile, and Ctrl+End opens the review.");

        static char Mnemonic(string label, char fallback) =>
            label.Select(char.ToUpperInvariant).FirstOrDefault(letter => Control.IsMnemonic(letter, label), fallback);
    }

    private void ShowAbout()
    {
        var close = new TaskDialogButton(L("&Zamknij", "&Close"));
        var notices = new TaskDialogButton(L("Zapisz informacje o &licencjach…", "Save &license information…"));
        var selected = TaskDialog.ShowDialog(this, new TaskDialogPage
        {
            Caption = L("O programie XMLator", "About XMLator"),
            Heading = ProductCaption,
            Text = L("Kreator plików odpowiedzi Windows. Korzysta z DiscUtils i Schneegans Unattend.",
                "Windows answer file wizard powered by DiscUtils and Schneegans Unattend."),
            Expander = new TaskDialogExpander
            {
                CollapsedButtonText = L("Pokaż informacje o licencjach", "Show license information"),
                ExpandedButtonText = L("Ukryj informacje o licencjach", "Hide license information"),
                Text = "Schneegans Unattend (MIT)\r\n" + UpstreamLicense() +
                    "\r\nDiscUtils (MIT)\r\nNewtonsoft.Json (MIT)\r\nMicrosoft Edge WebView2 SDK\r\n.NET"
            },
            AllowCancel = true,
            Buttons = { notices, close },
            DefaultButton = close
        });
        if (selected != notices)
            return;
        using var save = new SaveFileDialog { FileName = "THIRD-PARTY-NOTICES.txt", DefaultExt = "txt" };
        if (save.ShowDialog(this) == DialogResult.OK)
        {
            using var source = typeof(Wizard).Assembly.GetManifestResourceStream("XMLator.ThirdPartyNotices.txt")!;
            using var destination = File.Create(save.FileName);
            source.CopyTo(destination);
        }
    }

    private void ShowXmlPreview(string xml)
    {
        using var dialog = new Form
        {
            Text = AnswerFile.DefaultName, StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false, Font = Font, AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(690, 520), MinimumSize = new Size(430, 210), Padding = new Padding(9)
        };
        var lines = new ListBox
        {
            Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, TabIndex = 0,
            AccessibleName = L("Wiersze pliku Autounattend.xml", "Autounattend.xml lines")
        };
        using (var reader = new StringReader(xml))
        {
            var items = new List<string>();
            while (reader.ReadLine() is string line)
                items.Add(line);
            lines.Items.AddRange(items.ToArray());
        }
        if (lines.Items.Count > 0)
            lines.SelectedIndex = 0;
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, TabIndex = 1 };
        var close = Fields.ActionButton(L("&Zamknij", "&Close"), () => dialog.DialogResult = DialogResult.OK);
        var copy = Fields.ActionButton(L("S&kopiuj cały XML", "&Copy entire XML"), () =>
        {
            try
            {
                Clipboard.SetText(xml);
                ShowMessage(dialog, L("Skopiowano XML do schowka.", "XML copied to the clipboard."), MessageBoxIcon.Information);
            }
            catch (Exception e) { Error(e, dialog); }
        });
        buttons.Controls.AddRange([close, copy]);
        dialog.Controls.Add(lines);
        dialog.Controls.Add(buttons);
        dialog.CancelButton = close;
        ApplyTheme(dialog);
        dialog.ActiveControl = lines;
        dialog.ShowDialog(this);
    }
}
