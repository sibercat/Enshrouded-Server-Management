using System.Diagnostics;
using System.Runtime.InteropServices;
using EnshroudedServerManager.Config;
using EnshroudedServerManager.Models;

namespace EnshroudedServerManager.Core;

public class ServerManager
{
    // ── Constants ─────────────────────────────────────────────────────────────
    public const string Version   = "1.0.0";
    public const string BuildDate = "2026-04-06";
    private const string ProcessName = "enshrouded_server";

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly ConfigManager _configManager;
    private readonly ServerLauncher _launcher;
    private Process? _serverProcess;

    private BackupManager?  _backupManager;
    private RestartManager? _restartManager;

    private string _serverVersion = "Unknown";
    private DateTime _lastVersionCheck = DateTime.MinValue;
    private static readonly TimeSpan VersionCheckInterval = TimeSpan.FromMinutes(5);

    // Crash detection — set true when we intentionally stop so we don't fire crash alert
    private bool _intentionalStop = false;
    public bool WasCrash { get; private set; } = false;

    // CPU tracking
    private DateTime  _lastCpuSample   = DateTime.MinValue;
    private TimeSpan  _lastCpuTime     = TimeSpan.Zero;
    private double    _lastCpuPercent  = 0;

    // ── Public surface ────────────────────────────────────────────────────────
    public ServerConfig Config => _configManager.Config;
    public string ServerVersion => _serverVersion;

    public BackupManager  BackupManager  => _backupManager  ??= new BackupManager(this);
    public RestartManager RestartManager => _restartManager ??= new RestartManager(this);

    public ServerManager(ConfigManager configManager)
    {
        _configManager = configManager;
        _launcher      = new ServerLauncher(configManager);
        TryUpdateServerVersion();
    }

    // ── IsRunning ─────────────────────────────────────────────────────────────
    public bool IsRunning()
    {
        try
        {
            var procs = Process.GetProcessesByName(ProcessName);
            return procs.Length > 0;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Error checking server status: {ex.Message}");
            return false;
        }
    }

    // ── Start ─────────────────────────────────────────────────────────────────
    public async Task<bool> StartAsync()
    {
        if (IsRunning())
        {
            AppLogger.Warning("Server is already running.");
            return false;
        }

        AppLogger.Info("Starting Enshrouded server...");
        _intentionalStop = false;   // clear flag so crash detection is active again
        var process = await _launcher.LaunchAsync();
        if (process == null || !IsRunning())
        {
            AppLogger.Error("Failed to start server.");
            return false;
        }

        _serverProcess = process;

        // Start managers if configured
        if (Config.AutoBackup.Enabled)
            BackupManager.Start();
        if (Config.AutoRestart)
            RestartManager.Start();

        _lastVersionCheck = DateTime.MinValue;
        TryUpdateServerVersion();
        AppLogger.Info("Server started successfully.");
        _ = DiscordService.SendAsync(Config.DiscordStatusWebhookUrl,
            $"✅ **{Config.ServerName}** — Server started.");
        return true;
    }

