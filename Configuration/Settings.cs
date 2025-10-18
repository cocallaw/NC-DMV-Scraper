namespace NCDmvScraper.Configuration;

public class ScraperSettings
{
    public string BaseUrl { get; set; } = "https://skiptheline.ncdot.gov";
    public string AppointmentType { get; set; } = "Driver License - First Time";
    public int BaseIntervalMinutes { get; set; } = 10;
    public int MinRandomDelaySeconds { get; set; } = 10;
    public int MaxRandomDelaySeconds { get; set; } = 30;
    public int TimeoutSeconds { get; set; } = 90;
    public bool ProofOfLife { get; set; } = false;
    public string LocationDataFile { get; set; } = "ncdot_locations_coordinates_only.json";
}

public class NotificationSettings
{
    public string Type { get; set; } = "discord"; // discord, slack
    public string DiscordWebhookUrl { get; set; } = string.Empty;
    public string SlackWebhookUrl { get; set; } = string.Empty;
    public string IntroMessage { get; set; } = string.Empty;
    public int MaxDiscordMessageLength { get; set; } = 1950;
    public int MaxSlackMessageLength { get; set; } = 3900;
}

public class FilterSettings
{
    public string? UserAddress { get; set; }
    public decimal? DistanceRangeMiles { get; set; }
    
    // Date filtering
    public DateTime? DateRangeStart { get; set; }
    public DateTime? DateRangeEnd { get; set; }
    public string? DateRangeRelative { get; set; } // e.g., "2w", "3m", "30d"
    
    // Time filtering
    public TimeOnly? TimeRangeStart { get; set; }
    public TimeOnly? TimeRangeEnd { get; set; }
}

public class LocationData
{
    public string Address { get; set; } = string.Empty;
    public double[] Coordinates { get; set; } = Array.Empty<double>();
}

public class AppointmentResult
{
    public string LocationName { get; set; } = string.Empty;
    public string LocationAddress { get; set; } = string.Empty;
    public List<DateTime> AvailableTimes { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public bool IsError { get; set; }
}