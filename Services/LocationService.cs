using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NCDmvScraper.Configuration;
using System.Net.Http;

namespace NCDmvScraper.Services;

public interface ILocationService
{
    Task<bool> InitializeAsync();
    bool IsLocationAllowed(string locationAddress);
    HashSet<string> GetAllowedLocations();
    bool IsLocationFilteringEnabled();
}

public class LocationService : ILocationService
{
    private readonly FilterSettings _filterSettings;
    private readonly ScraperSettings _scraperSettings;
    private readonly ILogger<LocationService> _logger;
    private readonly HttpClient _httpClient;
    private HashSet<string> _allowedLocations = new();
    private bool _filteringEnabled = false;

    public LocationService(
        IOptions<FilterSettings> filterSettings,
        IOptions<ScraperSettings> scraperSettings,
        ILogger<LocationService> logger,
        HttpClient httpClient)
    {
        _filterSettings = filterSettings.Value;
        _scraperSettings = scraperSettings.Value;
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<bool> InitializeAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_filterSettings.UserAddress) || !_filterSettings.DistanceRangeMiles.HasValue)
            {
                _logger.LogInformation("Location filtering disabled - UserAddress or DistanceRangeMiles not set");
                return true;
            }

            if (_filterSettings.DistanceRangeMiles <= 0)
            {
                _logger.LogWarning("Distance range must be positive. Disabling location filtering.");
                return true;
            }

            var locationData = await LoadLocationDataAsync();
            if (locationData == null || !locationData.Any())
            {
                _logger.LogWarning("Could not load location data. Disabling location filtering.");
                return true;
            }

            var userCoordinates = await GeocodeAddressAsync(_filterSettings.UserAddress);
            if (userCoordinates == null)
            {
                _logger.LogWarning("Could not geocode user address. Disabling location filtering.");
                return true;
            }

            _allowedLocations = CalculateAllowedLocations(locationData, userCoordinates.Value, _filterSettings.DistanceRangeMiles.Value);
            _filteringEnabled = true;

            _logger.LogInformation("Location filtering enabled. Found {Count} locations within {Distance} miles of {Address}",
                _allowedLocations.Count, _filterSettings.DistanceRangeMiles, _filterSettings.UserAddress);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing location service. Disabling location filtering.");
            _filteringEnabled = false;
            return false;
        }
    }

    public bool IsLocationAllowed(string locationAddress)
    {
        if (!_filteringEnabled)
            return true;

        return _allowedLocations.Contains(locationAddress);
    }

    public HashSet<string> GetAllowedLocations() => _allowedLocations;

    public bool IsLocationFilteringEnabled() => _filteringEnabled;

    private async Task<List<LocationData>?> LoadLocationDataAsync()
    {
        try
        {
            string filePath = _scraperSettings.LocationDataFile;
            if (!File.Exists(filePath))
            {
                _logger.LogError("Location data file not found: {FilePath}", filePath);
                return null;
            }

            string json = await File.ReadAllTextAsync(filePath);
            var locationData = JsonConvert.DeserializeObject<List<LocationData>>(json);
            
            _logger.LogInformation("Loaded {Count} locations from {FilePath}", locationData?.Count ?? 0, filePath);
            return locationData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading location data from {FilePath}", _scraperSettings.LocationDataFile);
            return null;
        }
    }

    private async Task<(double Latitude, double Longitude)?> GeocodeAddressAsync(string address)
    {
        try
        {
            // Using a simple geocoding approach - in production you might want to use a more robust service
            // For now, we'll use a basic Nominatim-style approach
            var encodedAddress = Uri.EscapeDataString(address);
            var url = $"https://nominatim.openstreetmap.org/search?format=json&q={encodedAddress}&limit=1";
            
            _httpClient.DefaultRequestHeaders.UserAgent.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("NCDmvScraper/1.0");

            var response = await _httpClient.GetStringAsync(url);
            var results = JsonConvert.DeserializeObject<GeocodingResult[]>(response);

            if (results?.Length > 0)
            {
                var result = results[0];
                var lat = Convert.ToDouble(result.Lat);
                var lon = Convert.ToDouble(result.Lon);
                
                _logger.LogInformation("Geocoded address '{Address}' to coordinates ({Lat}, {Lon})", address, lat, lon);
                return (lat, lon);
            }

            _logger.LogWarning("Could not geocode address: {Address}", address);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error geocoding address: {Address}", address);
            return null;
        }
    }

    private HashSet<string> CalculateAllowedLocations(
        List<LocationData> locationData, 
        (double Latitude, double Longitude) userCoordinates, 
        decimal maxDistanceMiles)
    {
        var allowedLocations = new HashSet<string>();

        foreach (var location in locationData)
        {
            try
            {
                if (location.Coordinates?.Length != 2)
                {
                    _logger.LogWarning("Invalid coordinates for location: {Address}", location.Address);
                    continue;
                }

                var distance = CalculateDistanceInMiles(
                    userCoordinates.Latitude, userCoordinates.Longitude,
                    location.Coordinates[0], location.Coordinates[1]);

                if (distance <= (double)maxDistanceMiles)
                {
                    allowedLocations.Add(location.Address);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing location: {Address}", location.Address);
            }
        }

        return allowedLocations;
    }

    private static double CalculateDistanceInMiles(double lat1, double lon1, double lat2, double lon2)
    {
        // Haversine formula
        const double R = 3959; // Earth's radius in miles

        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        
        return R * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}

public class GeocodingResult
{
    [JsonProperty("lat")]
    public string Lat { get; set; } = string.Empty;
    
    [JsonProperty("lon")]
    public string Lon { get; set; } = string.Empty;
}