using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace XMLator;

internal sealed record UsbDisk(int Number, long Size, string Path, string UniqueId, string Name, string Letters)
{
    public string Label(string language) =>
        $"{(language == "pl" ? "Dysk" : UiText.Get(language, "Disk"))} {Number}, {Name}, {Size / (double)(1L << 30):0.#} GiB" +
        (Letters.Length == 0 ? "" : $", {Letters}");
}

internal static class UsbMedia
{
    internal static string RequiredFirmware(SetupProfile profile)
    {
        if (profile.Architecture is "arm64" or "ia64")
            return "Gpt";
        if (!profile.Target.CanUseGpt(profile.Architecture))
            return "Mbr";
        if (profile.WipesDisk && !profile.DetectsDiskFirmware)
            return profile.Firmware.ToString();
        return profile.Target.Windows11 && !profile.Has("BypassRequirements") ? "Gpt" : "Any";
    }

    internal static async Task<UsbDisk[]> ListDisks(string language)
    {
        using var process = StartStorage("list", language == "pl", language: language);
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new IOException((await errors).Trim());
        return JsonSerializer.Deserialize<UsbDisk[]>(await output) ?? [];
    }

    internal static Process StartStorage(string operation, bool polish, UsbJob? job = null, string? language = null)
    {
        using var source = typeof(UsbMedia).Assembly.GetManifestResourceStream("XMLator.UsbMedia.ps1")!;
        using var reader = new StreamReader(source);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "& ([ScriptBlock]::Create($env:XMLATOR_USB_SCRIPT))" })
            start.ArgumentList.Add(arg);
        start.Environment["XMLATOR_USB_SCRIPT"] = reader.ReadToEnd();
        start.Environment["XMLATOR_USB_OPERATION"] = operation;
        start.Environment["XMLATOR_USB_LANGUAGE"] = polish ? "pl" : "en";
        if ((job?.Language ?? language) is string uiLanguage && uiLanguage is not ("pl" or "en"))
            start.Environment["XMLATOR_USB_MESSAGES"] = UiText.UsbDiagnostics(uiLanguage);
        else
            start.Environment.Remove("XMLATOR_USB_MESSAGES");
        start.Environment["XMLATOR_USB_SYSTEM"] = Environment.SystemDirectory;
        if (job is not null)
        {
            start.Environment["XMLATOR_USB_DISK"] = JsonSerializer.Serialize(job.Disk);
            start.Environment["XMLATOR_USB_ISO"] = job.Iso;
            start.Environment["XMLATOR_USB_MEDIA"] = job.MediaRoot;
            start.Environment["XMLATOR_USB_LABEL"] = job.MediaLabel;
            start.Environment["XMLATOR_USB_OVERLAY"] = job.Overlay;
            start.Environment["XMLATOR_USB_SAVED_XML"] = Path.Combine(job.Overlay, AnswerFile.DefaultName);
            start.Environment["XMLATOR_USB_EXE"] = Application.ExecutablePath;
            start.Environment["XMLATOR_USB_FIRMWARE"] = job.Firmware.ToString();
            start.Environment["XMLATOR_USB_EFI"] = job.Efi;
        }
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start Windows Storage.");
    }
}
