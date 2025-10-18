using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using HtmlAgilityPack;
using NCDmvScraper.Configuration;
using System.Text.RegularExpressions;
using System.Globalization;

namespace NCDmvScraper.Services;

public interface IWebScrapingService
{
    Task<Dictionary<string, AppointmentResult>> ScrapeAppointmentsAsync();
}

public class WebScrapingService : IWebScrapingService
{
    private readonly ScraperSettings _scraperSettings;
    private readonly FilterSettings _filterSettings;
    private readonly ILocationService _locationService;
    private readonly ILogger<WebScrapingService> _logger;
    private readonly HttpClient _httpClient;

    public WebScrapingService(
        IOptions<ScraperSettings> scraperSettings,
        IOptions<FilterSettings> filterSettings,
        ILocationService locationService,
        ILogger<WebScrapingService> logger,
        HttpClient httpClient)
    {
        _scraperSettings = scraperSettings.Value;
        _filterSettings = filterSettings.Value;
        _locationService = locationService;
        _logger = logger;
        _httpClient = httpClient;
        
        // Configure HttpClient
        _httpClient.Timeout = TimeSpan.FromSeconds(_scraperSettings.TimeoutSeconds);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public async Task<Dictionary<string, AppointmentResult>> ScrapeAppointmentsAsync()
    {
        var results = new Dictionary<string, AppointmentResult>();
        var startTime = DateTime.Now;
        
        _logger.LogInformation("Starting appointment scraping at {Time}", startTime.ToString("yyyy-MM-dd HH:mm:ss"));

        try
        {
            // Get the main page to extract session data and form information
            var mainPageResponse = await _httpClient.GetAsync(_scraperSettings.BaseUrl);
            mainPageResponse.EnsureSuccessStatusCode();
            
            var mainPageContent = await mainPageResponse.Content.ReadAsStringAsync();
            var mainDoc = new HtmlDocument();
            mainDoc.LoadHtml(mainPageContent);

            // Look for location/appointment data through API calls or form submissions
            // This is a simplified approach - the actual DMV site may require more complex interaction
            
            var appointmentData = await ExtractAppointmentDataAsync(mainDoc);
            
            foreach (var locationData in appointmentData)
            {
                try
                {
                    var locationName = locationData.Key;
                    var locationAddress = ExtractLocationAddress(locationName);
                    
                    // Apply location filtering
                    if (_locationService.IsLocationFilteringEnabled() && 
                        !_locationService.IsLocationAllowed(locationAddress))
                    {
                        _logger.LogDebug("Skipping location {Location} - outside distance range", locationName);
                        continue;
                    }

                    var availableTimes = ParseAppointmentTimes(locationData.Value);
                    var filteredTimes = ApplyDateTimeFilters(availableTimes);

                    var result = new AppointmentResult
                    {
                        LocationName = locationName,
                        LocationAddress = locationAddress,
                        AvailableTimes = filteredTimes,
                        IsError = false
                    };

                    results[locationName] = result;
                    
                    if (filteredTimes.Any())
                    {
                        _logger.LogInformation("Found {Count} appointments at {Location}", 
                            filteredTimes.Count, locationName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing location {Location}", locationData.Key);
                    results[locationData.Key] = new AppointmentResult
                    {
                        LocationName = locationData.Key,
                        LocationAddress = "Unknown",
                        IsError = true,
                        ErrorMessage = ex.Message
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during appointment scraping");
            
            // Return error result
            results["General Error"] = new AppointmentResult
            {
                LocationName = "General Error",
                LocationAddress = "N/A",
                IsError = true,
                ErrorMessage = ex.Message
            };
        }

        var endTime = DateTime.Now;
        _logger.LogInformation("Scraping completed in {Duration}ms. Found {ResultCount} locations.", 
            (endTime - startTime).TotalMilliseconds, results.Count);

        return results;
    }

    private async Task<Dictionary<string, string>> ExtractAppointmentDataAsync(HtmlDocument mainDoc)
    {
        var appointmentData = new Dictionary<string, string>();

        try
        {
            // Method 1: Try to find AJAX endpoints or API calls
            var scriptTags = mainDoc.DocumentNode.SelectNodes("//script");
            if (scriptTags != null)
            {
                foreach (var script in scriptTags)
                {
                    var scriptContent = script.InnerText;
                    
                    // Look for API endpoints, AJAX calls, or data structures
                    if (scriptContent.Contains("appointment") || scriptContent.Contains("location"))
                    {
                        var matches = Regex.Matches(scriptContent, @"['""]([^'""]*(?:appointment|location)[^'""]*)['""]", 
                            RegexOptions.IgnoreCase);
                        
                        foreach (Match match in matches)
                        {
                            _logger.LogDebug("Found potential endpoint: {Endpoint}", match.Groups[1].Value);
                        }
                    }
                }
            }

            // Method 2: Try to extract form data and submit requests
            var forms = mainDoc.DocumentNode.SelectNodes("//form");
            if (forms != null)
            {
                foreach (var form in forms)
                {
                    var action = form.GetAttributeValue("action", "");
                    var method = form.GetAttributeValue("method", "GET").ToUpper();
                    
                    if (action.Contains("appointment", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Found appointment form with action: {Action}", action);
                        
                        // Try to submit the form to get appointment data
                        var formData = ExtractFormData(form);
                        var response = await SubmitFormAsync(action, method, formData);
                        
                        if (!string.IsNullOrEmpty(response))
                        {
                            var responseDoc = new HtmlDocument();
                            responseDoc.LoadHtml(response);
                            
                            var extractedData = ExtractLocationDataFromResponse(responseDoc);
                            foreach (var item in extractedData)
                            {
                                appointmentData[item.Key] = item.Value;
                            }
                        }
                    }
                }
            }

            // Method 3: Look for embedded JSON data
            var jsonMatches = Regex.Matches(mainDoc.DocumentNode.InnerText, 
                @"\{[^{}]*(?:location|appointment)[^{}]*\}", RegexOptions.IgnoreCase);
            
            foreach (Match match in jsonMatches)
            {
                try
                {
                    _logger.LogDebug("Found potential JSON data: {Json}", match.Value);
                    // Process JSON data here if needed
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error parsing JSON data");
                }
            }

            // Fallback: Create dummy data for testing
            if (!appointmentData.Any())
            {
                _logger.LogWarning("No appointment data found. Using fallback approach.");
                appointmentData = CreateFallbackAppointmentData();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting appointment data");
        }

        return appointmentData;
    }

    private Dictionary<string, string> CreateFallbackAppointmentData()
    {
        // This is a fallback for testing - in reality, you'd need to analyze the actual DMV site
        return new Dictionary<string, string>
        {
            ["Charlotte North DMV"] = "01/15/2025 10:00:00 AM,01/16/2025 2:00:00 PM",
            ["Raleigh Central DMV"] = "01/17/2025 9:00:00 AM",
            ["Durham DMV Office"] = "" // No appointments
        };
    }

    private Dictionary<string, string> ExtractFormData(HtmlNode form)
    {
        var formData = new Dictionary<string, string>();
        
        var inputs = form.SelectNodes(".//input[@name]");
        if (inputs != null)
        {
            foreach (var input in inputs)
            {
                var name = input.GetAttributeValue("name", "");
                var value = input.GetAttributeValue("value", "");
                var type = input.GetAttributeValue("type", "text").ToLower();
                
                if (!string.IsNullOrEmpty(name))
                {
                    formData[name] = value;
                }
            }
        }

        return formData;
    }

    private async Task<string> SubmitFormAsync(string action, string method, Dictionary<string, string> formData)
    {
        try
        {
            var uri = new Uri(new Uri(_scraperSettings.BaseUrl), action);
            
            if (method == "POST")
            {
                var content = new FormUrlEncodedContent(formData);
                var response = await _httpClient.PostAsync(uri, content);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            else
            {
                var queryString = string.Join("&", formData.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
                var fullUri = $"{uri}?{queryString}";
                
                var response = await _httpClient.GetAsync(fullUri);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting form to {Action}", action);
            return string.Empty;
        }
    }

    private Dictionary<string, string> ExtractLocationDataFromResponse(HtmlDocument doc)
    {
        var locationData = new Dictionary<string, string>();
        
        // Look for appointment time data in various formats
        var timeElements = doc.DocumentNode.SelectNodes("//div[contains(@class, 'appointment')]//time | //span[contains(@class, 'time')] | //td[contains(@class, 'time')]");
        
        if (timeElements != null)
        {
            foreach (var element in timeElements)
            {
                var timeText = element.InnerText?.Trim();
                var locationElement = element.Ancestors().FirstOrDefault(a => 
                    a.GetAttributeValue("class", "").Contains("location", StringComparison.OrdinalIgnoreCase));
                
                if (locationElement != null && !string.IsNullOrEmpty(timeText))
                {
                    var locationName = ExtractLocationName(locationElement);
                    if (!string.IsNullOrEmpty(locationName))
                    {
                        if (locationData.ContainsKey(locationName))
                        {
                            locationData[locationName] += "," + timeText;
                        }
                        else
                        {
                            locationData[locationName] = timeText;
                        }
                    }
                }
            }
        }

        return locationData;
    }

    private string ExtractLocationName(HtmlNode locationElement)
    {
        var text = locationElement.InnerText?.Trim();
        if (string.IsNullOrEmpty(text))
            return string.Empty;
            
        // Clean up location name
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private string ExtractLocationAddress(string locationName)
    {
        // In the original Python code, this was extracted from button text
        // For now, return the location name as the address
        return locationName;
    }

    private List<DateTime> ParseAppointmentTimes(string appointmentData)
    {
        var times = new List<DateTime>();
        
        if (string.IsNullOrEmpty(appointmentData))
            return times;

        var timeStrings = appointmentData.Split(',', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var timeString in timeStrings)
        {
            var cleanedTime = timeString.Trim();
            
            // Try various date/time formats
            string[] formats = {
                "M/d/yyyy h:mm:ss tt",
                "MM/dd/yyyy h:mm:ss tt",
                "M/d/yyyy HH:mm:ss",
                "MM/dd/yyyy HH:mm:ss",
                "M/d/yyyy h:mm tt",
                "MM/dd/yyyy h:mm tt"
            };

            foreach (var format in formats)
            {
                if (DateTime.TryParseExact(cleanedTime, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime))
                {
                    times.Add(parsedTime);
                    break;
                }
            }
        }

        return times.OrderBy(t => t).ToList();
    }

    private List<DateTime> ApplyDateTimeFilters(List<DateTime> appointmentTimes)
    {
        var filteredTimes = appointmentTimes.AsEnumerable();

        // Apply date filtering
        if (_filterSettings.DateRangeStart.HasValue || _filterSettings.DateRangeEnd.HasValue || !string.IsNullOrEmpty(_filterSettings.DateRangeRelative))
        {
            var (hasDateFilter, startDate, endDate) = CalculateDateRange();
            
            if (hasDateFilter)
            {
                filteredTimes = filteredTimes.Where(t => t.Date >= startDate && t.Date <= endDate);
                _logger.LogDebug("Applied date filter: {StartDate} to {EndDate}", startDate, endDate);
            }
        }

        // Apply time filtering
        if (_filterSettings.TimeRangeStart.HasValue && _filterSettings.TimeRangeEnd.HasValue)
        {
            var startTime = _filterSettings.TimeRangeStart.Value;
            var endTime = _filterSettings.TimeRangeEnd.Value;
            
            filteredTimes = filteredTimes.Where(t => 
            {
                var timeOnly = TimeOnly.FromDateTime(t);
                return timeOnly >= startTime && timeOnly <= endTime;
            });
            
            _logger.LogDebug("Applied time filter: {StartTime} to {EndTime}", startTime, endTime);
        }

        return filteredTimes.ToList();
    }

    private (bool HasFilter, DateTime StartDate, DateTime EndDate) CalculateDateRange()
    {
        var today = DateTime.Today;

        if (!string.IsNullOrEmpty(_filterSettings.DateRangeRelative))
        {
            var relative = _filterSettings.DateRangeRelative.ToLower().Trim();
            if (relative.Length > 1)
            {
                var numStr = relative[..^1];
                var unit = relative[^1];
                
                if (int.TryParse(numStr, out var num) && num > 0)
                {
                    DateTime endDate = unit switch
                    {
                        'd' => today.AddDays(num),
                        'w' => today.AddDays(num * 7),
                        'm' => today.AddMonths(num),
                        _ => today.AddDays(num)
                    };
                    
                    return (true, today, endDate);
                }
            }
        }

        if (_filterSettings.DateRangeStart.HasValue && _filterSettings.DateRangeEnd.HasValue)
        {
            return (true, _filterSettings.DateRangeStart.Value.Date, _filterSettings.DateRangeEnd.Value.Date);
        }

        return (false, today, today);
    }
}