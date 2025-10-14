using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Serilog;

namespace DICOM7.ORU2DICOM
{
  /// <summary>
  /// Minimal MLLP server for processing incoming HL7 ORU messages
  /// </summary>
  public class Hl7Server
  {
    private static readonly ILogger Logger = Log.ForContext<Hl7Server>();

    private readonly Config _config;
    private readonly OruMessageProcessor _processor;
    private readonly List<(IPAddress Address, int Port, TcpListener Listener, Thread Worker)> _listeners;
    private volatile bool _isRunning;

    private const char START_OF_BLOCK = '\u000b';
    private const char END_OF_BLOCK = '\u001c';
    private const char CARRIAGE_RETURN = (char)13;
    private const char FIELD_DELIMITER = '|';
    private const int MESSAGE_CONTROL_ID_LOCATION = 9;

    public Hl7Server(Config config, OruMessageProcessor processor)
    {
      _config = config ?? throw new ArgumentNullException(nameof(config));
      _processor = processor ?? throw new ArgumentNullException(nameof(processor));
      _listeners = new List<(IPAddress, int, TcpListener, Thread)>();
    }

    public bool IsRunning
    {
      get { return _isRunning; }
    }

    public void Start()
    {
      if (_isRunning)
      {
        Logger.Warning("HL7 server already running");
        return;
      }

      try
      {
        int port = _config.HL7.ListenPort;
        string listenIp = _config.HL7.ListenIP;

        Logger.Information("Starting HL7 server on {ListenIp}:{Port}", listenIp, port);

        _listeners.Clear();
        _isRunning = true;

        if (!string.IsNullOrEmpty(listenIp) && listenIp != "0.0.0.0" && IPAddress.TryParse(listenIp, out IPAddress configuredIp))
        {
          StartListener(configuredIp, port);
        }
        else
        {
          foreach (IPAddress ipAddress in LocalIpAddresses())
          {
            try
            {
              StartListener(ipAddress, port);
            }
            catch (Exception ex)
            {
              Logger.Error(ex, "Failed to start HL7 listener on {Ip}:{Port}", ipAddress, port);
            }
          }
        }

        Logger.Information("HL7 server started on port {Port}", port);
      }
      catch (Exception ex)
      {
        _isRunning = false;
        Logger.Error(ex, "Failed to start HL7 server");
        throw;
      }
    }

    public void Stop()
    {
      if (!_isRunning)
      {
        return;
      }

      Logger.Information("Stopping HL7 server");
      _isRunning = false;

      foreach ((IPAddress address, int port, TcpListener listener, Thread worker) in _listeners)
      {
        try
        {
          listener.Stop();
          Logger.Information("Stopped listener on {Address}:{Port}", address, port);
        }
        catch (Exception ex)
        {
          Logger.Error(ex, "Error stopping listener on {Address}:{Port}", address, port);
        }
      }

      _listeners.Clear();
    }

    private void StartListener(IPAddress ipAddress, int port)
    {
      TcpListener listener = new TcpListener(ipAddress, port);
      listener.Start();

      Thread worker = new Thread(ListenerThreadMethod)
      {
        IsBackground = true,
        Name = string.Format("HL7Listener-{0}-{1}", ipAddress, port)
      };

      worker.Start(listener);
      _listeners.Add((ipAddress, port, listener, worker));

      Logger.Information("HL7 listener ready on {Address}:{Port}", ipAddress, port);
    }

