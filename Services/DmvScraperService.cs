using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using NCDmvScraper.Configuration;

namespace NCDmvScraper.Services;

public interface IDmvScraperService
{
    Task StartAsync();
}

public class DmvScraperService : IDmvScraperService
{
    private readonly ScraperSettings _scraperSettings;
    private readonly NotificationSettings _notificationSettings;
    private readonly ILocationService _locationService;
    private readonly IWebScrapingService _webScrapingService;
    private readonly INotificationService _notificationService;
    private readonly ILogger<DmvScraperService> _logger;
    private readonly Random _random = new();

    public DmvScraperService(
        IOptions<ScraperSettings> scraperSettings,
        IOptions<NotificationSettings> notificationSettings,
        ILocationService locationService,
        IWebScrapingService webScrapingService,
        INotificationService notificationService,
        ILogger<DmvScraperService> logger)
    {
        _scraperSettings = scraperSettings.Value;
        _notificationSettings = notificationSettings.Value;
        _locationService = locationService;
        _webScrapingService = webScrapingService;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task StartAsync()
    {
        _logger.LogInformation("NC DMV Scraper Service starting...");
        
        // Validate configuration
        if (!ValidateConfiguration())
        {
            _logger.LogCritical("Configuration validation failed. Exiting.");
            return;
        }

        // Initialize location service
        await _locationService.InitializeAsync();

        _logger.LogInformation("Starting continuous scraping loop...");
        _logger.LogInformation("Configuration:");
        _logger.LogInformation("  - Base URL: {BaseUrl}", _scraperSettings.BaseUrl);
        _logger.LogInformation("  - Appointment Type: {AppointmentType}", _scraperSettings.AppointmentType);
        _logger.LogInformation("  - Interval: {Interval} minutes", _scraperSettings.BaseIntervalMinutes);
        _logger.LogInformation("  - Notification Type: {NotificationType}", _notificationSettings.Type);
        _logger.LogInformation("  - Location Filtering: {LocationFiltering}", 
            _locationService.IsLocationFilteringEnabled() ? "Enabled" : "Disabled");

        while (true)
        {
            var runStartTime = DateTime.Now;
            _logger.LogInformation("--- Starting scraping run at {Time} ---", runStartTime.ToString("yyyy-MM-dd HH:mm:ss"));

            try
            {
                // Scrape appointments
                var results = await _webScrapingService.ScrapeAppointmentsAsync();
                
                // Log results summary
                LogResultsSummary(results);

                // Send notifications
                await _notificationService.SendNotificationAsync(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during scraping run");
            }

            // Calculate sleep time with random delay
            var baseSleepMs = _scraperSettings.BaseIntervalMinutes * 60 * 1000;
            var randomDelayMs = _random.Next(
                _scraperSettings.MinRandomDelaySeconds * 1000,
                _scraperSettings.MaxRandomDelaySeconds * 1000);
            var totalSleepMs = baseSleepMs + randomDelayMs;

            var nextRunTime = DateTime.Now.AddMilliseconds(totalSleepMs);
            _logger.LogInformation("--- Run finished. Next run scheduled for {NextRunTime} (sleeping for {Minutes}m {Seconds}s) ---",
                nextRunTime.ToString("yyyy-MM-dd HH:mm:ss"),
                totalSleepMs / 60000,
                (totalSleepMs % 60000) / 1000);

            try
            {
                await Task.Delay(totalSleepMs);
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("Scraping service was cancelled.");
                break;
            }
        }
    }

    private bool ValidateConfiguration()
    {
        var isValid = true;

        // Validate scraper settings
        if (string.IsNullOrEmpty(_scraperSettings.BaseUrl))
        {
            _logger.LogError("BaseUrl is not configured");
            isValid = false;
        }

        if (string.IsNullOrEmpty(_scraperSettings.AppointmentType))
        {
            _logger.LogError("AppointmentType is not configured");
            isValid = false;
        }

        if (_scraperSettings.BaseIntervalMinutes <= 0)
        {
            _logger.LogError("BaseIntervalMinutes must be greater than 0");
            isValid = false;
        }

        // Validate notification settings
        var notificationType = _notificationSettings.Type.ToLower();
        if (notificationType == "discord")
        {
            if (string.IsNullOrEmpty(_notificationSettings.DiscordWebhookUrl))
            {
                _logger.LogWarning("Discord webhook URL is not configured. Notifications will be skipped.");
                _logger.LogInformation("Set the DiscordWebhookUrl in configuration or NOTIFICATION_SETTINGS__DISCORDWEBHOOKURL environment variable.");
            }
        }
        else if (notificationType == "slack")
        {
            if (string.IsNullOrEmpty(_notificationSettings.SlackWebhookUrl))
            {
                _logger.LogWarning("Slack webhook URL is not configured. Notifications will be skipped.");
                _logger.LogInformation("Set the SlackWebhookUrl in configuration or NOTIFICATION_SETTINGS__SLACKWEBHOOKURL environment variable.");
            }
        }
        else
        {
            _logger.LogWarning("Unknown notification type '{NotificationType}'. Defaulting to Discord.", notificationType);
        }

        // Validate location data file
        if (!File.Exists(_scraperSettings.LocationDataFile))
        {
            _logger.LogWarning("Location data file not found: {LocationDataFile}. Location filtering will be disabled.", 
                _scraperSettings.LocationDataFile);
        }

        return isValid;
    }

    private void LogResultsSummary(Dictionary<string, AppointmentResult> results)
    {
        var totalLocations = results.Count;
        var locationsWithAppointments = results.Count(r => !r.Value.IsError && r.Value.AvailableTimes.Any());
        var locationsWithErrors = results.Count(r => r.Value.IsError);
        var totalAppointments = results.Where(r => !r.Value.IsError).Sum(r => r.Value.AvailableTimes.Count);

        _logger.LogInformation("Scraping results summary:");
        _logger.LogInformation("  - Total locations processed: {TotalLocations}", totalLocations);
        _logger.LogInformation("  - Locations with appointments: {LocationsWithAppointments}", locationsWithAppointments);
        _logger.LogInformation("  - Locations with errors: {LocationsWithErrors}", locationsWithErrors);
        _logger.LogInformation("  - Total appointments found: {TotalAppointments}", totalAppointments);

        if (locationsWithAppointments > 0)
        {
            _logger.LogInformation("Locations with available appointments:");
            foreach (var result in results.Where(r => !r.Value.IsError && r.Value.AvailableTimes.Any()))
            {
                _logger.LogInformation("  - {LocationName}: {AppointmentCount} appointments", 
                    result.Key, result.Value.AvailableTimes.Count);
                
                // Log first few appointment times
                var firstFewTimes = result.Value.AvailableTimes.Take(3).Select(t => t.ToString("M/d/yyyy h:mm tt"));
                _logger.LogDebug("    Times: {Times}{More}", 
                    string.Join(", ", firstFewTimes),
                    result.Value.AvailableTimes.Count > 3 ? "..." : "");
            }
        }

        if (locationsWithErrors > 0)
        {
            _logger.LogWarning("Locations with errors:");
            foreach (var result in results.Where(r => r.Value.IsError))
            {
                _logger.LogWarning("  - {LocationName}: {Error}", result.Key, result.Value.ErrorMessage);
            }
        }
    }
}