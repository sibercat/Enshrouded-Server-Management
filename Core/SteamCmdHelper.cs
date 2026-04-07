using System.IO.Compression;

namespace EnshroudedServerManager.Core;

public static class SteamCmdHelper
{
    private const string SteamCmdWindowsUrl =
        "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

    /// <summary>
    /// Ensure SteamCMD is installed in the given directory.
    /// Downloads and extracts if not present.
    /// </summary>
    public static async Task<bool> InstallAsync(string installDir)
    {
        try
        {
            if (!Directory.Exists(installDir))
                Directory.CreateDirectory(installDir);

            var exe = Path.Combine(installDir, "steamcmd.exe");
            if (File.Exists(exe))
            {
                AppLogger.Info("SteamCMD already installed.");
                return true;
            }

            AppLogger.Info("SteamCMD not found. Downloading...");

            var zipPath = Path.Combine(installDir, "steamcmd.zip");

            using (var client = new HttpClient())
            using (var response = await client.GetAsync(SteamCmdWindowsUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var file = File.Create(zipPath);
                await stream.CopyToAsync(file);
            }

            ZipFile.ExtractToDirectory(zipPath, installDir, overwriteFiles: true);
            File.Delete(zipPath);

            AppLogger.Info("Running SteamCMD first-time update...");
            await RunProcessAsync(exe, "+quit", installDir);

            AppLogger.Info("SteamCMD installed successfully.");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to install SteamCMD: {ex.Message}");
            return false;
        }
    }

    /// <summary>Returns (isValid, fullExePath).</summary>
    public static (bool Valid, string Path) Check(string installDir)
    {
        var exe = Path.Combine(installDir, "steamcmd.exe");
        return File.Exists(exe) ? (true, exe) : (false, string.Empty);
    }

    private static async Task RunProcessAsync(string exe, string args, string workingDir)
    {
        using var proc = new System.Diagnostics.Process();
        proc.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName         = exe,
            Arguments        = args,
            WorkingDirectory = workingDir,
            UseShellExecute  = true
        };
        proc.Start();
        await proc.WaitForExitAsync();
    }
}
