using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using FellowOakDicom;
using Serilog;

namespace DICOM7.ORM2DICOM
{
  /// <summary>
  /// TCP server that listens for and processes incoming HL7 ORM messages
  /// </summary>
  public class Hl7Server
  {
    /* An example ORM message:

MSH|^~\&|HIS|MedCenter|LIS|MedCenter|20060307110114||ORM^O01|MSGID20060307110114|P|2.3
PID|||12001||Jones^John^^^Mr.||19670824|M|||123 West St.^^Denver^CO^80020^USA|||||||
PV1||O|OP^PAREG^||||2342^Jones^Bob|||OP|||||||||2|||||||||||||||||||||||||20060307110111|
ORC|NW|20060307110114
OBR|1|20060307110114||003038^Urinalysis^L|||20060307110114

 */

    private static readonly ILogger Logger = Log.ForContext<Hl7Server>();

    private List<(IPAddress IpAddress, int Port, TcpListener Listener, Thread Thread)> _listenersAndThreads;
    private volatile bool _isRunning;
    private readonly Config _config;

    private const char END_OF_BLOCK = '\u001c'; // File separator
    private const char START_OF_BLOCK = '\u000b'; // Vertical tab
    private const char CARRIAGE_RETURN = (char)13; // Carriage return
    private const char FIELD_DELIMITER = '|';
    private const int MESSAGE_CONTROL_ID_LOCATION = 9;

    /// <summary>
    /// Gets whether the HL7 server is currently running
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Creates a new instance of the HL7 server
    /// </summary>
    /// <param name="config">Application configuration</param>
    public Hl7Server(Config config)
    {
      _config = config ?? throw new ArgumentNullException(nameof(config));
      _listenersAndThreads = new List<(IPAddress, int, TcpListener, Thread)>();
    }

    /// <summary>
    /// Gets all local IP addresses available for listening
    /// </summary>
    public static List<IPAddress> LocalIPAddresses()
    {
      List<IPAddress> ips = new List<IPAddress> { IPAddress.Loopback };
      if (!NetworkInterface.GetIsNetworkAvailable()) return ips;

      try
      {
        IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());

        ips.AddRange(host
          .AddressList
          .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork));
      }
      catch (Exception ex)
      {
        Logger.Error(ex, "Error getting local IP addresses");
      }

