using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XMLator;

internal sealed partial class Wizard
{
    private readonly UpdateService? updates;
    private bool checkingForUpdates;
    private Version? lastOfferedUpdate;

    private void AddUpdateSettings(Fields fields, Action<bool> changeAutomatic, Action<int> changeInterval,
        Action<ReleaseUpdate> acceptUpdate)
    {
        var group = new Fields();
        var automatic = group.Check(L("Sprawdzaj &automatycznie przy uruchamianiu", "Check &automatically at startup"),
            preferences.AutomaticUpdates, changeAutomatic);
        var interval = group.Select(L("Jak często &sprawdzać aktualizacje?", "How &often should updates be checked?"),
            preferences.UpdateIntervalDays,
            [C(0, "Przy każdym uruchomieniu", "At every startup"), C(1, "Raz dziennie", "Daily"),
                C(7, "Raz w tygodniu", "Weekly"), C(30, "Co 30 dni", "Every 30 days")], changeInterval);
        interval.Enabled = automatic.Checked;
        automatic.CheckedChanged += (_, _) => interval.Enabled = automatic.Checked;
        var check = group.Button(L("Sprawdź aktualizacje &teraz", "Check for updates &now"));
        check.Enabled = updates is not null;
        check.Click += async (_, _) =>
        {
            if (check.FindForm() is not Form dialog)
                return;
            if (checkingForUpdates)
            {
                ShowMessage(dialog, L("Sprawdzanie aktualizacji już trwa.", "An update check is already in progress."), MessageBoxIcon.Information);
                return;
            }
            check.Enabled = false;
            try
            {
                var update = await CheckForUpdateAsync(dialog, automatic: false);
                if (!dialog.IsDisposed && update is not null && OfferUpdate(dialog, update))
                {
                    acceptUpdate(update);
                    dialog.DialogResult = DialogResult.OK;
                }
            }
            finally
            {
                if (!check.IsDisposed)
                {
                    check.Enabled = true;
                    if (dialog.DialogResult == DialogResult.None)
                        check.Select();
                }
            }
        };
        fields.Group(L("Aktualizacje", "Updates"), group);
    }

    private async Task CheckAtStartupAsync()
    {
        var update = await CheckForUpdateAsync(this, automatic: true);
        if (update is null)
            return;
        while (!IsDisposed && (working || !IsWindowEnabled(Handle)))
            await Task.Delay(350);
        if (!IsDisposed && preferences.AutomaticUpdates && lastOfferedUpdate != update.Version && OfferUpdate(this, update))
            await InstallUpdateAsync(update);
    }

