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

    private int _scheduledIntervalHours;

    public void Start()
    {
        // A loop whose token is already cancelled counts as stopped even if the
        // task hasn't unwound yet — this happens when the loop itself triggers a
        // restart (RestartAsync → StopAsync cancels us → StartAsync calls Start()
        // while the old loop is still on the stack). Spin up a fresh loop; the
        // old one exits on its cancelled token.
        if (_loopTask is { IsCompleted: false } && _cts is { IsCancellationRequested: false })
        {
            AppLogger.Info("Restart manager already running.");
            return;
        }

        ScheduleNext();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loopTask = Task.Run(() => RunLoopAsync(token));
        AppLogger.Info("Restart manager started.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        NextRestart = null;
        AppLogger.Info("Restart manager stop requested.");
    }

    public void UpdateFromConfig()
    {
        if (_server.Config.AutoRestart)
        {
            if (_loopTask is null or { IsCompleted: true } || _cts is null or { IsCancellationRequested: true })
            {
                if (_server.IsRunning())
                    Start();
            }
            else if (_server.Config.RestartInterval != _scheduledIntervalHours)
            {
                ScheduleNext(); // interval changed — recompute; otherwise keep the current schedule
            }
        }
        else
        {
            Stop();
        }
    }

    public void Shutdown() => Stop();

    private void ScheduleNext()
    {
        _scheduledIntervalHours = _server.Config.RestartInterval;
        NextRestart = DateTime.Now.AddHours(_scheduledIntervalHours);
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
                    NextRestart = null;
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

                    // RestartAsync cycles this manager (StopAsync cancels our token,
                    // StartAsync spawns a replacement loop that owns the schedule now).
                    if (ct.IsCancellationRequested) break;

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
