using System;
using System.IO;
using Serilog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using FellowOakDicom;
using DICOM7.Shared.Config;
using DICOM7.Shared.Cache;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.Collections.Generic;

namespace DICOM7.Shared.Common
{
  /// <summary>
  /// Helper methods for Program.cs implementations
  /// </summary>
  public static class ProgramHelpers
  {
    /// <summary>
    /// Parses command line arguments looking for --path to set a custom base path
    /// </summary>
    /// <param name="args">Command line arguments</param>
    /// <param name="applicationName">The name of the application for initialization</param>
    public static void ParseCommandLineArgs(string[] args, string applicationName)
    {
      // Initialize AppConfig
      AppConfig.Initialize(applicationName);

      for (int i = 0; i < args.Length; i++)
      {
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

    /// <summary>
    /// Configures the standard Serilog logger for a DICOM7 application
    /// </summary>
    /// <param name="applicationName">The name of the application for log file naming</param>
    public static void ConfigureSerilog(string applicationName)
    {
      string logPath = Path.Combine(AppConfig.CommonAppFolder, "logs", $"dicom7-{applicationName.ToLowerInvariant()}-.log");
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
    }

    /// <summary>
    /// Loads configuration from common or local path with fallback to default
    /// </summary>
    /// <typeparam name="T">The type of configuration to load</typeparam>
    /// <param name="applicationName">The application name</param>
    /// <returns>The loaded configuration or default if loading fails</returns>
    public static T LoadConfiguration<T>(string applicationName) where T : class, new()
    {
      // Only try to load the config from the common app folder (which is either the --path argument or ProgramData\Flux Inc\DICOM7\appname\)
      string configPath = AppConfig.GetConfigFilePath();
      T config = null;

      try
      {
        // Try to load from the configured path
        if (File.Exists(configPath))
        {
          Log.Information("Loading configuration from path: {ConfigPath}", configPath);
          try
          {
            config = LoadConfigFromFile<T>(configPath);
          }
          catch (Exception ex)
          {
            Log.Error(ex, "Error loading configuration file");
            // Fall back to default config
          }
        }
        else
        {
          Log.Warning("Configuration file not found: {ConfigPath}", configPath);
        }

        // If still null, use default configuration
        if (config == null)
        {
          Log.Information("No configuration found, using default values");
          config = new T();
        }

        return config;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error loading configuration, using defaults");
        return new T();
      }
    }

    /// <summary>
    /// Initializes and configures the cache system
    /// </summary>
    /// <param name="config">The configuration object containing cache settings</param>
    /// <param name="defaultRetentionDays">Default retention period in days if not specified in config</param>
    /// <param name="logCacheFolder">Whether to log the cache folder path</param>
    public static void InitializeCache(IHasCacheConfig config, int defaultRetentionDays = 7, bool logCacheFolder = false)
    {
      // Ensure Cache config is initialized
      if (config.Cache == null)
      {
        config.Cache = new BaseCacheConfig
        {
          RetentionDays = defaultRetentionDays
        };
      }
      else if (config.Cache.RetentionDays <= 0)
      {
        config.Cache.RetentionDays = defaultRetentionDays;
      }

      // Set the configured cache folder if specified in config
      if (!string.IsNullOrWhiteSpace(config.Cache.Folder))
      {
        BaseCacheManager.SetConfiguredCacheFolder(config.Cache.Folder);
      }

      // Ensure cache folder exists
      BaseCacheManager.EnsureCacheFolder();

      // Log cache folder if requested
      if (logCacheFolder)
      {
        Log.Information("Set cache folder to: {CacheFolder}", BaseCacheManager.CacheFolder);
      }

      // Clean up cache based on retention policy
      BaseCacheManager.CleanUpCache(BaseCacheManager.CacheFolder, config.Cache.RetentionDays);
    }

    /// <summary>
    /// Loads configuration from a YAML file
    /// </summary>
    /// <typeparam name="T">The type to deserialize into</typeparam>
    /// <param name="path">Path to the configuration file</param>
    /// <returns>The deserialized configuration</returns>
    private static T LoadConfigFromFile<T>(string path) where T : class
    {
      IDeserializer deserializer = new DeserializerBuilder()
        .WithNamingConvention(PascalCaseNamingConvention.Instance)
        .Build();

      return deserializer.Deserialize<T>(File.ReadAllText(path));
    }

    /// <summary>
    /// Configures standard DICOM services for a host builder
    /// </summary>
    public static void ConfigureServices(IServiceCollection services, string aeTitle)
    {
      // Configure host options
      services.Configure<HostOptions>(hostOptions =>
      {
        hostOptions.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
      });

      // Configure DICOM services
      services.AddFellowOakDicom();
      services.AddSingleton(aeTitle);
    }

    /// <summary>
    /// Configures logging for a host builder
    /// </summary>
    public static void ConfigureLogging(ILoggingBuilder logging)
    {
      logging.ClearProviders();
      logging.AddSerilog(dispose: true);
    }
  }
}
