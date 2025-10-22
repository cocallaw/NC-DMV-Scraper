using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NCDmvScraper.Configuration;
using System.Text;

namespace NCDmvScraper.Services;

public interface INotificationService
{
    Task SendNotificationAsync(Dictionary<string, AppointmentResult> results);
    Task SendProofOfLifeAsync();
}

public class NotificationService : INotificationService
{
    private readonly NotificationSettings _notificationSettings;
    private readonly ScraperSettings _scraperSettings;
    private readonly ILogger<NotificationService> _logger;
    private readonly HttpClient _httpClient;

    public NotificationService(
        IOptions<NotificationSettings> notificationSettings,
        IOptions<ScraperSettings> scraperSettings,
        ILogger<NotificationService> logger,
        HttpClient httpClient)
    {
        _notificationSettings = notificationSettings.Value;
        _scraperSettings = scraperSettings.Value;
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task SendNotificationAsync(Dictionary<string, AppointmentResult> results)
    {
        var validResults = results.Where(r => !r.Value.IsError && r.Value.AvailableTimes.Any()).ToList();

        if (!validResults.Any())
        {
            if (_scraperSettings.ProofOfLife)
            {
                await SendProofOfLifeAsync();
            }
            return;
        }

        var message = FormatResultsForNotification(validResults);
        
        switch (_notificationSettings.Type.ToLower())
        {
            case "slack":
                await SendSlackNotificationAsync(message);
                break;
            case "discord":
            default:
                await SendDiscordNotificationAsync(message);
                break;
        }
    }

    public async Task SendProofOfLifeAsync()
    {
        var message = "No valid appointments found at this time";
        
        switch (_notificationSettings.Type.ToLower())
        {
            case "slack":
                await SendSlackNotificationAsync(message, isProofOfLife: true);
                break;
            case "discord":
            default:
                await SendDiscordNotificationAsync(message, isProofOfLife: true);
                break;
        }
    }

    private string FormatResultsForNotification(List<KeyValuePair<string, AppointmentResult>> validResults)
    {
        var messageLines = new List<string>();
        var isSlack = _notificationSettings.Type.ToLower() == "slack";

        foreach (var (locationName, result) in validResults)
        {
            // Format location header based on notification type
            if (isSlack)
            {
                messageLines.Add($"\n*Location: {locationName}*");
            }
            else
            {
                messageLines.Add($"\n**Location: {locationName}**");
            }

            // Add appointment times
            foreach (var appointmentTime in result.AvailableTimes.OrderBy(t => t))
            {
                var formattedTime = appointmentTime.ToString("M/d/yyyy h:mm:ss tt");
                if (isSlack)
                {
                    messageLines.Add($"• {formattedTime}");
                }
                else
                {
                    messageLines.Add($"- {formattedTime}");
                }
            }
        }

        return string.Join("\n", messageLines);
    }

    private async Task SendDiscordNotificationAsync(string message, bool isProofOfLife = false)
    {
        if (string.IsNullOrEmpty(_notificationSettings.DiscordWebhookUrl))
        {
            _logger.LogWarning("Discord webhook URL not configured. Skipping notification.");
            return;
        }

        try
        {
            string fullMessage;
            if (isProofOfLife)
            {
                fullMessage = message;
            }
            else
            {
                var introMessage = string.IsNullOrEmpty(_notificationSettings.IntroMessage)
                    ? $"@everyone Appointments available at {_scraperSettings.BaseUrl}:\n"
                    : _notificationSettings.IntroMessage;
                fullMessage = introMessage + message;
            }

            // Handle ntfy.sh special case
            if (_notificationSettings.DiscordWebhookUrl.Contains("ntfy.sh"))
            {
                await SendNtfyNotificationAsync(fullMessage);
                return;
            }

            var chunks = ChunkMessage(fullMessage, _notificationSettings.MaxDiscordMessageLength);
            
            _logger.LogInformation("Sending Discord notification in {ChunkCount} chunk(s)", chunks.Count);

            for (int i = 0; i < chunks.Count; i++)
            {
                var payload = new { content = chunks[i] };
                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(_notificationSettings.DiscordWebhookUrl, content);
                response.EnsureSuccessStatusCode();
                
                _logger.LogInformation("Discord notification chunk {ChunkNumber}/{Total} sent successfully", i + 1, chunks.Count);
                
                if (i < chunks.Count - 1)
                {
                    await Task.Delay(1000); // Rate limiting
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending Discord notification");
        }
    }

    private async Task SendSlackNotificationAsync(string message, bool isProofOfLife = false)
    {
        if (string.IsNullOrEmpty(_notificationSettings.SlackWebhookUrl))
        {
            _logger.LogWarning("Slack webhook URL not configured. Skipping notification.");
            return;
        }

        try
        {
            string fullMessage;
            if (isProofOfLife)
            {
                fullMessage = message;
            }
            else
            {
                var introMessage = string.IsNullOrEmpty(_notificationSettings.IntroMessage)
                    ? $"<!channel> Appointments available at {_scraperSettings.BaseUrl}:\n"
                    : _notificationSettings.IntroMessage;
                fullMessage = introMessage + message;
            }

            var chunks = ChunkMessage(fullMessage, _notificationSettings.MaxSlackMessageLength);
            
            _logger.LogInformation("Sending Slack notification in {ChunkCount} chunk(s)", chunks.Count);
            _logger.LogDebug("Full message content: {Message}", fullMessage);

            for (int i = 0; i < chunks.Count; i++)
            {
                var payload = new SlackPayload
                {
                    Text = chunks[i],
                    Username = "NC DMV Bot",
                    IconEmoji = ":car:",
                    UnfurlLinks = false,
                    UnfurlMedia = false
                };

                var json = JsonConvert.SerializeObject(payload);
                _logger.LogDebug("Sending Slack payload: {Payload}", json);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(_notificationSettings.SlackWebhookUrl, content);
                
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Slack API returned {StatusCode}: {ErrorContent}. Payload was: {Payload}", 
                        response.StatusCode, errorContent, json);
                }
                
                response.EnsureSuccessStatusCode();
                
                _logger.LogInformation("Slack notification chunk {ChunkNumber}/{Total} sent successfully", i + 1, chunks.Count);
                
                if (i < chunks.Count - 1)
                {
                    await Task.Delay(1000); // Rate limiting
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending Slack notification");
        }
    }

    private async Task SendNtfyNotificationAsync(string message)
    {
        try
        {
            var content = new StringContent(message, Encoding.UTF8, "text/plain");
            content.Headers.Add("Markdown", "yes");

            var response = await _httpClient.PostAsync(_notificationSettings.DiscordWebhookUrl, content);
            response.EnsureSuccessStatusCode();
            
            _logger.LogInformation("ntfy notification sent successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending ntfy notification");
        }
    }

    private static List<string> ChunkMessage(string message, int maxLength)
    {
        var chunks = new List<string>();
        var remaining = message;

        while (remaining.Length > 0)
        {
            if (remaining.Length <= maxLength)
            {
                chunks.Add(remaining);
                break;
            }

            var splitIndex = remaining.LastIndexOf('\n', maxLength);
            if (splitIndex == -1)
            {
                splitIndex = maxLength;
            }

            chunks.Add(remaining[..splitIndex]);
            remaining = remaining[splitIndex..].TrimStart();

            if (splitIndex == maxLength && remaining.Length > 0)
            {
                chunks[^1] += "\n... (continued in next message)";
            }
        }

        return chunks;
    }
}

public class SlackPayload
{
    [JsonProperty("text")]
    public string Text { get; set; } = string.Empty;
    
    [JsonProperty("username")]
    public string Username { get; set; } = string.Empty;
    
    [JsonProperty("icon_emoji")]
    public string IconEmoji { get; set; } = string.Empty;
    
    [JsonProperty("unfurl_links")]
    public bool UnfurlLinks { get; set; }
    
    [JsonProperty("unfurl_media")]
    public bool UnfurlMedia { get; set; }
}