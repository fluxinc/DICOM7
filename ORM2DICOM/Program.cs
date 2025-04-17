using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DICOM7.Shared;
using FellowOakDicom.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DICOM7.ORM2DICOM
{
  internal class Program
  {
    private static Config _config;
    private static Hl7Server _hl7Server;
    private static Timer _cleanupTimer;
    private static bool _running = true;
    private static CancellationTokenSource _cts;
    private static IHost _host;

    public static void Main(string[] args)
    {
      // Parse command line arguments
      ParseCommandLineArgs(args);

      // Initialize the logger
      string logPath = Path.Combine(AppConfig.CommonAppFolder, "logs", "dicom7-orm2dicom-.log");
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
        // Load configuration
        LoadConfiguration();

        // Initialize cache system
        InitializeCache();

        // Register handlers for shutdown events
        Console.CancelKeyPress += OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        // Create a cancellation token source for graceful shutdown
        _cts = new CancellationTokenSource();

        // Start the HL7 server
        _hl7Server = new Hl7Server(_config);
        _hl7Server.Start();

        // Start the DICOM Worklist SCP server
        var builder = CreateHostBuilder(args);
        _host = builder.Build();
        _host.StartAsync(_cts.Token).GetAwaiter().GetResult();

        // Set up cleanup timer
        if (_config.Expiry.AutoCleanup)
        {
          int cleanupInterval = _config.Expiry.CleanupIntervalMinutes * 60 * 1000; // Convert to milliseconds
          _cleanupTimer = new Timer(CleanupExpiredOrms, null, cleanupInterval, cleanupInterval);
        }

        Log.Information("ORM2DICOM server started successfully.");
        Log.Information("HL7 server listening on port {HL7Port}.", _config.HL7.ListenPort);
        Log.Information("DICOM server listening on port {DicomPort} with AE Title {AETitle}.",
          _config.Dicom.ListenPort, _config.Dicom.AETitle);

        // Keep the application running until cancelled
        WaitForShutdown();
      }
      catch (Exception ex)
      {
        Log.Fatal(ex, "Application terminated unexpectedly");
      }
      finally
      {
        Shutdown();
        Log.CloseAndFlush();
      }
    }

    // Expose config to the background service
    public static Config GetConfig()
    {
      return _config;
    }

    private static void WaitForShutdown()
    {
      // Simple blocking wait until signaled to shut down
      while (_running)
      {
        Thread.Sleep(1000);
      }
    }

    private static void LoadConfiguration()
    {
      // Try to load the config from the common app folder first
      string commonConfigPath = AppConfig.GetConfigFilePath();
      _config = null;

      var deserializer = new DeserializerBuilder()
          .WithNamingConvention(PascalCaseNamingConvention.Instance)
          .Build();

      // First try to load from common app folder
      if (File.Exists(commonConfigPath))
      {
        Log.Information("Loading configuration from common location: {ConfigPath}", commonConfigPath);
        try
        {
          _config = deserializer.Deserialize<Config>(File.ReadAllText(commonConfigPath));
        }
        catch (Exception ex)
        {
          Log.Error(ex, "Error loading configuration from common location");
          // Will fall back to local config
        }
      }

      // If not found or failed to load, try local config
      if (_config != null) return;
      {
        string localConfigPath = Path.GetFullPath("config.yaml");
        Log.Information("Loading configuration from local path: {ConfigPath}", localConfigPath);

        try
        {
          _config = deserializer.Deserialize<Config>(File.ReadAllText(localConfigPath));
        }
        catch (Exception ex)
        {
          Log.Error(ex, "Error loading configuration");

          // Create a default configuration if none exists
          Log.Information("No configuration found, using default values");
          _config = new Config(); // This will use the default values defined in the Config class
        }
      }
    }

    private static void InitializeCache()
    {
      // Set the configured cache folder if specified in config
      if (_config.Cache != null && !string.IsNullOrWhiteSpace(_config.Cache.Folder))
      {
        CacheManager.SetConfiguredCacheFolder(_config.Cache.Folder);
      }

      // Ensure cache folder exists
      CacheManager.EnsureCacheFolder();

      // Clean up cache based on retention policy
      CacheManager.CleanUpCache(CacheManager.CacheFolder, _config.Cache.RetentionDays);
    }

    public static IHostBuilder CreateHostBuilder(string[] args)
    {
      return Host.CreateDefaultBuilder(args)
        .ConfigureLogging((hostContext, logging) => ConfigureLogging(logging))
        .ConfigureServices((hostContext, services) => ConfigureServices(services));
    }

    private static void ConfigureLogging(ILoggingBuilder logging)
    {
      logging.ClearProviders();
      logging.AddSerilog(dispose: true);
    }

    private static void ConfigureServices(IServiceCollection services) =>
      ConfigureBackgroundServices(services);

    private static void ConfigureBackgroundServices(IServiceCollection services) =>
      services.AddHostedService<DICOMServerBackgroundService>();

    private static void CleanupExpiredOrms(object state)
    {
      try
      {
        int expiryHours = _config.Expiry.ExpiryHours;
        int removed = CachedORM.RemoveExpired(expiryHours);

        if (removed > 0)
        {
          Log.Information("Cleaned up {Count} expired ORM messages", removed);
        }
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error during cleanup of expired ORM messages");
      }
    }

    private static void Shutdown()
    {
      _running = false;

      // Dispose of the cleanup timer
      _cleanupTimer?.Dispose();

      // Stop the HL7 server
      if (_hl7Server != null && _hl7Server.IsRunning)
      {
        Log.Information("Stopping HL7 server...");
        _hl7Server.Stop();
      }

      // Stop the host (which will stop the DICOM server)
      if (_host != null)
      {
        Log.Information("Stopping host services...");
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
      }

      // Signal cancellation to any tasks
      _cts?.Cancel();

      Log.Information("Shutdown complete");
    }

    private static void OnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
    {
      Log.Information("Shutdown requested via console");
      e.Cancel = true; // Prevent the process from terminating immediately
      _running = false;
    }

    private static void OnProcessExit(object sender, EventArgs e)
    {
      Log.Information("Application is exiting");
      _running = false;
    }

    private static void ParseCommandLineArgs(string[] args)
    {
      for (int i = 0; i < args.Length; i++)
      {
        if (args[i] != "--path" || i + 1 >= args.Length) continue;

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
        i++; // Skip the next argument as it's the path
      }
    }
  }
}
