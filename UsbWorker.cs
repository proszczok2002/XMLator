using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace XMLator;

internal sealed record UsbJob(UsbDisk Disk, string Iso, string MediaRoot, string MediaLabel, string Overlay, Firmware Firmware, string Efi,
    bool Polish, string? Language = null);

internal enum UsbStage { Prepare, Elevate, Checking, ExtractingBoot, Splitting, Formatting, Copy, Extras, Verify, Boot, Finalizing }

internal readonly record struct UsbProgress(UsbStage Stage, int? Percent = null)
{
    internal static bool TryParse(string line, out UsbProgress progress)
    {
        progress = default;
        if (!line.StartsWith("PROGRESS ", StringComparison.Ordinal))
            return false;
        string[] parts = line[9..].Split(' ');
        if (parts.Length > 2 || !Enum.TryParse(parts[0], out UsbStage stage) || stage < UsbStage.Checking || !Enum.IsDefined(stage))
            return false;
        if (stage == UsbStage.Copy)
        {
            if (parts.Length != 2 || !int.TryParse(parts[1], out int percent) || percent is < 0 or > 100)
                return false;
            progress = new(stage, percent);
            return true;
        }
        if (parts.Length != 1)
            return false;
        progress = new(stage);
        return true;
    }
}

internal static class UsbWorker
{
    private const string Command = "--usb-worker";
    private const string PipePrefix = "XMLator-usb-";

    internal static async Task<bool> Prepare(UsbJob job, Func<Task<bool>> confirm, string? workerExecutable = null,
        Action<UsbProgress>? report = null)
    {
        string pipeName = PipePrefix + Guid.NewGuid().ToString("N");
        using var user = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
        var start = new ProcessStartInfo(workerExecutable ?? Environment.ProcessPath!) { Verb = "runas", UseShellExecute = true, WorkingDirectory = Environment.SystemDirectory };
        foreach (string argument in new[] { Command, pipeName, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Windows Storage.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await pipe.WaitForConnectionAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            throw new IOException(job.Polish ? "Nie udało się połączyć z usługą przygotowania pendrive’a." : "Could not connect to the USB preparation worker.");
        }
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != process.Id)
            throw new IOException("Unexpected USB preparation process.");

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(job));
        if (await ReadStatus(reader, report) is not "READY" and var response)
            throw ReadFailure(response, job.Polish);
        bool approved = await confirm();
        await writer.WriteLineAsync(approved ? "WRITE" : "CANCEL");
        string? result = await ReadStatus(reader, report);
        if (result != (approved ? "DONE" : "CANCELED"))
            throw ReadFailure(result, job.Polish);
        return approved;
    }

    private static async Task<string?> ReadStatus(StreamReader reader, Action<UsbProgress>? report)
    {
        while (await reader.ReadLineAsync() is string line)
        {
            if (!UsbProgress.TryParse(line, out var progress))
                return line;
            report?.Invoke(progress);
        }
        return null;
    }

    internal static bool RunIfRequested(string[] args)
    {
        if (args.FirstOrDefault() != Command)
            return false;
        if (args is [_, var pipeName, var parent] && pipeName.StartsWith(PipePrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(pipeName[PipePrefix.Length..], "N", out _) && uint.TryParse(parent, out uint parentId))
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                Run(pipeName, parentId).GetAwaiter().GetResult();
        }
        return true;
    }

    private static async Task Run(string pipeName, uint parentId)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try { await pipe.ConnectAsync(15000); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        catch (TimeoutException) { return; }
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid != parentId)
            return;
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        try
        {
            string? message = await reader.ReadLineAsync();
            if (message is null)
                return;
            var job = JsonSerializer.Deserialize<UsbJob>(message) ?? throw new IOException("Missing USB preparation settings.");
            string answer = Path.Combine(job.Overlay, AnswerFile.DefaultName);
            if (!File.Exists(answer) || !File.Exists(job.Iso) || !Directory.Exists(job.MediaRoot))
                throw new IOException(job.Polish ? "Brakuje pliku odpowiedzi lub obrazu ISO." : "The answer file or ISO is missing.");
            using var process = UsbMedia.StartStorage("write", job.Polish, job);
            Task<string> errors = process.StandardError.ReadToEndAsync();
            string? first = await ForwardProgress();
            bool writing = false;
            try
            {
                if (first == "READY")
                {
                    await writer.WriteLineAsync("READY");
                    writing = await reader.ReadLineAsync() == "WRITE";
                    if (writing)
                        await process.StandardInput.WriteLineAsync("WRITE");
                }
            }
            finally { process.StandardInput.Close(); }
            string? terminal = first == "READY" ? await ForwardProgress() : first;
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || first != "READY" || terminal != (writing ? "DONE" : null) || !string.IsNullOrWhiteSpace(output))
            {
                string details = (await errors).Trim();
                throw new IOException(details.Length == 0 ? ReadFailure(null, job.Polish).Message : details);
            }
            await writer.WriteLineAsync(writing ? "DONE" : "CANCELED");

            async Task<string?> ForwardProgress()
            {
                while (await process.StandardOutput.ReadLineAsync() is string line)
                {
                    if (!UsbProgress.TryParse(line, out _))
                        return line;
                    await writer.WriteLineAsync(line);
                }
                return null;
            }
        }
        catch (Exception e)
        {
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(e.Message)); }
            catch (IOException) { }
        }
    }

    private static IOException ReadFailure(string? response, bool polish) => new(response is not null && response.StartsWith('"')
        ? JsonSerializer.Deserialize<string>(response)
        : polish ? "Przygotowanie pendrive’a zakończyło się błędem." : "USB preparation failed.");

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
