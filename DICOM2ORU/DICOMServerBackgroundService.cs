using System;
using System.Threading;
using System.Threading.Tasks;
using FellowOakDicom.Network;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DICOM7.DICOM2ORU
{
  internal class DICOMServerBackgroundService(
    IDicomServerFactory factory,
    ILogger<DICOMServerBackgroundService> logger)
    : BackgroundService, IDisposable
  {
    private IDicomServer<StoreSCP> _storeSCP;
    private readonly Config _config = Program.GetConfig();

    private void StartSCP()
    {
      try
      {
        _storeSCP = (IDicomServer<StoreSCP>)factory.Create<StoreSCP>(
          port: _config.Dicom.ListenPort, tlsAcceptor: null, fallbackEncoding: null, logger: logger);

        logger.LogInformation("Store SCP started on port {Port} with AE Title {AETitle}",
          _config.Dicom.ListenPort, _config.Dicom.AETitle);
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "Failed to start WorklistSCP on port {Port}", _config.Dicom.ListenPort);
      }
    }

    private void StopSCP()
    {
      if (_storeSCP == null) return;

      logger.LogInformation("Stopping Store SCP server");
      _storeSCP.Dispose();
      _storeSCP = null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

      StartSCP();

      // Just keep the service alive until cancellation is requested
      while (!stoppingToken.IsCancellationRequested)
      {
        await Task.Delay(250, stoppingToken);
      }

      StopSCP();
    }

    public override void Dispose()
    {
      StopSCP();
      base.Dispose();
    }
  }
}
