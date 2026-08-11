using System.Diagnostics;
using EnshroudedServerManager.Config;
using EnshroudedServerManager.Models;

namespace EnshroudedServerManager.Core;

public class ServerLauncher
{
    private readonly ConfigManager _configManager;
    private ServerConfig Config => _configManager.Config;

    public ServerLauncher(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    public async Task<Process?> LaunchAsync()
    {
        try
        {
            if (!Directory.Exists(Config.ServerDir))
            {
                AppLogger.Error($"Server directory not found: {Config.ServerDir}");
                return null;
            }

            // Check for the binary before touching enshrouded_server.json — writing
            // config (and generating group passwords) for a server that isn't
            // installed just leaves confusing state behind.
            var serverExe = Path.Combine(Config.ServerDir, "enshrouded_server.exe");
            if (!File.Exists(serverExe))
            {
                AppLogger.Error($"Server executable not found: {serverExe} — " +
                                "run 'Update Server' to install the server files.");
                return null;
            }

            if (!_configManager.UpdateServerJson(Config.ServerDir))
                AppLogger.Warning("Failed to update enshrouded_server.json — launching anyway.");

            // The dedicated server takes no documented command-line arguments —
            // all configuration lives in enshrouded_server.json.
            // An explicit null in server_config.json defeats the property initializer,
            // so don't dereference StartupParams directly.
            var args = Config.StartupParams?.Trim() ?? "";
            AppLogger.Info($"Launching: {serverExe}{(args.Length > 0 ? " " + args : "")}");

            var process = Process.Start(new ProcessStartInfo
            {
                FileName         = serverExe,
                Arguments        = args,
                WorkingDirectory = Config.ServerDir,
                UseShellExecute  = true
            });

            if (process == null)
            {
                AppLogger.Error("Process.Start returned null.");
                return null;
            }

            await Task.Delay(5000);

            if (process.HasExited)
            {
                AppLogger.Error($"Server process exited immediately (exit code {SafeExitCode(process)}).");
                AppLogger.Error("Check enshrouded_server.json — for example, every user group must have a non-empty password.");
                return null;
            }

            AppLogger.Info("Server launched successfully.");
            AppLogger.Info($"  Name      : {Config.ServerName}");
            AppLogger.Info($"  Players   : {Config.MaxPlayers}");
            AppLogger.Info($"  Preset    : {Config.GameSettingsPreset}");
            return process;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Error launching server: {ex.Message}");
            return null;
        }
    }

    private static string SafeExitCode(Process process)
    {
        try { return process.ExitCode.ToString(); }
        catch { return "unknown"; }
    }
}
