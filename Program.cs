using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using NCDmvScraper.Services;
using NCDmvScraper.Configuration;

namespace NCDmvScraper;

class Program
{
    static async Task Main(string[] args)
    {
        var host = CreateHostBuilder(args).Build();
        
        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("NC DMV Scraper starting...");

        try
        {
            var scraperService = host.Services.GetRequiredService<IDmvScraperService>();
            await scraperService.StartAsync();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Application terminated unexpectedly");
            throw;
        }
    }

    static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                config.AddEnvironmentVariables();
            })
            .ConfigureServices((context, services) =>
            {
                var configuration = context.Configuration;

                // Configure strongly-typed settings
                services.Configure<ScraperSettings>(configuration.GetSection("ScraperSettings"));
                services.Configure<NotificationSettings>(configuration.GetSection("NotificationSettings"));
                services.Configure<FilterSettings>(configuration.GetSection("FilterSettings"));

                // Register HttpClient
                services.AddHttpClient();

                // Register services
                services.AddSingleton<ILocationService, LocationService>();
                services.AddSingleton<INotificationService, NotificationService>();
                services.AddSingleton<IWebScrapingService, WebScrapingService>();
                services.AddSingleton<IDmvScraperService, DmvScraperService>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            });
}