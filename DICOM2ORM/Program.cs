using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DICOM7.Shared.Common;
using DICOM7.Shared.Config;
using FellowOakDicom;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DICOM7.DICOM2ORM
{

  internal class Program
  {
    private static Config _config;
    private static bool _running = true;
    private static CancellationTokenSource _cts;
    private const string APPLICATION_NAME = "DICOM2ORM";
    private static string _ormTemplate;

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

            // Register handlers for shutdown events
            Console.CancelKeyPress += OnCancelKeyPress;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            // Create a cancellation token source for graceful shutdown
            _cts = new CancellationTokenSource();

            // Load ORM template
            string templatePath = Path.Combine(AppConfig.CommonAppFolder, "ormTemplate.hl7");
            Log.Information("Looking for ORM template at {TemplatePath}", templatePath);
            _ormTemplate = ORMGenerator.LoadTemplate(templatePath);

            // Create worklist querier
            WorklistQuerier querier = new WorklistQuerier(_config, _ormTemplate);

            Log.Information("Starting HL7 ORM sender script with SCU AE Title '{DicomScuAeTitle}'",
                _config.Dicom.ScuAeTitle);

            // Main loop
            while (_running)
            {
                try
                {
                    // First process any pending messages that need to be retried
                    querier.ProcessPendingMessages();

                    // Then query for new messages
                    await querier.QueryAsync();

                    // The processing of results is now handled in the OnFindResponseReceived method
                    // We can optionally access the results here if needed
                    IEnumerable<DicomDataset> results = querier.GetQueryResults();
                    if (!results.Any()) Log.Information("No new orders found in this query cycle");

                    Log.Information("Sleeping for {QueryInterval} seconds", _config.Query.IntervalSeconds);
                    await Task.Delay(TimeSpan.FromSeconds(_config.Query.IntervalSeconds), _cts.Token);
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

    private static void Shutdown()
    {
        _running = false;

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
