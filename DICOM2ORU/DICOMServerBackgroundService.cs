using System;
using System.Threading;
using System.Threading.Tasks;
using FellowOakDicom.Network;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DICOM7.DICOM2ORU
{
  internal class DICOMServerBackgroundService : BackgroundService, IDisposable
  {
    private readonly IDicomServerFactory _factory;
    private readonly ILogger<DICOMServerBackgroundService> _logger;
    private IDicomServer<StoreSCP> _storeSCP;
    private readonly Config _config = Program.GetConfig();

    public DICOMServerBackgroundService(
      IDicomServerFactory factory,
      ILogger<DICOMServerBackgroundService> logger)
    {
      _factory = factory;
      _logger = logger;
    }

    private void StartSCP()
    {
      try
      {
        _storeSCP = (IDicomServer<StoreSCP>)_factory.Create<StoreSCP>(
          port: _config.Dicom.ListenPort, tlsAcceptor: null, fallbackEncoding: null, logger: _logger);

        _logger.LogInformation("Store SCP started on port {Port} with AE Title {AETitle}",
          _config.Dicom.ListenPort, _config.Dicom.AETitle);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to start WorklistSCP on port {Port}", _config.Dicom.ListenPort);
      }
    }

    private void StopSCP()
    {
      if (_storeSCP == null)
        return;

      _logger.LogInformation("Stopping Store SCP server");
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
