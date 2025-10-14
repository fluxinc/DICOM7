using System;
using System.Threading;
using DICOM7.Shared.Common;
using Serilog;

namespace DICOM7.ORU2DICOM
{
  internal class Program
  {
    private const string APPLICATION_NAME = "ORU2DICOM";

    private static Config _config;
    private static Hl7Server _hl7Server;
    private static OruMessageProcessor _processor;
    private static CancellationTokenSource _cts;
    private static Timer _retryTimer;
    private static bool _running = true;

    public static void Main(string[] args)
    {
      ProgramHelpers.ParseCommandLineArgs(args, APPLICATION_NAME);
      ProgramHelpers.ConfigureSerilog(APPLICATION_NAME);

      try
      {
        _config = ProgramHelpers.LoadConfiguration<Config>(APPLICATION_NAME);
        ProgramHelpers.InitializeCache(_config, defaultRetentionDays: _config.Cache != null ? _config.Cache.RetentionDays : 3, logCacheFolder: true);
        CacheManager.Initialize(_config.Cache);

        _processor = new OruMessageProcessor(_config);
        _hl7Server = new Hl7Server(_config, _processor);
        _cts = new CancellationTokenSource();

        Console.CancelKeyPress += OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        _hl7Server.Start();

        // Process any pending retries immediately on startup
        TryProcessPendingMessages();

        int retryMinutes = Math.Max(1, _config.Retry.RetryIntervalMinutes);
        _retryTimer = new Timer(RetryTimerCallback, null, TimeSpan.FromMinutes(retryMinutes), TimeSpan.FromMinutes(retryMinutes));

        Log.Information("ORU2DICOM service started (retry interval: {RetryMinutes} minutes)", retryMinutes);

        while (_running)
        {
          Thread.Sleep(500);
        }
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

    private static void RetryTimerCallback(object state)
    {
      TryProcessPendingMessages();
    }

    private static void TryProcessPendingMessages()
    {
      if (_processor == null || _cts == null || _cts.IsCancellationRequested)
      {
        return;
      }

      try
      {
        _processor.ProcessPendingMessagesAsync(_cts.Token).GetAwaiter().GetResult();
      }
      catch (OperationCanceledException)
      {
        // ignore cancellation during shutdown
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error while processing pending ORU messages");
      }
    }

    private static void Shutdown()
    {
      if (!_running)
      {
        return;
      }

      _running = false;

      try
      {
        _retryTimer?.Dispose();
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error disposing retry timer");
      }

      try
      {
        _hl7Server?.Stop();
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error stopping HL7 server");
      }

      try
      {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
          _cts.Cancel();
        }
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error cancelling operations");
      }
      finally
      {
        _cts?.Dispose();
      }

      Log.Information("ORU2DICOM service stopped");
    }

    private static void OnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
    {
      Log.Information("Shutdown requested via console");
      e.Cancel = true;
      _running = false;
      _cts?.Cancel();
    }

    private static void OnProcessExit(object sender, EventArgs e)
    {
      Log.Information("Process exit detected");
      _running = false;
      _cts?.Cancel();
    }
  }
}
