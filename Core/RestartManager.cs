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

    /// <summary>
    /// Replaces the loop with a fresh one on an explicit retry delay. Only used
    /// when a restart we initiated failed: StopAsync already cancelled our token,
    /// so the current loop is unusable, but nothing spawned a successor.
    /// </summary>
    private void Rearm(TimeSpan retryIn)
    {
        if (!_server.Config.AutoRestart)
        {
            NextRestart = null;
            return;
        }

        _scheduledIntervalHours = _server.Config.RestartInterval;
        NextRestart = DateTime.Now.Add(retryIn);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loopTask = Task.Run(() => RunLoopAsync(token));
        AppLogger.Info($"Restart retry scheduled: {NextRestart:yyyy-MM-dd HH:mm:ss}");
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

                // A warning window at or beyond the interval would trip on the very
                // first pass and push the restart out by the whole window, so cap it
                // just under the interval.
                var intervalMinutes = Math.Max(1, _scheduledIntervalHours * 60);
                var warningMinutes  = Math.Clamp(_server.Config.RestartWarningMinutes, 0, intervalMinutes - 1);
                var warningTime     = TimeSpan.FromMinutes(warningMinutes);

                if (NextRestart.HasValue && now >= NextRestart.Value - warningTime)
                {
                    if (warningMinutes > 0)
                    {
                        AppLogger.Info($"Server restart in {warningMinutes} minute(s)...");
                        await DiscordService.SendAsync(_server.Config.DiscordStatusWebhookUrl,
                            $"⚠️ **{_server.Config.ServerName}** — Server restarting in {warningMinutes} minute(s).");

                        // Wait out the warning period
                        await Task.Delay(warningTime, ct);

                        if (ct.IsCancellationRequested) break;
                    }

                    AppLogger.Info("Executing scheduled restart...");
                    bool ok = await _server.RestartAsync();

                    // Either way our token is now cancelled — StopAsync calls Stop() on
                    // us on its way through — so this loop cannot continue past here.
                    if (ok)
                    {
                        // StartAsync called Start(), which spawned a replacement loop
                        // that owns the schedule from here on.
                        break;
                    }

                    // The restart failed, so StartAsync never reached Start() and no
                    // replacement loop exists. Re-arm with a fresh token rather than
                    // letting the manager die silently for the rest of the session.
                    if (_server.IsRunning())
                    {
                        AppLogger.Error("Scheduled restart failed but the server is still up — retrying in 5 minutes.");
                        // Include the warning window so players still get the full
                        // heads-up before the retry rather than an immediate one.
                        Rearm(TimeSpan.FromMinutes(5) + warningTime);
                    }
                    else
                    {
                        AppLogger.Error("Scheduled restart failed and the server is down — automatic restarts " +
                                        "stopped. Check the server logs, then start it manually.");
                        NextRestart = null;
                    }
                    break;
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
