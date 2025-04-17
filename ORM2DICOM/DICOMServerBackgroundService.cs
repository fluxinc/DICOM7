using System;
using System.Threading;
using System.Threading.Tasks;
using DICOM7.Shared;
using FellowOakDicom.Network;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DICOM7.ORM2DICOM
{
    internal class DICOMServerBackgroundService : BackgroundService, IDisposable
    {
        private readonly ILogger<DICOMServerBackgroundService> _logger;
        private IDicomServer<WorklistSCP> _worklistSCP;
        private readonly Config _config;

        public DICOMServerBackgroundService(ILogger<DICOMServerBackgroundService> logger)
        {
            _logger = logger;
            _config = Program.GetConfig();
            // Removed StartAllServers() from constructor to avoid duplicate startup
        }

        public void StartAllServers()
        {
           StartWorklistSCP();
        }

        private void StartWorklistSCP()
        {
            try
            {
                // TODO: Initialize the DICOM server here
                // Example (adjust parameters as needed for your version):
                // _worklistSCP = DicomServerFactory.Create<WorklistSCP>(
                //     _config.Dicom.ListenPort,
                //     ... other parameters as required by your FO-DICOM version
                // );

                _logger.LogInformation("Worklist SCP started on port {Port} with AE Title {AETitle}",
                    _config.Dicom.ListenPort, _config.Dicom.AETitle);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start WorklistSCP on port {Port}", _config.Dicom.ListenPort);
            }
        }

        public void StopAllServers()
        {
            if (_worklistSCP != null)
            {
                _logger.LogInformation("Stopping Worklist SCP server");
                _worklistSCP.Dispose();
                _worklistSCP = null;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Start the servers when the background service starts
            StartAllServers();

            // Just keep the service alive until cancellation is requested
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(250, stoppingToken);
            }

            StopAllServers();
        }

        public override void Dispose()
        {
            StopAllServers();
            base.Dispose();
        }
    }
}