    private void ListenerThreadMethod(object state)
    {
      TcpListener listener = (TcpListener)state;

      while (_isRunning)
      {
        try
        {
          if (!listener.Pending())
          {
            Thread.Sleep(100);
            continue;
          }

          TcpClient client = listener.AcceptTcpClient();
          ThreadPool.QueueUserWorkItem(ProcessClientConnection, client);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
        {
          break;
        }
        catch (Exception ex)
        {
          Logger.Error(ex, "Error accepting HL7 client connection");
          Thread.Sleep(500);
        }
      }
    }

    private void ProcessClientConnection(object state)
    {
      TcpClient client = (TcpClient)state;
      string remoteEndpoint = client.Client.RemoteEndPoint != null ? client.Client.RemoteEndPoint.ToString() : "unknown";

      Logger.Information("HL7 connection established from {RemoteEndpoint}", remoteEndpoint);

      NetworkStream networkStream = null;

      try
      {
        networkStream = client.GetStream();
        networkStream.ReadTimeout = 30000;
        networkStream.WriteTimeout = 30000;

        byte[] buffer = new byte[4096];
        StringBuilder messageBuilder = new StringBuilder();

        while (_isRunning && client.Connected)
        {
          int bytesRead = networkStream.Read(buffer, 0, buffer.Length);
          if (bytesRead <= 0)
          {
            break;
          }

          messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

          string currentText = messageBuilder.ToString();
          int startIndex = currentText.IndexOf(START_OF_BLOCK);
          if (startIndex < 0)
          {
            continue;
          }

          int endIndex = currentText.IndexOf(END_OF_BLOCK, startIndex + 1);
          if (endIndex < 0)
          {
            continue;
          }

          string hl7Payload = currentText.Substring(startIndex + 1, endIndex - startIndex - 1);
          messageBuilder.Remove(0, endIndex + 1);

          Logger.Information("Received HL7 message ({Bytes} bytes) from {RemoteEndpoint}", hl7Payload.Length, remoteEndpoint);
          Logger.Debug("HL7 message:\n{Message}", hl7Payload.Replace("\r", "\r\n"));

          ProcessHl7Message(hl7Payload, networkStream, remoteEndpoint);
        }
      }
      catch (Exception ex)
      {
        Logger.Error(ex, "Exception while handling HL7 connection from {RemoteEndpoint}", remoteEndpoint);
      }
      finally
      {
        try
        {
          if (networkStream != null)
          {
            networkStream.Close();
            networkStream.Dispose();
          }
        }
        catch
        {
          // ignore cleanup exceptions
        }

        client.Close();
        Logger.Information("Closed HL7 connection from {RemoteEndpoint}", remoteEndpoint);
      }
    }

    private void ProcessHl7Message(string hl7MessageData, NetworkStream networkStream, string remoteEndpoint)
    {
      string ackMessage;
      string ackCode = "AA";
      string ackError = string.Empty;

      try
      {
        OruProcessingResult result = _processor.HandleIncomingAsync(hl7MessageData, CancellationToken.None).GetAwaiter().GetResult();

        if (!result.ShouldAckSuccess)
        {
          ackCode = "AE";
          ackError = result.ErrorMessage;
          Logger.Error("Failed to process ORU message from {RemoteEndpoint}: {Reason}", remoteEndpoint, ackError);
        }
        else if (result.Status == OruProcessingStatus.Deferred)
        {
          ackCode = string.IsNullOrEmpty(_config.HL7.DeferredAckCode) ? "AA" : _config.HL7.DeferredAckCode;
          Logger.Warning("Deferred ORU message from {RemoteEndpoint}; send queued", remoteEndpoint);
        }
        else if (result.Status == OruProcessingStatus.Success)
        {
          Logger.Information("Processed ORU message: {Summary}", result.Message != null ? result.Message.SummaryForLog() : "Message processed");
        }
        else if (result.Status == OruProcessingStatus.AlreadyProcessed)
        {
          Logger.Information("Acknowledging previously processed ORU message {MessageId}", result.Message != null ? result.Message.UUID : "unknown");
        }

        ackMessage = CreateAcknowledgementMessage(hl7MessageData, ackCode, ackError);
      }
      catch (Exception ex)
      {
        Logger.Error(ex, "Unexpected error while processing HL7 message from {RemoteEndpoint}", remoteEndpoint);
        ackMessage = CreateAcknowledgementMessage(hl7MessageData, "AE", ex.Message);
      }

      try
      {
        byte[] responseBytes = Encoding.UTF8.GetBytes(ackMessage);
        networkStream.Write(responseBytes, 0, responseBytes.Length);
        networkStream.Flush();
        Logger.Information("Sent HL7 ACK to {RemoteEndpoint} with code {AckCode}", remoteEndpoint, ackCode);
      }
      catch (Exception ex)
      {
        Logger.Error(ex, "Failed to send HL7 ACK to {RemoteEndpoint}", remoteEndpoint);
      }
    }

    private string CreateAcknowledgementMessage(string incomingMessage, string ackCode, string errorText)
    {
      if (string.IsNullOrEmpty(incomingMessage))
      {
        return CreateDefaultAcknowledgement("", ackCode, string.IsNullOrEmpty(errorText) ? "Invalid HL7" : errorText);
      }

      try
      {
        string messageControlId = GetMessageControlId(incomingMessage);

        string sendingApp = string.Empty;
        string sendingFacility = string.Empty;
        string receivingApp = string.Empty;
        string receivingFacility = string.Empty;

        string[] segments = incomingMessage.Split(CARRIAGE_RETURN);
        if (segments.Length > 0)
        {
          string[] fields = segments[0].Split(FIELD_DELIMITER);
          if (fields.Length > 5)
          {
            sendingApp = fields[4];
            sendingFacility = fields[5];
            receivingApp = fields[2];
            receivingFacility = fields[3];
          }
        }

        StringBuilder builder = new StringBuilder();
        builder.Append(START_OF_BLOCK)
          .AppendFormat("MSH|^~\\&|{0}|{1}|{2}|{3}|{4:yyyyMMddHHmmss}||ACK|ACK{5}|P|2.3", sendingApp, sendingFacility, receivingApp, receivingFacility, DateTime.Now, messageControlId)
          .Append(CARRIAGE_RETURN)
          .AppendFormat("MSA|{0}|{1}", ackCode, messageControlId);

        if (!string.IsNullOrEmpty(errorText) && (ackCode == "AE" || ackCode == "AR"))
        {
          builder.Append("|").Append(errorText.Replace("|", "\\|"));
        }

        builder.Append(CARRIAGE_RETURN)
          .Append(END_OF_BLOCK)
          .Append(CARRIAGE_RETURN);

        return builder.ToString();
      }
      catch (Exception ex)
      {
        Logger.Error(ex, "Failed to build HL7 acknowledgement message");
        return CreateDefaultAcknowledgement("", "AR", "Internal error");
      }
    }

    private static string CreateDefaultAcknowledgement(string messageId, string ackCode, string errorText)
    {
      StringBuilder builder = new StringBuilder();
      builder.Append(START_OF_BLOCK)
        .AppendFormat("MSH|^~\\&||||{0:yyyyMMddHHmmss}||ACK|ACK{1}|P|2.3", DateTime.Now, DateTime.Now.Ticks)
        .Append(CARRIAGE_RETURN)
        .AppendFormat("MSA|{0}|{1}|{2}", ackCode, messageId, errorText)
        .Append(CARRIAGE_RETURN)
        .Append(END_OF_BLOCK)
        .Append(CARRIAGE_RETURN);

      return builder.ToString();
    }

    private string GetMessageControlId(string incomingMessage)
    {
      try
      {
        string[] segments = incomingMessage.Split(CARRIAGE_RETURN);
        string[] fields = segments[0].Split(FIELD_DELIMITER);

        if (fields.Length > MESSAGE_CONTROL_ID_LOCATION)
        {
          return fields[MESSAGE_CONTROL_ID_LOCATION];
        }
      }
      catch (Exception ex)
      {
        Logger.Error(ex, "Could not extract message control ID");
      }

      return DateTime.Now.Ticks.ToString();
    }

    private static IEnumerable<IPAddress> LocalIpAddresses()
    {
      List<IPAddress> addresses = new List<IPAddress> { IPAddress.Loopback };
      if (!NetworkInterface.GetIsNetworkAvailable())
      {
        return addresses;
      }

      try
      {
        IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
        addresses.AddRange(host.AddressList.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork));
      }
      catch
      {
        // ignore
      }

      return addresses;
    }
  }
}
