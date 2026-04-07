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

            if (!_configManager.UpdateServerJson(Config.ServerDir))
                AppLogger.Warning("Failed to update enshrouded_server.json — launching anyway.");

            var serverExe = Path.Combine(Config.ServerDir, "enshrouded_server.exe");
            if (!File.Exists(serverExe))
            {
                AppLogger.Error($"Server executable not found: {serverExe}");
                return null;
            }

            var args = BuildStartupArgs();
            AppLogger.Info($"Launching: {serverExe} {args}");

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

            AppLogger.Info("Server launched successfully.");
            AppLogger.Info($"  Name      : {Config.ServerName}");
            AppLogger.Info($"  Players   : {Config.MaxPlayers}");
            AppLogger.Info($"  Game Port : {Config.GamePort}");
            AppLogger.Info($"  Preset    : {Config.GameSettingsPreset}");
            return process;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Error launching server: {ex.Message}");
            return null;
        }
    }

    private string BuildStartupArgs()
    {
        var parts = new List<string>
        {
            $"-servername \"{Config.ServerName}\"",
            $"-gameport {Config.GamePort}",
            $"-queryport {Config.QueryPort}",
            $"-maxplayers {Config.MaxPlayers}",
            $"-maxfps {Config.MaxFps}",
            $"-tickrate {Config.TickRate}"
        };

        if (Config.PvpEnabled)
            parts.Add("-pvp");

        if (!string.IsNullOrWhiteSpace(Config.StartupParams))
            parts.Add(Config.StartupParams);

        return string.Join(" ", parts);
    }
}
