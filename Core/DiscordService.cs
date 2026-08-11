using System.Text;
using System.Text.Json;

namespace EnshroudedServerManager.Core;

public static class DiscordService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Sends a message to the configured Discord webhook. No-ops if URL is blank.
    /// </summary>
    public static async Task SendAsync(string webhookUrl, string message)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;

        try
        {
            var payload = JsonSerializer.Serialize(new { content = message });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(webhookUrl, content);

            if (!response.IsSuccessStatusCode)
                AppLogger.Warning($"Discord webhook returned {(int)response.StatusCode}: {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Discord webhook failed: {ex.Message}");
        }
    }
}