    private async Task<ReleaseUpdate?> CheckForUpdateAsync(Form owner, bool automatic)
    {
        if (updates is null)
            return null;
        checkingForUpdates = true;
        try
        {
            var update = await updates.CheckAsync();
            if (IsDisposed)
                return null;
            preferences.LastUpdateCheck = DateTimeOffset.UtcNow;
            try { preferences.Save(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (!automatic && !owner.IsDisposed && update is null)
                ShowMessage(owner, L("Masz aktualną wersję XMLatora.", "You have the latest version of XMLator."), MessageBoxIcon.Information);
            return update;
        }
        catch (Exception)
        {
            if (!automatic && !owner.IsDisposed)
                ShowMessage(owner, L("Nie udało się sprawdzić aktualizacji na GitHubie. Spróbuj ponownie później.",
                    "Could not check for updates on GitHub. Try again later."), MessageBoxIcon.Warning);
            return null;
        }
        finally { checkingForUpdates = false; }
    }

    private bool OfferUpdate(Form owner, ReleaseUpdate update)
    {
        lastOfferedUpdate = update.Version;
        string notes = update.NotesFor(UiLanguage);
        if (notes.Length == 0)
            notes = L("Wydawca nie dodał opisu zmian.", "The publisher did not include release notes.");
        string version = update.Version.ToString(3);
        string title = L("Aktualizacja XMLatora", "XMLator update");
        var (page, yes) = QuestionPage(title, L(
            "Pobrać i zainstalować aktualizację? XMLator uruchomi się ponownie.",
            "Download and install the update? XMLator will restart.") + "\r\n\r\n" +
            L("Co się zmieniło", "What's changed") + "\r\n" + notes, MessageBoxIcon.Information);
        page.Heading = LF($"Dostępny XMLator {version}", $"XMLator {version} is available");
        if (TaskDialog.ShowDialog(owner, page) != yes)
            return false;
        Validate();
        return !HasUnsavedConfiguration() || ShowMessage(owner,
            L("Masz niezapisane zmiany w kreatorze. Czy chcesz zaktualizować program bez ich zapisywania?",
                "The wizard has unsaved changes. Update without saving them?"),
            MessageBoxIcon.Warning, confirm: true, title: L("Niezapisane zmiany", "Unsaved changes")) == DialogResult.Yes;
    }

    private string UpdateFailureMessage() =>
        L("Nie udało się pobrać lub zainstalować aktualizacji XMLatora. Możesz ponowić próbę w ustawieniach.",
            "The XMLator update could not be downloaded or installed. You can retry in Settings.");

    internal void ShowUpdateFailure() => ShowMessage(this, UpdateFailureMessage(), MessageBoxIcon.Error);

    private async Task InstallUpdateAsync(ReleaseUpdate update)
    {
        if (updates is null || working)
            return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var stop = Fields.ActionButton(L("&Anuluj pobieranie", "&Cancel download"), () => cancellation.Cancel());
        navigation.Controls.Add(stop);
        ApplyColors(stop, UseDarkTheme());
        string title = L("Pobieranie aktualizacji XMLatora…", "Downloading the XMLator update…");
        string progressName = L("Postęp pobierania aktualizacji", "Update download progress");
        string? staged = null;
        SetBusy(true);
        stop.Select();
        ReportOperation(progressName, title, 0);
        try
        {
            var progress = new Progress<int>(percent =>
            {
                if (working && !stop.IsDisposed && stop.Enabled)
                    ReportOperation(progressName, title, percent);
            });
            staged = await updates.DownloadAsync(update, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            stop.Enabled = false;
            ReportOperation(progressName, L("Instalowanie aktualizacji XMLatora…", "Installing the XMLator update…"));
            UpdateInstaller.Start(updates.Executable, staged, update, UpdateFailureMessage());
            staged = null;
            FormClosing -= ConfirmClose;
            Close();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Info(L("Anulowano lub przekroczono limit czasu pobierania aktualizacji.",
                "The update download was canceled or timed out."));
        }
        catch (Exception) { ShowUpdateFailure(); }
        finally
        {
            if (staged is not null)
            {
                try { File.Delete(staged); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            if (!IsDisposed)
            {
                SetBusy(false);
                if (interfaceSettingsButton is not null)
                    interfaceSettingsButton.Select();
                else
                    FocusStep();
            }
        }
    }

    private void ShowSupport()
    {
        var close = new TaskDialogCommandLinkButton(L("&Zamknij", "&Close"));
        var page = new TaskDialogPage
        {
            Caption = L("Wsparcie i kontakt", "Support and contact"), AllowCancel = true, DefaultButton = close,
            Buttons =
            {
                SupportLink(L("Wesprzyj przez &PayPal", "Support via &PayPal"), "https://paypal.me/proszczok"),
                SupportLink(L("Wesprzyj przez &Revolut", "Support via &Revolut"), "https://revolut.me/r_rosiejka"),
                SupportLink(L("Projekt na &GitHubie", "Project on &GitHub"), UpdateService.Repository),
                SupportLink(L("&Strona autora", "Author’s &website"), "https://proszczok.pl/"),
                close
            }
        };
        TaskDialog.ShowDialog(this, page);
    }

    private TaskDialogCommandLinkButton SupportLink(string label, string address)
    {
        var button = new TaskDialogCommandLinkButton(label, address) { AllowCloseDialog = false };
        button.Click += (_, _) => Run(() => Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })?.Dispose());
        return button;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
}
