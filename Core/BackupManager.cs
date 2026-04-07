using System.IO.Compression;
using EnshroudedServerManager.Models;

namespace EnshroudedServerManager.Core;

public class BackupManager
{
    private readonly ServerManager _server;
    private ServerConfig Config => _server.Config;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public DateTime? NextBackup { get; private set; }

    public BackupManager(ServerManager server)
    {
        _server = server;
    }

    public void Start()
    {
        if (_loopTask is { IsCompleted: false })
        {
            AppLogger.Info("Backup manager already running.");
            return;
        }

        ScheduleNext();
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        AppLogger.Info("Backup manager started.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        AppLogger.Info("Backup manager stop requested.");
    }

    public void UpdateFromConfig()
    {
        if (Config.AutoBackup.Enabled)
        {
            if (_loopTask is null or { IsCompleted: true })
                Start();
            else
                ScheduleNext(); // Recalculate interval if already running
        }
        else
        {
            Stop();
            NextBackup = null;
        }
    }

    public async Task ShutdownAsync()
    {
        if (Config.AutoBackup.BackupOnShutdown)
        {
            AppLogger.Info("Performing shutdown backup...");
            await PerformBackupAsync();
        }
        Stop();
        if (_loopTask != null)
            await Task.WhenAny(_loopTask, Task.Delay(10_000));
    }

    private void ScheduleNext()
    {
        if (Config.AutoBackup.Enabled)
        {
            NextBackup = DateTime.Now.AddMinutes(Config.AutoBackup.IntervalMinutes);
            AppLogger.Info($"Next backup scheduled: {NextBackup:yyyy-MM-dd HH:mm:ss}");
        }
        else
        {
            NextBackup = null;
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var lastCleanupCheck = DateTime.Now;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_server.IsRunning())
                {
                    AppLogger.Info("Server stopped — backup manager exiting.");
                    break;
                }

                var now = DateTime.Now;

                if (NextBackup.HasValue && now >= NextBackup.Value)
                {
                    // Daily cleanup check
                    if (now - lastCleanupCheck > TimeSpan.FromDays(1))
                    {
                        CleanupOldBackups();
                        lastCleanupCheck = now;
                    }

                    bool ok = await PerformBackupAsync();
                    NextBackup = ok
                        ? DateTime.Now.AddMinutes(Config.AutoBackup.IntervalMinutes)
                        : DateTime.Now.AddMinutes(5); // retry sooner on failure

                    AppLogger.Info($"Next backup scheduled: {NextBackup:yyyy-MM-dd HH:mm:ss}");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Error in backup loop: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            }
        }

        AppLogger.Info("Backup manager stopped.");
    }

    public async Task<bool> PerformBackupAsync()
    {
        try
        {
            var backupDir = Config.BackupDir;
            Directory.CreateDirectory(backupDir);

            var saveDir = Path.Combine(Config.ServerDir, "savegame");
            if (!Directory.Exists(saveDir))
            {
                AppLogger.Error($"Save directory not found: {saveDir}");
                return false;
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupPath = Path.Combine(backupDir, $"enshrouded_backup_{timestamp}.zip");

            AppLogger.Info($"Creating backup: {backupPath}");

            await Task.Run(() =>
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);

                ZipFile.CreateFromDirectory(saveDir, backupPath, CompressionLevel.Optimal, false);
            });

            AppLogger.Info($"Backup created successfully: {backupPath}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Backup failed: {ex.Message}");
            return false;
        }
    }

    private void CleanupOldBackups()
    {
        try
        {
            var backupDir = Config.BackupDir;
            if (!Directory.Exists(backupDir)) return;

            var cutoff = DateTime.Now.AddDays(-Config.AutoBackup.KeepDays);
            int deleted = 0, failed = 0;

            foreach (var file in Directory.GetFiles(backupDir, "enshrouded_backup_*.zip"))
            {
                try
                {
                    // Parse date from filename: enshrouded_backup_YYYYMMDD_HHMMSS.zip
                    var stem = Path.GetFileNameWithoutExtension(file);
                    var parts = stem.Split('_');
                    if (parts.Length >= 4)
                    {
                        var dateStr = $"{parts[2]}_{parts[3]}";
                        if (DateTime.TryParseExact(dateStr, "yyyyMMdd_HHmmss",
                            null, System.Globalization.DateTimeStyles.None, out var fileDate))
                        {
                            if (fileDate < cutoff)
                            {
                                File.Delete(file);
                                deleted++;
                                AppLogger.Info($"Deleted old backup: {Path.GetFileName(file)}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warning($"Could not process backup file {Path.GetFileName(file)}: {ex.Message}");
                    failed++;
                }
            }

            if (deleted > 0 || failed > 0)
                AppLogger.Info($"Cleanup complete: {deleted} deleted, {failed} failed.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Error during backup cleanup: {ex.Message}");
        }
    }
}