      return ips;
    }

    /// <summary>
    /// Starts the HL7 server
    /// </summary>
    public void Start()
    {
      if (_isRunning)
      {
        Logger.Warning("HL7 server is already running");
        return;
      }

      try
      {
        int port = _config.HL7.ListenPort;
        Logger.Information("Starting HL7 server on port {Port}...", port);

        _listenersAndThreads = new List<(IPAddress, int, TcpListener, Thread)>();
        _isRunning = true;

        // Get the listen IP address from config
        IPAddress configIp = null;
        if (!string.IsNullOrEmpty(_config.HL7.ListenIP) && _config.HL7.ListenIP != "0.0.0.0")
        {
          if (IPAddress.TryParse(_config.HL7.ListenIP, out configIp))
          {
            StartListener(configIp, port);
          }
          else
          {
            Logger.Error("Invalid IP address in configuration: {ListenIP}", _config.HL7.ListenIP);
            StartListenersOnAllInterfaces(port);
          }
        }
        else
        {
          // Listen on all interfaces
          StartListenersOnAllInterfaces(port);
        }

        Logger.Information("HL7 server started successfully on port {Port}", port);
      }
      catch (Exception ex)
      {
        _isRunning = false;
        Logger.Error(ex, "Error starting HL7 server");
        throw;
      }
    }

    private void StartListenersOnAllInterfaces(int port)
    {
      foreach (IPAddress ip in LocalIPAddresses())
      {
        try
        {
          StartListener(ip, port);
        }
        catch (Exception ex)
        {
          Logger.Error(ex, "Failed to start listener on {IpAddress}:{Port}", ip, port);
        }
      }
    }

    private void StartListener(IPAddress ip, int port)
    {
      TcpListener listener = new TcpListener(ip, port);
      listener.Start();

      Thread thread = new Thread(ListenerThreadMethod)
      {
        IsBackground = true,
        Name = $"HL7Listener-{ip}-{port}"
      };

      thread.Start(listener);
      _listenersAndThreads.Add((ip, port, listener, thread));

      Logger.Information("HL7 server listening on {IpAddress}:{Port}", ip, port);
    }

    /// <summary>
    /// Stops the HL7 server
    /// </summary>
    public void Stop()
    {
      if (!_isRunning)
      {
        return;
      }

      Logger.Information("Stopping HL7 server...");
      _isRunning = false;

      foreach ((IPAddress ip, int port, TcpListener listener, Thread thread) in _listenersAndThreads)
      {
        try
        {
          listener.Stop();
          Logger.Information("Stopped listener on {IpAddress}:{Port}", ip, port);
        }
        catch (Exception ex)
        {
          Logger.Error(ex, "Error stopping listener on {IpAddress}:{Port}", ip, port);
        }
      }

      // Clear the list
      _listenersAndThreads.Clear();
      Logger.Information("HL7 server stopped");
    }

    private void ListenerThreadMethod(object state)
    {
      TcpListener listener = (TcpListener)state;

      while (_isRunning)
      {
        try
        {
          // AcceptTcpClient() blocks until a client connects
          // Add a timeout to check _isRunning periodically
          if (listener.Pending())
          {
            TcpClient client = listener.AcceptTcpClient();
            ThreadPool.QueueUserWorkItem(ProcessClientConnection, client);
          }
          else
          {
            // Sleep for a short time to avoid busy waiting
            Thread.Sleep(100);
          }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
        {
          // Expected when stopping the server
          break;
        }
        catch (Exception ex)
        {
          Logger.Error(ex, "Error accepting client connection");
          // Short delay to prevent tight error loop
          Thread.Sleep(1000);
        }
      }
    }

    private void ProcessClientConnection(object state)
    {
      TcpClient client = (TcpClient)state;
      string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

      Logger.Information("Client connection established from {RemoteEndpoint}", endpoint);

      byte[] receivedByteBuffer = new byte[4096]; // Increased buffer size
      NetworkStream netStream = null;

      try
      {
        netStream = client.GetStream();
        netStream.ReadTimeout = 30000; // 30 second timeout
        netStream.WriteTimeout = 30000;
#if DEBUG
        Logger.Debug("Ready to receive HL7 data from {RemoteEndpoint} (ReadTimeout={ReadTimeout}ms, WriteTimeout={WriteTimeout}ms)",
          endpoint, netStream.ReadTimeout, netStream.WriteTimeout);
#endif

        // Keep receiving data until the client closes connection
        int bytesReceived;
        string hl7Data = string.Empty;
        bool receivedAnyData = false;

        while (_isRunning && client.Connected && (bytesReceived = netStream.Read(receivedByteBuffer, 0, receivedByteBuffer.Length)) > 0)
        {
          Logger.Debug("Received {BytesReceived} bytes from {RemoteEndpoint}", bytesReceived, endpoint);

#if DEBUG
          Logger.Debug("Chunk of {Bytes} bytes received from {RemoteEndpoint}: {Preview}",
            bytesReceived, endpoint, FormatPayloadPreview(receivedByteBuffer, bytesReceived));
#endif

          hl7Data += Encoding.UTF8.GetString(receivedByteBuffer, 0, bytesReceived);
          receivedAnyData = true;

          // Find start of MLLP frame (VT character)
          int startOfMllpEnvelope = hl7Data.IndexOf(START_OF_BLOCK);

          if (startOfMllpEnvelope >= 0)
          {
            // Look for the end of the frame (FS character)
            int end = hl7Data.IndexOf(END_OF_BLOCK);
#if DEBUG
            if (end < 0)
            {
              Logger.Debug("Awaiting MLLP end marker from {RemoteEndpoint}. Buffered {BufferedCharacters} characters.",
                endpoint, hl7Data.Length);
            }
#endif
            if (end >= startOfMllpEnvelope) // End of block received
            {
              // Extract the complete message
              string hl7MessageData = hl7Data.Substring(startOfMllpEnvelope + 1, end - startOfMllpEnvelope - 1);

              // Log the received message (with line breaks for better readability)
              Logger.Information("Received HL7 message ({Length} bytes) from {RemoteEndpoint}",
                hl7MessageData.Length, endpoint);
              Logger.Debug("Message content:\n{Message}", hl7MessageData.Replace("\r", "\r\n"));

              // Process the HL7 message
              ProcessHl7Message(hl7MessageData, netStream, endpoint);

              // Reset for the next message
              hl7Data = hl7Data.Substring(end + 1);
            }
          }
        }
#if DEBUG
        if (!receivedAnyData)
        {
          Logger.Debug("Connection from {RemoteEndpoint} closed without transmitting HL7 payload", endpoint);
        }
#endif
      }
      catch (Exception ex)
      {
        Logger.Error(ex, "Error processing client connection from {RemoteEndpoint}", endpoint);
      }
      finally
      {
        try
        {
          netStream?.Close();
          netStream?.Dispose();
          client.Close();
          Logger.Information("Closed connection from {RemoteEndpoint}", endpoint);
        }
        catch (Exception ex)
        {
          Logger.Error(ex, "Error closing client connection from {RemoteEndpoint}", endpoint);
        }
      }
    }

    private void ProcessHl7Message(string hl7MessageData, NetworkStream netStream, string remoteEndpoint)
    {
      if (!TryExtractMessageType(hl7MessageData, out string messageType))
      {
        Logger.Warning("Discarded payload from {RemoteEndpoint}: message did not include a valid MSH segment", remoteEndpoint);
        try
        {
          SendAcknowledgement(netStream, CreateDefaultAcknowledgement(string.Empty, "AR", "Invalid HL7 message format"), remoteEndpoint);
        }
        catch (Exception ackEx)
        {
          Logger.Error(ackEx, "Failed to send invalid-format acknowledgment to {RemoteEndpoint}", remoteEndpoint);
        }

        return;
      }

      if (!IsSupportedMessageType(messageType))
      {
        Logger.Warning("Rejected HL7 message type {MessageType} from {RemoteEndpoint}", messageType, remoteEndpoint);
        try
        {
          SendAcknowledgement(netStream, CreateAcknowledgementMessage(hl7MessageData, "AR", $"Unsupported message type {messageType}"), remoteEndpoint);
        }
        catch (Exception ackEx)
        {
          Logger.Error(ackEx, "Failed to send unsupported-type acknowledgment to {RemoteEndpoint}", remoteEndpoint);
        }

        return;
      }

      try
      {
        // Create and save CachedORM
        CachedORM cachedOrm = new CachedORM(hl7MessageData);

        // Validate the message by converting to DicomDataset
        DicomDataset dicomDataset = cachedOrm.AsDicomDataset();

        if (dicomDataset == null)
        {
          Logger.Warning("Received HL7 message from {RemoteEndpoint} produced a null dataset, not saving", remoteEndpoint);
        }
        else
        {
          // Save the message to the cache
          if (cachedOrm.Save())
          {
            Logger.Information("Saved ORM message with UUID {UUID} to cache", cachedOrm.UUID);
          }
          else
          {
            Logger.Warning("Failed to save ORM message from {RemoteEndpoint}", remoteEndpoint);
          }
        }

        // Create acknowledgment message
        string ackMessage = CreateAcknowledgementMessage(hl7MessageData, dicomDataset == null ? "AE" : "AA");

        // Send acknowledgment
        SendAcknowledgement(netStream, ackMessage, remoteEndpoint);
      }
      catch (Exception ex)
      {
        Logger.Error(ex, "Error processing HL7 message from {RemoteEndpoint}", remoteEndpoint);

        // Send error acknowledgment if stream is still writable
        try
        {
          string errorAck = CreateAcknowledgementMessage(hl7MessageData, "AR", ex.Message);
          SendAcknowledgement(netStream, errorAck, remoteEndpoint);
        }
        catch (Exception ackEx)
        {
          Logger.Error(ackEx, "Failed to send error acknowledgment to {RemoteEndpoint}", remoteEndpoint);
        }
      }
    }

    private string CreateAcknowledgementMessage(string incomingHl7Message, string ackCode = "AA", string errorText = "")
    {
      if (string.IsNullOrEmpty(incomingHl7Message))
      {
        return CreateDefaultAcknowledgement("", ackCode, "Invalid HL7 message");
      }

      try
      {
        // Get the message control ID
        string messageControlId = GetMessageControlID(incomingHl7Message);

        // Extract sending and receiving info from original message
        string sendingApp = "";
        string sendingFacility = "";
        string receivingApp = "";
        string receivingFacility = "";

        try
        {
          string[] segments = incomingHl7Message.Split(CARRIAGE_RETURN);
          if (segments.Length > 0)
          {
            string[] fields = segments[0].Split(FIELD_DELIMITER);
            if (fields.Length > 5)
            {
              // For ACK, we swap the sending and receiving entities
              // MSH fields: MSH|^~\&|SendingApp|SendingFacility|ReceivingApp|ReceivingFacility|...
              sendingApp = fields[4];       // Field 5 (index 4) is ReceivingApp in original message
              sendingFacility = fields[5];  // Field 6 (index 5) is ReceivingFacility in original message
              receivingApp = fields[2];     // Field 3 (index 2) is SendingApp in original message
              receivingFacility = fields[3]; // Field 4 (index 3) is SendingFacility in original message
            }
          }
        }
        catch (Exception ex)
        {
          Logger.Warning(ex, "Error extracting sender/receiver information from message");
          // If extraction fails, use empty strings - don't use config values
        }

        // Build acknowledgment message
        StringBuilder ackMessage = new StringBuilder();
        ackMessage.Append(START_OF_BLOCK)
            .Append($"MSH|^~\\&|{sendingApp}|{sendingFacility}|{receivingApp}|{receivingFacility}|{DateTime.Now:yyyyMMddHHmmss}||ACK|ACK{messageControlId}|P|2.3")
            .Append(CARRIAGE_RETURN)
            .Append($"MSA|{ackCode}|{messageControlId}");

        // Add error text if provided
        if (!string.IsNullOrEmpty(errorText) && (ackCode == "AE" || ackCode == "AR"))
        {
            ackMessage.Append($"|{errorText.Replace("|", "\\|")}");
        }

        ackMessage.Append(CARRIAGE_RETURN)
            .Append(END_OF_BLOCK)
            .Append(CARRIAGE_RETURN);

        return ackMessage.ToString();
      }
      catch (Exception ex)
      {
        Logger.Error(ex, "Error creating acknowledgment message");
        return CreateDefaultAcknowledgement("", "AR", "Internal server error");
      }
    }

    private string CreateDefaultAcknowledgement(string messageId, string ackCode, string errorText)
    {
      StringBuilder ackMessage = new StringBuilder();

      ackMessage.Append(START_OF_BLOCK)
          .Append($"MSH|^~\\&||||{DateTime.Now:yyyyMMddHHmmss}||ACK|ACK{DateTime.Now.Ticks}|P|2.3")
          .Append(CARRIAGE_RETURN)
          .Append($"MSA|{ackCode}|{messageId}|{errorText}")
          .Append(CARRIAGE_RETURN)
          .Append(END_OF_BLOCK)
          .Append(CARRIAGE_RETURN);

      return ackMessage.ToString();
    }

    private string GetMessageControlID(string incomingHl7Message)
    {
      try
      {
        // Parse the message into segments using the end of segment separator
        string[] hl7MessageSegments = incomingHl7Message.Split(CARRIAGE_RETURN);

        // Tokenize the MSH segment into fields using the field separator
        string[] hl7FieldsInMshSegment = hl7MessageSegments[0].Split(FIELD_DELIMITER);

        if ((MESSAGE_CONTROL_ID_LOCATION + 1) > hl7FieldsInMshSegment.Length)
        {
          Logger.Warning(
            "Expected Message Control ID in field #{MessageControlIdLocation} but found only {FieldCount} fields in MSH",
            MESSAGE_CONTROL_ID_LOCATION, hl7FieldsInMshSegment.Length);

          return DateTime.Now.Ticks.ToString();
        }
        else
        {
          string mcid = hl7FieldsInMshSegment[MESSAGE_CONTROL_ID_LOCATION];
          return mcid;
        }
      }
      catch (Exception ex)
      {
        Logger.Error(ex, "Error extracting message control ID");
        return DateTime.Now.Ticks.ToString();
      }
    }

#if DEBUG
    private static string FormatPayloadPreview(byte[] buffer, int count)
    {
      string text = Encoding.UTF8.GetString(buffer, 0, count);
      if (text.Length > 256)
      {
        text = text.Substring(0, 256) + "...";
      }

      return text
        .Replace("\r", "\\r")
        .Replace("\n", "\\n");
    }
#endif

    private static void SendAcknowledgement(NetworkStream netStream, string ackMessage, string remoteEndpoint)
    {
      if (netStream == null || !netStream.CanWrite)
      {
        Logger.Warning("Cannot send acknowledgment to {RemoteEndpoint}: stream is not writable", remoteEndpoint);
        return;
      }

      byte[] buffer = Encoding.UTF8.GetBytes(ackMessage);
      netStream.Write(buffer, 0, buffer.Length);
      Logger.Information("Sent acknowledgment to {RemoteEndpoint}", remoteEndpoint);
      Logger.Debug("ACK Message: {Message}", ackMessage.Replace(START_OF_BLOCK.ToString(), "<SB>")
        .Replace(END_OF_BLOCK.ToString(), "<EB>")
        .Replace("\r", "\r\n"));
    }

    private static bool TryExtractMessageType(string hl7MessageData, out string messageType)
    {
      messageType = string.Empty;

      if (string.IsNullOrWhiteSpace(hl7MessageData))
      {
        return false;
      }

      string[] segments = hl7MessageData.Split(CARRIAGE_RETURN);
      if (segments.Length == 0 || !segments[0].StartsWith("MSH", StringComparison.OrdinalIgnoreCase))
      {
        return false;
      }

      string[] fields = segments[0].Split(FIELD_DELIMITER);
      if (fields.Length <= 8)
      {
        return false;
      }

      messageType = fields[8].Trim();
      return !string.IsNullOrEmpty(messageType);
    }

    private static bool IsSupportedMessageType(string messageType)
    {
      if (string.IsNullOrWhiteSpace(messageType))
      {
        return false;
      }

      string normalized = messageType.ToUpperInvariant();
      return normalized.StartsWith("ORM", StringComparison.Ordinal);
    }
  }
}
