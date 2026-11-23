using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using XMLator;

internal static class UpdateAudit
{
    internal static async Task Run(string previous, string next, string workDirectory)
    {
        string folder = Path.Combine(Path.GetFullPath(workDirectory), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, "XMLator.exe");
        string config = Path.Combine(folder, "config.ini");
        string rollbackFolder = Path.Combine(folder, "rollback");
        string rollbackTarget = Path.Combine(rollbackFolder, "XMLator.exe");
        try
        {
            File.Copy(previous, target);
            var older = ReadVersion(target);
            var version = ReadVersion(next);
            byte[] binary = await File.ReadAllBytesAsync(next);
            string digest = Hash(binary);
            string tag = $"v{version.ToString(3)}";
            string asset = $"{UpdateService.Repository}/releases/download/{tag}/XMLator.exe";
            byte[] Metadata(string url, string sha, bool prerelease = false) => JsonSerializer.SerializeToUtf8Bytes(new
            {
                tag_name = tag, draft = false, prerelease, body = "## Polski\n- Test aktualizacji\n\n## English\n- Update test",
                assets = new[] { new { name = "XMLator.exe", state = "uploaded", size = binary.Length, digest = "sha256:" + sha, browser_download_url = url } }
            });
            byte[] metadata = Metadata(asset, digest);
            Require(UpdateService.ParseRelease(metadata, version) is null, "The same version is not offered.");
            Require(UpdateService.ParseRelease(metadata, new Version(99, 0, 0, 0)) is null, "The installed version cannot be downgraded.");
            await Reject(() => Task.FromResult(UpdateService.ParseRelease(Metadata(asset, digest, prerelease: true), older)));
            await Reject(() => Task.FromResult(UpdateService.ParseRelease(Metadata("https://example.org/XMLator.exe", digest), older)));
            await Reject(() => Task.FromResult(UpdateService.ParseRelease(Metadata(asset, new string('?', 64)), older)));

            var preferences = new InterfacePreferences(config)
            {
                Language = "en", Theme = AppTheme.Dark, AutomaticUpdates = false, UpdateIntervalDays = 7,
                LastUpdateCheck = DateTimeOffset.UtcNow.AddDays(-3)
            };
            preferences.Save();
            string originalConfig = File.ReadAllText(config);
            var saved = new InterfacePreferences(config);
            var now = DateTimeOffset.UtcNow;
            Require(!saved.AutomaticUpdates && saved.UpdateIntervalDays == 7 && !saved.ShouldCheckForUpdates(now), "Automatic checks can be disabled.");
            saved.AutomaticUpdates = true;
            Require(!saved.ShouldCheckForUpdates(now) && saved.ShouldCheckForUpdates(now.AddDays(5)), "Restart retains the configured interval.");
            saved.UpdateIntervalDays = 0;
            Require(saved.ShouldCheckForUpdates(now), "The every-startup interval checks again.");

            const string cdn = "https://release-assets.githubusercontent.com/xmlator-test";
            string redirectTo = cdn;
            byte[] download = binary;
            using var http = new HttpClient(new Fixture(request =>
            {
                if (request.RequestUri?.Host == "api.github.com")
                    return Response(metadata);
                if (request.RequestUri?.AbsoluteUri == asset)
                    return new(HttpStatusCode.Found) { Headers = { Location = new(redirectTo) } };
                if (request.RequestUri?.AbsoluteUri == cdn)
                    return Response(download);
                throw new InvalidOperationException("Unexpected update request.");
            }));
            var service = new UpdateService(target, older, http);
            var release = await service.CheckAsync() ?? throw new InvalidOperationException("No update offered.");
            Require(release.NotesFor("pl") == "• Test aktualizacji" && release.NotesFor("fr") == "• Update test", "Release notes follow the language.");
            redirectTo = "https://example.org/evil";
            await Reject(() => service.DownloadAsync(release, null, default));
            redirectTo = cdn;
            download = binary[..^1];
            await Reject(() => service.DownloadAsync(release, null, default));
            download = (byte[])binary.Clone();
            download[^1] ^= 1;
            await Reject(() => service.DownloadAsync(release, null, default));
            download = binary;
            try
            {
                await service.DownloadAsync(release, null, new CancellationToken(canceled: true));
                throw new InvalidOperationException("Accepted a canceled download.");
            }
            catch (OperationCanceledException) { }
            Require(!Directory.GetFiles(folder, ".xmlator-update-*").Any(), "Rejected downloads leave no staging files.");
            var progress = new ProgressLog();
            string staged = await service.DownloadAsync(release, progress, default);
            Require(progress.Values.SequenceEqual(Enumerable.Range(0, 101)), "The download reports each percentage point.");
            Console.WriteLine("Release metadata, language, schedule, cancellation, redirect, size, SHA-256 and progress passed.");

            using (var app = Process.Start(new ProcessStartInfo(target) { UseShellExecute = false })!)
            using (var worker = StartWorker(target, staged, release, app))
            {
                await Task.Delay(1400);
                Require(!worker.HasExited && File.Exists(staged) && ReadVersion(target) == older, "The helper waits for the running process.");
                app.Kill();
                await app.WaitForExitAsync();
                await Wait(worker);
                Require(worker.ExitCode == 0, $"The Windows replacement helper must succeed. Exit {worker.ExitCode}.");
            }
            Require(ReadVersion(target) == version && Hash(await File.ReadAllBytesAsync(target)) == digest &&
                originalConfig == File.ReadAllText(config) && !Directory.GetFiles(folder, ".xmlator-update-*").Any() &&
                FindRunning(target) is not null, "The updated wizard reached its main window, preserved config and removed the old file.");
            StopFixture(target);
            Console.WriteLine("Win32 replacement, startup handshake, configuration preservation and old EXE deletion passed.");

            Directory.CreateDirectory(rollbackFolder);
            string rollbackConfig = Path.Combine(rollbackFolder, "config.ini");
            File.Copy(config, rollbackConfig);
            File.Copy(previous, rollbackTarget);
            staged = Path.Combine(rollbackFolder, ".xmlator-update-rollback.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), staged);
            byte[] incompatible = await File.ReadAllBytesAsync(staged);
            var invalid = release with { Size = incompatible.Length, Sha256 = Hash(incompatible) };
            using (var worker = StartWorker(rollbackTarget, staged, invalid))
            {
                await Wait(worker);
                Require(worker.ExitCode == 1, "An executable that cannot signal readiness fails the update.");
            }
            Require(ReadVersion(rollbackTarget) == older && originalConfig == File.ReadAllText(rollbackConfig) &&
                !Directory.GetFiles(rollbackFolder, ".xmlator-update-*").Any(), "A bad restart rolls back the original EXE and cleans the staged files.");
            Console.WriteLine("Rollback after failed restart and cleanup passed.");
        }
        finally
        {
            StopFixture(rollbackTarget);
            StopFixture(target);
            Directory.Delete(folder, recursive: true);
        }
    }

    internal static async Task CheckPublished(string installed, string candidate)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        var service = new UpdateService(installed, ReadVersion(installed), client);
        var release = await service.CheckAsync() ?? throw new InvalidOperationException("The public update was not offered.");
        string file = await service.DownloadAsync(release, null, default);
        try
        {
            string hash = Hash(await File.ReadAllBytesAsync(candidate));
            Require(release.Version == ReadVersion(candidate) && release.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase),
                "The public update exactly matches the locally verified binary.");
            Console.WriteLine($"Public GitHub asset {release.Version}, {release.Size} bytes, SHA-256 {hash}");
        }
        finally { File.Delete(file); }
    }

    private static Process StartWorker(string target, string stage, ReleaseUpdate update, Process? parent = null) =>
        Process.Start(UpdateInstaller.CreateStartInfo(target, stage, update, parent?.Id ?? int.MaxValue,
            parent?.StartTime.ToUniversalTime().Ticks ?? 0, "XMLator update test failed."))!;

    private static async Task Wait(Process process)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        await process.WaitForExitAsync(limit.Token);
    }

    private static int? FindRunning(string executable)
    {
        foreach (var process in Process.GetProcessesByName("XMLator"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        return process.Id;
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return null;
    }

    private static void StopFixture(string executable)
    {
        if (FindRunning(executable) is not int id)
            return;
        using var process = Process.GetProcessById(id);
        if (!process.HasExited)
        {
            process.Kill();
            process.WaitForExit();
        }
    }

    private static Version ReadVersion(string file) => UpdateService.VersionOf(FileVersionInfo.GetVersionInfo(file));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static HttpResponseMessage Response(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid update data was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Fixture(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(reply(request));
        }
    }

    private sealed class ProgressLog : IProgress<int>
    {
        internal List<int> Values { get; } = [];
        public void Report(int value) => Values.Add(value);
    }
}
