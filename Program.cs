using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OrderORM;

internal class Program
{
    private static async Task Main(string[] args)
    {
        // Parse command line arguments
        ParseCommandLineArgs(args);

        var logPath = Path.Combine(AppConfig.CommonAppFolder, "logs", "order-orm-log-.txt");
        Log.Logger = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .WriteTo.Console()
            .WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 1024 * 1024 * 10,
                rollOnFileSizeLimit: true
            )
            .CreateLogger();

        try
        {
            // Try to load the config from the common app folder first
            var commonConfigPath = AppConfig.GetConfigFilePath();
            Config config = null;

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .Build();

            // First try to load from common app folder
            if (File.Exists(commonConfigPath))
            {
                Log.Information("Loading configuration from common location: {ConfigPath}", commonConfigPath);
                try
                {
                    config = deserializer.Deserialize<Config>(File.ReadAllText(commonConfigPath));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error loading configuration from common location");
                    // Will fall back to local config
                }
            }

            // If not found or failed to load, try local config
            if (config == null)
            {
                var localConfigPath = Path.GetFullPath("config.yaml");
                Log.Information("Loading configuration from local path: {ConfigPath}", localConfigPath);

                try
                {
                    config = deserializer.Deserialize<Config>(File.ReadAllText(localConfigPath));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error loading local configuration");
                    throw; // Cannot continue without configuration
                }
            }

            // Ensure Cache config is initialized
            config.Cache ??= new CacheConfig
            {
                RetentionDays = 7 // Default retention period
            };

            // Set the configured cache folder if specified in config
            if (config.Cache != null && !string.IsNullOrWhiteSpace(config.Cache.Folder))
                CacheManager.SetConfiguredCacheFolder(config.Cache.Folder);

            // Clean up cache based on retention policy
            CacheManager.CleanUpCache(CacheManager.CacheFolder, config.Cache?.RetentionDays ?? 7);

            var templatePath = Path.Combine(AppConfig.CommonAppFolder, "ormTemplate.hl7");

            Log.Information("Looking for ORM template at {TemplatePath}", templatePath);
            var ormTemplate = ORMGenerator.LoadTemplate(templatePath);

            var querier = new WorklistQuerier(config, ormTemplate);

            Log.Information("Starting HL7 ORM sender script with SCU AE Title '{DicomScuAeTitle}'",
                config.Dicom.ScuAeTitle);

            // Main loop
            while (true)
            {
                // First process any pending messages that need to be retried
                querier.ProcessPendingMessages();

                // Then query for new messages
                await querier.QueryAsync();

                // The processing of results is now handled in the OnFindResponseReceived method
                // We can optionally access the results here if needed
                var results = querier.GetQueryResults();
                if (!results.Any()) Log.Information("No new orders found in this query cycle");

                Log.Information("Sleeping for {QueryInterval} seconds", config.QueryInterval);
                await Task.Delay(TimeSpan.FromSeconds(config.QueryInterval));
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    ///     Parses command line arguments and configures the application accordingly
    /// </summary>
    /// <param name="args">Command line arguments</param>
    private static void ParseCommandLineArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--path" && i + 1 < args.Length)
            {
                var basePath = args[i + 1];
                basePath = Path.GetFullPath(basePath);
                if (Directory.Exists(basePath))
                {
                    AppConfig.SetBasePath(basePath);
                    Console.WriteLine($"Using custom base path: {basePath}");
                }
                else
                {
                    Console.WriteLine($"ERROR: Specified path '{basePath}' does not exist.  Terminating.");
                    Environment.Exit(1);
                }

                i++;
            }
    }
}