    // ── Stop ──────────────────────────────────────────────────────────────────
    public async Task<bool> StopAsync()
    {
        if (!IsRunning())
        {
            AppLogger.Warning("Server is not running.");
            return false;
        }

        AppLogger.Info("Stopping Enshrouded server...");
        _intentionalStop = true;
        WasCrash = false;

        _backupManager?.Stop();
        _restartManager?.Stop();

        // Graceful terminate first
        bool stopped = false;
        if (_serverProcess is { HasExited: false })
        {
            try
            {
                _serverProcess.CloseMainWindow();
                stopped = await WaitForExitAsync(_serverProcess, TimeSpan.FromSeconds(10));
            }
            catch { }
        }

        // Force kill if still running
        if (!stopped || IsRunning())
        {
            AppLogger.Warning("Graceful shutdown timed out — force killing.");
            try
            {
                await Task.Run(() =>
                    Process.Start(new ProcessStartInfo("taskkill", "/F /IM enshrouded_server.exe")
                    {
                        UseShellExecute = false,
                        CreateNoWindow  = true
                    })?.WaitForExit(5000));
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Error during force kill: {ex.Message}");
            }
        }

        // Wait for full termination (up to 30 s)
        var deadline = DateTime.Now.AddSeconds(30);
        while (IsRunning() && DateTime.Now < deadline)
            await Task.Delay(500);

        if (IsRunning())
        {
            AppLogger.Error("Server did not stop within timeout.");
            return false;
        }

        _serverProcess = null;
        AppLogger.Info("Server stopped successfully.");
        _ = DiscordService.SendAsync(Config.DiscordStatusWebhookUrl,
            $"🛑 **{Config.ServerName}** — Server stopped.");

        if (Config.AutoBackup.BackupOnShutdown)
        {
            AppLogger.Info("BackupOnShutdown enabled — creating backup...");
            await BackupManager.PerformBackupAsync();
        }

        return true;
    }

    // ── Restart ───────────────────────────────────────────────────────────────
    public async Task<bool> RestartAsync()
    {
        AppLogger.Info("Restarting Enshrouded server...");
        await StopAsync();
        await Task.Delay(3000);
        return await StartAsync();
    }

