using System.Diagnostics;
using System.Security.Cryptography;

namespace HappyBot.Update;

/// <summary>
/// Updates DANBOT.exe before the deck opens: fetch the signed manifest, download
/// and check the newer exe, swap it in at the same path (HidHide's whitelist
/// matches the full path) and relaunch. Every failure falls back to starting the
/// current version.
/// </summary>
internal static class AutoUpdater
{
    private const string ExeName = "DANBOT.exe";
    private const string RelaunchFlag = "--updated";
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Returns true when a newer DANBOT was started and this process should exit.</summary>
    public static bool UpdateAndRelaunch(string[] args)
    {
        // Only the published single-file build updates itself; dev builds run as HappyBot.exe.
        string exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !Path.GetFileName(exe).Equals(ExeName, StringComparison.OrdinalIgnoreCase))
            return false;
        DeleteQuietly(exe + ".old");

        // Right after an update, start normally even if the manifest still looks newer,
        // so a mislabeled release can never cause a restart loop.
        if (args.Contains(RelaunchFlag, StringComparer.OrdinalIgnoreCase)) return false;

        try
        {
            UpdateManifest manifest = FetchManifest();
            if (manifest == null || !manifest.IsNewerThan(BuildInfo.Version)) return false;

            string update = DownloadWithProgress(manifest);
            if (update == null || !SwapIn(exe, update)) return false;

            Process.Start(new ProcessStartInfo(exe, RelaunchFlag)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
            });
            return true;
        }
        catch (Exception ex)
        {
            // Offline, blocked, or a bad release: keep running the version we have.
            Trace.WriteLine("DANBOT update skipped: " + ex.Message);
            return false;
        }
    }

    private static UpdateManifest FetchManifest()
    {
        using var http = new HttpClient { Timeout = ManifestTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DANBOT/" + BuildInfo.Version);
        string token = http.GetStringAsync(UpdateChannel.ManifestUrl).GetAwaiter().GetResult();
        return UpdateManifest.Verify(token, UpdateChannel.PublicKey);
    }

    /// <summary>Downloads behind a small progress window; returns the checked file, or null.</summary>
    private static string DownloadWithProgress(UpdateManifest manifest)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string folder = Path.Combine(appData, "DANBOT", "updates");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, $"DANBOT-{manifest.Version}.exe");

        string result = null;
        using var window = new UpdateProgressForm(manifest.Version);
        window.Shown += async (_, _) =>
        {
            var progress = new Progress<double>(window.SetProgress);
            try
            {
                result = await Task.Run(() => Download(manifest, target, progress));
            }
            catch (Exception ex)
            {
                Trace.WriteLine("DANBOT update download failed: " + ex.Message);
            }
            window.Close();
        };
        Application.Run(window);
        return result;
    }

    private static string Download(UpdateManifest manifest, string target, IProgress<double> progress)
    {
        string partial = target + ".part";
        DeleteQuietly(partial);
        try
        {
            using var http = new HttpClient { Timeout = DownloadTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DANBOT/" + BuildInfo.Version);
            using HttpResponseMessage response = http
                .GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            using (Stream source = response.Content.ReadAsStream())
            using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > manifest.Size) throw new InvalidDataException("The download is larger than the release.");
                    file.Write(buffer, 0, read);
                    hash.AppendData(buffer, 0, read);
                    progress.Report((double)total / manifest.Size);
                }
                string sha256 = Convert.ToHexString(hash.GetHashAndReset());
                if (total != manifest.Size || !sha256.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download does not match the signed release.");
            }

            DeleteQuietly(target);
            File.Move(partial, target);
            return target;
        }
        catch
        {
            DeleteQuietly(partial);
            throw;
        }
    }

    /// <summary>
    /// Renames the running exe aside (Windows allows that, not deleting it) and moves the
    /// update into its place. Restores the original on failure.
    /// </summary>
    private static bool SwapIn(string exe, string update)
    {
        string old = exe + ".old";
        DeleteQuietly(old);
        try
        {
            File.Move(exe, old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // e.g. the exe sits in a folder this user cannot write
        }

        try
        {
            File.Move(update, exe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Move(old, exe); }
            catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine("DANBOT could not restore " + exe + ": " + restore.Message);
            }
            return false;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still locked by the previous process; the next start tries again.
        }
    }
}
