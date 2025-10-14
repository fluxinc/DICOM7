using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DICOM7.Shared.Common;
using DICOM7.Shared.Config;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

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
    private const string APPLICATION_NAME = "ORM2DICOM";

    public static void Main(string[] args)
    {
      // Parse command line arguments
      ProgramHelpers.ParseCommandLineArgs(args, APPLICATION_NAME);

      // Configure Serilog
      ProgramHelpers.ConfigureSerilog(APPLICATION_NAME);

      try
      {
        // Load configuration
        _config = ProgramHelpers.LoadConfiguration<Config>(APPLICATION_NAME);

        // Initialize cache system
        ProgramHelpers.InitializeCache(_config, logCacheFolder: true);

        // Log resolved base paths for clarity on runtime locations
        ProgramHelpers.LogBasePaths(APPLICATION_NAME);

        // Register handlers for shutdown events
        Console.CancelKeyPress += OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        // Create a cancellation token source for graceful shutdown
        _cts = new CancellationTokenSource();

        // Start the HL7 server
        _hl7Server = new Hl7Server(_config);
        _hl7Server.Start();

        // Start the DICOM Worklist SCP server
        IHostBuilder builder = CreateHostBuilder(args);
        _host = builder.Build();
        _host.StartAsync(_cts.Token).GetAwaiter().GetResult();

        // Set up cleanup timer
        if (_config.Cache.AutoCleanup)
        {
          int cleanupInterval = _config.Cache.CleanupIntervalMinutes * 60 * 1000; // Convert to milliseconds
          _cleanupTimer = new Timer(CleanupExpiredOrms, null, cleanupInterval, cleanupInterval);
        }

        Log.Information("DICOM7 ORM2DICOM server started successfully");

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
    public static Config GetConfig() => _config;

    private static void WaitForShutdown()
    {
      // Simple blocking wait until signaled to shut down
      while (_running)
      {
        Thread.Sleep(1000);
      }
    }

    private static IHostBuilder CreateHostBuilder(string[] args)
    {
      return Host.CreateDefaultBuilder(args)
        .ConfigureLogging((hostContext, logging) => ProgramHelpers.ConfigureLogging(logging))
        .ConfigureServices((hostContext, services) => ConfigureServices(services));
    }

    private static void ConfigureServices(IServiceCollection services)
    {
      // Configure host options and DICOM services
      ProgramHelpers.ConfigureServices(services, _config.Dicom.AETitle);

      // Register background services
      services.AddSingleton<DICOMServerBackgroundService>();
      services.AddHostedService<DICOMServerBackgroundService>();
    }

    private static void CleanupExpiredOrms(object state)
    {
      try
      {
        int expiryHours = _config.Order.ExpiryHours;
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
      _cts?.Cancel();
    }

    private static void OnProcessExit(object sender, EventArgs e)
    {
      Log.Information("Application is exiting");
      _running = false;
      _cts?.Cancel();
    }
  }
}
