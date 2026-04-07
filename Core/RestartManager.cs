namespace EnshroudedServerManager.Core;

public class RestartManager
{
    private readonly ServerManager _server;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public DateTime? NextRestart { get; private set; }

    public RestartManager(ServerManager server)
    {
        _server = server;
    }

    public void Start()
    {
        if (_loopTask is { IsCompleted: false })
        {
            AppLogger.Info("Restart manager already running.");
            return;
        }

        ScheduleNext();
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        AppLogger.Info("Restart manager started.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        AppLogger.Info("Restart manager stop requested.");
    }

    public void UpdateFromConfig()
    {
        if (_server.Config.AutoRestart)
        {
            if (_loopTask is null or { IsCompleted: true })
                Start();
            else
                ScheduleNext();
        }
        else
        {
            Stop();
            NextRestart = null;
        }
    }

    public void Shutdown() => Stop();

    private void ScheduleNext()
    {
        NextRestart = DateTime.Now.AddHours(_server.Config.RestartInterval);
        AppLogger.Info($"Next restart scheduled: {NextRestart:yyyy-MM-dd HH:mm:ss}");
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_server.IsRunning())
                {
                    AppLogger.Info("Server stopped — restart manager exiting.");
                    break;
                }

                var now = DateTime.Now;
                var warningTime = TimeSpan.FromMinutes(_server.Config.RestartWarningMinutes);

                if (NextRestart.HasValue && now >= NextRestart.Value - warningTime)
                {
                    AppLogger.Info($"Server restart in {_server.Config.RestartWarningMinutes} minute(s)...");
                    await DiscordService.SendAsync(_server.Config.DiscordStatusWebhookUrl,
                        $"⚠️ **{_server.Config.ServerName}** — Server restarting in {_server.Config.RestartWarningMinutes} minute(s).");

                    // Wait out the warning period
                    await Task.Delay(warningTime, ct);

                    if (ct.IsCancellationRequested) break;

                    AppLogger.Info("Executing scheduled restart...");
                    bool ok = await _server.RestartAsync();
                    if (ok)
                    {
                        ScheduleNext();
                    }
                    else
                    {
                        AppLogger.Error("Scheduled restart failed — retrying in 5 minutes.");
                        NextRestart = DateTime.Now.AddMinutes(5);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Error in restart loop: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            }
        }

        AppLogger.Info("Restart manager stopped.");
    }
}
