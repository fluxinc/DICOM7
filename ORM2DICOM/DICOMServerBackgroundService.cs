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
    internal class DICOMServerBackgroundService(
      IDicomServerFactory factory,
      ILogger<DICOMServerBackgroundService> logger)
      : BackgroundService, IDisposable
    {
      private IDicomServer<WorklistSCP> _worklistSCP;
        private readonly Config _config = Program.GetConfig();

        private void StartWorklistSCP()
        {
            try
            {
              _worklistSCP = (IDicomServer<WorklistSCP>)factory.Create<WorklistSCP>(port: _config.Dicom.ListenPort, tlsAcceptor: null, fallbackEncoding: null, logger: logger);

                logger.LogInformation("Worklist SCP started on port {Port} with AE Title {AETitle}",
                    _config.Dicom.ListenPort, _config.Dicom.AETitle);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to start WorklistSCP on port {Port}", _config.Dicom.ListenPort);
            }
        }

        private void StopWorklistSCP()
        {
          if (_worklistSCP == null) return;

          logger.LogInformation("Stopping Worklist SCP server");
          _worklistSCP.Dispose();
          _worklistSCP = null;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {

            StartWorklistSCP();

            // Just keep the service alive until cancellation is requested
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(250, stoppingToken);
            }

            StopWorklistSCP();
        }

        public override void Dispose()
        {
            StopWorklistSCP();
            base.Dispose();
        }
    }
}

