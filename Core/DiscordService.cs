using System.Text;
using System.Text.Json;

namespace EnshroudedServerManager.Core;

public static class DiscordService
{
    /// <summary>
    /// Sends a message to the configured Discord webhook. No-ops if URL is blank.
    /// </summary>
    public static async Task SendAsync(string webhookUrl, string message)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;

        try
        {
            var payload = JsonSerializer.Serialize(new { content = message });
            using var http    = new HttpClient();
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await http.PostAsync(webhookUrl, content);

            if (!response.IsSuccessStatusCode)
                AppLogger.Warning($"Discord webhook returned {(int)response.StatusCode}: {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Discord webhook failed: {ex.Message}");
        }
    }
}
