using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace XMLator;

internal static class UpdateInstaller
{
    internal const string ReadyArgument = "--xmlator-update-ready";
    internal const string FailedArgument = "--xmlator-update-failed";

    internal static (EventWaitHandle? Ready, bool Failed, string? Profile) StartupArguments(string[] args)
    {
        if (args is [ReadyArgument, string token] && Guid.TryParseExact(token, "N", out _) &&
            EventWaitHandle.TryOpenExisting($@"Local\XMLator.Update.Ready.{token}", out var ready))
            return (ready, false, null);
        if (args is [FailedArgument])
            return (null, true, null);
        return (null, false, args.FirstOrDefault());
    }

    internal static void Start(string executable, string staged, ReleaseUpdate update, string failureMessage)
    {
        using var current = Process.GetCurrentProcess();
        using var worker = Process.Start(CreateStartInfo(executable, staged, update,
            current.Id, current.StartTime.ToUniversalTime().Ticks, failureMessage)) ??
            throw new InvalidOperationException("Could not start the update installer.");
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string staged, ReleaseUpdate update,
        int parentId, long parentStart, string failureMessage)
    {
        using var stream = typeof(UpdateInstaller).Assembly.GetManifestResourceStream("XMLator.ApplyUpdate.ps1")!;
        using var reader = new StreamReader(stream);
        string script = Convert.ToBase64String(Encoding.Unicode.GetBytes(reader.ReadToEnd()));
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", script })
            start.ArgumentList.Add(argument);
        start.Environment["XMLATOR_UPDATE_TARGET"] = executable;
        start.Environment["XMLATOR_UPDATE_STAGE"] = staged;
        start.Environment["XMLATOR_UPDATE_VERSION"] = update.Version.ToString(4);
        start.Environment["XMLATOR_UPDATE_HASH"] = update.Sha256;
        start.Environment["XMLATOR_UPDATE_SIZE"] = update.Size.ToString(CultureInfo.InvariantCulture);
        start.Environment["XMLATOR_UPDATE_PARENT"] = parentId.ToString(CultureInfo.InvariantCulture);
        start.Environment["XMLATOR_UPDATE_STARTED"] = parentStart.ToString(CultureInfo.InvariantCulture);
        start.Environment["XMLATOR_UPDATE_MESSAGE"] = failureMessage;
        start.Environment["XMLATOR_UPDATE_MUTEX"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant())));
        return start;
    }
}
