using System.IO;

namespace EnshroudedServerManager.Core;

public static class AppLogger
{
    private static StreamWriter? _fileWriter;
    private static readonly object _lock = new();
    private static Action<string>? _uiCallback;

    public static void Initialize(string logDir = "logs")
    {
        try
        {
            Directory.CreateDirectory(logDir);
            _fileWriter = new StreamWriter(
                Path.Combine(logDir, "server_manager.log"),
                append: true)
            { AutoFlush = true };
        }
        catch
        {
            // If log dir fails, we still run — just no file logging
        }
    }

    /// <summary>Register a callback to forward log lines to the UI console.</summary>
    public static void SetUiCallback(Action<string> callback) => _uiCallback = callback;

    public static void Info(string message)    => Log("INFO ", message);
    public static void Warning(string message) => Log("WARN ", message);
    public static void Error(string message)   => Log("ERROR", message);
    public static void Debug(string message)   => Log("DEBUG", message);

    private static void Log(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (_lock)
        {
            try { _fileWriter?.WriteLine(line); } catch { }
            _uiCallback?.Invoke(line);
        }
    }

    public static void Shutdown()
    {
        lock (_lock)
        {
            _fileWriter?.Flush();
            _fileWriter?.Dispose();
            _fileWriter = null;
        }
    }
}
