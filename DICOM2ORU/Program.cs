using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DICOM7.Shared;
using DICOM7.Shared.Common;
using DICOM7.Shared.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DICOM7.DICOM2ORU
{
  internal class Program
  {
    private static Config _config;
    private static CancellationTokenSource _cts;
    private static IHost _host;
    private static bool _running = true;
    private const string APPLICATION_NAME = "DICOM2ORU";
    private static DicomImageProcessor _processor;
    private static string _oruTemplate;
    private static readonly object _processingLock = new object();
    private static bool _isProcessing;

    private static async Task Main(string[] args)
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
        ProgramHelpers.InitializeCache(_config);

        // Log resolved base paths for clarity on runtime locations
        ProgramHelpers.LogBasePaths(APPLICATION_NAME);

        // Register handlers for shutdown events
        Console.CancelKeyPress += OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        // Create a cancellation token source for graceful shutdown
        _cts = new CancellationTokenSource();

        // Load ORU template
        string templatePath = Path.Combine(AppConfig.CommonAppFolder, "oruTemplate.hl7");
        Log.Information("Looking for ORU template at {TemplatePath}", templatePath);
        _oruTemplate = ORUGenerator.LoadTemplate(templatePath);

        // Initialize processor
        _processor = new DicomImageProcessor(_config, _oruTemplate);

        // Set up the StoreSCP to use our processor and template
        StoreSCP.SetProcessors(_processor, _oruTemplate, _config);

        // Start the DICOM Store SCP server
        IHostBuilder builder = CreateHostBuilder(args);
        _host = builder.Build();
        await _host.StartAsync(_cts.Token);

        Log.Information("DICOM2ORU service started successfully");

        // Main processing loop
        while (_running)
          try
          {
            // First process any pending messages that need to be retried
            _processor.ProcessPendingMessages();

            // Then process any messages in the outgoing folder
            await ProcessOutgoingMessagesAsync();

            Log.Information("Sleeping for {RetryIntervalMinutes} minutes", _config.Retry.RetryIntervalMinutes);
            await Task.Delay(TimeSpan.FromSeconds(_config.Retry.RetryIntervalMinutes * 60), _cts.Token);
          }
          catch (TaskCanceledException)
          {
            // Cancellation requested, exit the loop
            break;
          }
          catch (Exception ex)
          {
            Log.Error(ex, "Error in processing cycle: {Message}", ex.Message);
            // Continue to next cycle after error
            await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token);
          }
      }
      catch (TaskCanceledException)
      {
        // Normal cancellation, log at info level
        Log.Information("Application was canceled");
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

    /// <summary>
    ///   Processes all pending ORU messages in the outgoing folder
    /// </summary>
    private static async Task ProcessOutgoingMessagesAsync()
    {
      if (_isProcessing)
      {
        Log.Debug("Already processing outgoing messages, skipping");
        return;
      }

      lock (_processingLock)
      {
        if (_isProcessing) return;
        _isProcessing = true;
      }

      try
      {
        // Ensure cache folder exists
        CacheManager.EnsureCacheFolder();

        // Get the outgoing folder path
        string outgoingFolder = Path.Combine(CacheManager.CacheFolder, "outgoing");
        if (!Directory.Exists(outgoingFolder))
        {
          Directory.CreateDirectory(outgoingFolder);
          Log.Information("Created outgoing ORU folder: {OutgoingPath}", outgoingFolder);
          return; // No files to process in a newly created folder
        }

        // Get all ORU files in the outgoing folder
        string[] oruFiles = Directory.GetFiles(outgoingFolder, "*.oru");
        if (oruFiles.Length == 0)
        {
          Log.Debug("No ORU messages found in outgoing folder");
          return;
        }

        Log.Information("Found {Count} ORU messages to send", oruFiles.Length);

        // Process each ORU file
        foreach (string filePath in oruFiles) await ProcessOruFileAsync(filePath);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error processing outgoing messages: {Message}", ex.Message);
      }
      finally
      {
        _isProcessing = false;
      }
    }

    /// <summary>
    ///   Processes a single ORU file
    /// </summary>
    private static async Task ProcessOruFileAsync(string filePath)
    {
      string fileName = Path.GetFileName(filePath);
      string sopInstanceUid = Path.GetFileNameWithoutExtension(filePath);

      try
      {
        // Read the ORU message content
        string oruMessage = File.ReadAllText(filePath);

        // Send the message using the shared HL7Sender
        bool success =
          await HL7Sender.SendOruAsync(_config, oruMessage, _config.HL7.ReceiverHost, _config.HL7.ReceiverPort);

        if (success)
        {
          // Mark as processed in cache
          CacheManager.MarkAsProcessed(sopInstanceUid, _config.Cache.KeepSentItems, oruMessage);

          // Delete the file from outgoing folder
          File.Delete(filePath);

          // Remove from retry queue if it was there
          if (RetryManager.IsPendingRetry(sopInstanceUid, CacheManager.CacheFolder))
            RetryManager.RemovePendingMessage(sopInstanceUid, CacheManager.CacheFolder);

          Log.Information("Successfully sent ORU message: {SopInstanceUid}", sopInstanceUid);
        }
        else
        {
          // If failed, add to retry queue
          int attemptCount = 1;
          if (RetryManager.IsPendingRetry(sopInstanceUid, CacheManager.CacheFolder))
            attemptCount = RetryManager.GetAttemptCount(sopInstanceUid, CacheManager.CacheFolder) + 1;

          RetryManager.SavePendingMessage(sopInstanceUid, oruMessage, CacheManager.CacheFolder, attemptCount);

          // Delete the file from outgoing folder since it's now in the retry queue
          File.Delete(filePath);

          Log.Warning("Failed to send ORU message: {SopInstanceUid}, added to retry queue (attempt {AttemptCount})",
            sopInstanceUid, attemptCount);
        }
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error processing ORU file {FilePath}: {Message}", filePath, ex.Message);

        // If we can't even read the file, move it out of the outgoing folder to avoid continual errors
        try
        {
          string errorFolder = Path.Combine(CacheManager.CacheFolder, "error");
          if (!Directory.Exists(errorFolder)) Directory.CreateDirectory(errorFolder);

          string errorPath = Path.Combine(errorFolder, fileName);
          if (File.Exists(errorPath))
          {
            // Add timestamp if file already exists in error folder
            string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            errorPath = Path.Combine(errorFolder, $"{sopInstanceUid}_{timestamp}.oru");
          }

          File.Move(filePath, errorPath);
          Log.Information("Moved problematic ORU file to error folder: {ErrorPath}", errorPath);
        }
        catch (Exception moveEx)
        {
          Log.Error(moveEx, "Failed to move problematic ORU file to error folder: {FilePath}", filePath);
        }
      }
    }

    public static IHostBuilder CreateHostBuilder(string[] args)
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

    public static Config GetConfig() => _config;

    private static void Shutdown()
    {
      _running = false;

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