    // ── Update ────────────────────────────────────────────────────────────────
    public async Task<bool> UpdateAsync()
    {
        AppLogger.Info("Updating Enshrouded server via SteamCMD...");

        if (IsRunning() && !await StopAsync())
        {
            AppLogger.Error("Failed to stop server before update.");
            return false;
        }

        if (!await SteamCmdHelper.InstallAsync(Config.SteamcmdDir))
        {
            AppLogger.Error("Failed to install/verify SteamCMD.");
            return false;
        }

        var (valid, steamcmdExe) = SteamCmdHelper.Check(Config.SteamcmdDir);
        if (!valid)
        {
            AppLogger.Error("SteamCMD executable not found after install.");
            return false;
        }

        try
        {
            // Write a .bat that handles the "Missing configuration" first-run quirk with a
            // built-in retry — same pattern used by The Isle Evrima Server Launcher.
            var batPath = Path.Combine(Config.SteamcmdDir, "UpdateServer.bat");
            var steamArgs = $"+force_install_dir \"{Config.ServerDir}\" +login anonymous +app_update {Config.AppId} validate +quit";

            var bat = $"""
                @ECHO OFF
                TITLE SteamCMD - Enshrouded Update
                CD /D "{Config.SteamcmdDir}"
                steamcmd.exe {steamArgs}
                IF %ERRORLEVEL% NEQ 0 (
                    ECHO First attempt failed, retrying...
                    steamcmd.exe {steamArgs}
                )
                """;

            await File.WriteAllTextAsync(batPath, bat);
            AppLogger.Info($"Running update batch: {batPath}");

            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName         = "cmd.exe",
                Arguments        = $"/C \"{batPath}\"",
                WorkingDirectory = Config.SteamcmdDir,
                UseShellExecute  = true
            };

            proc.Start();
            await proc.WaitForExitAsync();

            var code = proc.ExitCode;
            if (code == 0)
            {
                AppLogger.Info("Server update completed successfully.");
                _lastVersionCheck = DateTime.MinValue;
                TryUpdateServerVersion();
                return true;
            }

            AppLogger.Error($"Update failed — SteamCMD exited with code {code}.");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Error during update: {ex.Message}");
            return false;
        }
    }

    // ── Backup ────────────────────────────────────────────────────────────────
    public Task<bool> BackupAsync() => BackupManager.PerformBackupAsync();

    // ── Shutdown (clean close of everything) ─────────────────────────────────
    public async Task ShutdownAsync()
    {
        AppLogger.Info("Initiating full shutdown sequence...");

        // Stop managers first
        _restartManager?.Shutdown();
        _backupManager?.Stop();

        // Stop the server so savegame files are released before we back them up
        if (IsRunning())
            await StopAsync();

        // Backup AFTER server is stopped — always use the property (creates lazily if null)
        // so BackupOnShutdown works even when AutoBackup.Enabled is false
        await BackupManager.ShutdownAsync();

        AppLogger.Info("Shutdown complete.");
    }

    // ── Performance metrics ───────────────────────────────────────────────────
    public (double CpuPercent, double MemoryMb) GetMetrics()
    {
        try
        {
            var procs = Process.GetProcessesByName(ProcessName);
            if (procs.Length == 0) return (0, 0);

            var proc = procs[0];
            double memMb = proc.WorkingSet64 / 1024.0 / 1024.0;

            var now       = DateTime.UtcNow;
            var cpuTime   = proc.TotalProcessorTime;
            double cpuPct = 0;

            if (_lastCpuSample != DateTime.MinValue)
            {
                var elapsed = (now - _lastCpuSample).TotalSeconds;
                var used    = (cpuTime - _lastCpuTime).TotalSeconds;
                if (elapsed > 0)
                    cpuPct = Math.Round(Math.Min(100, used / (elapsed * Environment.ProcessorCount) * 100), 1);
            }

            _lastCpuSample  = now;
            _lastCpuTime    = cpuTime;
            _lastCpuPercent = cpuPct;

            return (cpuPct, Math.Round(memMb, 1));
        }
        catch
        {
            return (0, 0);
        }
    }

    // ── Crash notification ────────────────────────────────────────────────────
    public async Task NotifyCrashIfUnexpectedAsync()
    {
        if (_intentionalStop) return;
        WasCrash = true;
        AppLogger.Warning("Server process disappeared unexpectedly — possible crash.");
        await DiscordService.SendAsync(Config.DiscordCrashWebhookUrl,
            $"💥 **{Config.ServerName}** — Server crashed or stopped unexpectedly!");

        if (Config.AutoRestartOnCrash)
        {
            var delay = Math.Max(1, Config.CrashRestartDelaySeconds);
            AppLogger.Info($"Auto Restart on Crash enabled — restarting in {delay} second(s)...");
            await DiscordService.SendAsync(Config.DiscordStatusWebhookUrl,
                $"🔄 **{Config.ServerName}** — Attempting automatic restart after crash in {delay}s...");

            await Task.Delay(TimeSpan.FromSeconds(delay));
            AppLogger.Info("Restarting server after crash...");
            await StartAsync();
        }
    }

    // ── Config save ───────────────────────────────────────────────────────────
    public bool SaveConfig()
    {
        bool ok = _configManager.Save();
        if (ok)
        {
            _backupManager?.UpdateFromConfig();
            _restartManager?.UpdateFromConfig();
        }
        return ok;
    }

    // ── Server version ────────────────────────────────────────────────────────
    private void TryUpdateServerVersion()
    {
        try
        {
            if (DateTime.Now - _lastVersionCheck < VersionCheckInterval) return;

            var logPath = Path.Combine(Config.ServerDir, "logs", "enshrouded_server.log");
            if (!File.Exists(logPath)) return;

            using var fs     = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Contains("Game Version (SVN):"))
                {
                    var v = line.Split("Game Version (SVN):")[1].Trim();
                    if (v != _serverVersion)
                    {
                        _serverVersion = v;
                        AppLogger.Info($"Server version detected: {v}");
                    }
                    break;
                }
            }

            _lastVersionCheck = DateTime.Now;
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Could not read server version: {ex.Message}");
        }
    }

    public void RefreshVersion()
    {
        _lastVersionCheck = DateTime.MinValue;
        TryUpdateServerVersion();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static async Task<bool> WaitForExitAsync(Process proc, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
            return proc.HasExited;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // ── Admin check ───────────────────────────────────────────────────────────
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity  = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
