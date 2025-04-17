using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using FellowOakDicom;

namespace DICOM2ORU
{
  internal class Program
  {
    private static async Task Main(string[] args)
    {
      // Parse command line arguments
      ParseCommandLineArgs(args);

      string logPath = Path.Combine(AppConfig.CommonAppFolder, "logs", "dicom7-dicom2oru-.log");
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
        string commonConfigPath = AppConfig.GetConfigFilePath();
        Config config = null;

        IDeserializer deserializer = new DeserializerBuilder()
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
          string localConfigPath = Path.GetFullPath("config.yaml");
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

        string templatePath = Path.Combine(AppConfig.CommonAppFolder, "oruTemplate.hl7");

        Log.Information("Looking for ORU template at {TemplatePath}", templatePath);
        string oruTemplate = ORUGenerator.LoadTemplate(templatePath);

        DicomImageProcessor processor = new(config, oruTemplate);

        Log.Information("Starting DICOM2ORU HL7 sender service");

        // Main loop
        while (true)
        {
          // First process any pending messages that need to be retried
          processor.ProcessPendingMessages();

          // Then check for new DICOM files to process
          await processor.ProcessInputFolderAsync();

          System.Collections.Generic.IEnumerable<ProcessingResult> processedResults = processor.GetProcessingResults();
          if (!processedResults.Any())
            Log.Information("No new DICOM images found in this processing cycle");

          Log.Information("Sleeping for {ProcessInterval} seconds", config.ProcessInterval);
          await Task.Delay(TimeSpan.FromSeconds(config.ProcessInterval));
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
      for (int i = 0; i < args.Length; i++)
        if (args[i] == "--path" && i + 1 < args.Length)
        {
          string basePath = args[i + 1];
          basePath = Path.GetFullPath(basePath);
          if (Directory.Exists(basePath))
          {
            AppConfig.SetBasePath(basePath);
            Console.WriteLine($"Using custom base path: {basePath}");
          }
          else
          {
            Console.WriteLine($"ERROR: Specified path '{basePath}' does not exist. Terminating.");
            Environment.Exit(1);
          }

          i++;
        }
    }
  }
}
