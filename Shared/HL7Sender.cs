using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Serilog;
using System.Collections.Generic;

namespace DICOM7.Shared
{
  /// <summary>
  /// Handles sending HL7 messages to a receiver
  /// </summary>
  public static class HL7Sender
  {
    /// <summary>
    /// Sends an ORM message to the specified HL7 receiver
    /// </summary>
    /// <param name="config">Application configuration</param>
    /// <param name="message">The HL7 message to send</param>
    /// <param name="host">The host to send to</param>
    /// <param name="port">The port to send to</param>
    /// <param name="senderName">The sender application name</param>
    /// <returns>True if the message was sent successfully, false otherwise</returns>
    public static bool SendOrm<T>(T config, string message, string host, int port, string senderName = null) where T : class
    {
      try
      {
        Log.Information("Sending ORM to {Host}:{Port}", host, port);

        // Replace template values with configured values
        string processedMessage = ReplaceTemplateValues(config, message);

        // Wrap message in MLLP envelope
        string mllpMessage = $"{(char)0x0B}{processedMessage}{(char)0x1C}{(char)0x0D}";

        using TcpClient client = new TcpClient();
        // Set connection timeout
        IAsyncResult result = client.BeginConnect(host, port, null, null);
        bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5));

        if (!success)
        {
          Log.Error("Failed to connect to HL7 receiver {Host}:{Port} (timeout)", host, port);
          return false;
        }

        // Complete the connection
        client.EndConnect(result);

        using NetworkStream stream = client.GetStream();
        // Set read/write timeout
        stream.ReadTimeout = 5000;

        // Send the message
        byte[] data = Encoding.UTF8.GetBytes(mllpMessage);
        stream.Write(data, 0, data.Length);
        stream.Flush();

        Log.Debug("Sent {ByteCount} bytes", data.Length);

        // Check if we need to wait for acknowledgment
        bool waitForAck = true;
        try
        {
          // Try to get WaitForAck property from config
          object property = config.GetType().GetProperty("HL7")?.GetValue(config, null);
          if (property != null)
          {
            waitForAck = (bool)(property.GetType().GetProperty("WaitForAck")?.GetValue(property, null) ?? true);
          }
        }
        catch
        {
          // Default to true if we can't get the property
          waitForAck = true;
        }

        if (waitForAck)
        {
          try
          {
            // Try to read acknowledgment
            byte[] buffer = new byte[4096];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            // Remove MLLP wrappers
            response = response.Trim((char)0x0B, (char)0x1C, (char)0x0D);

            if (response.Contains("AA") || response.Contains("ACK"))
            {
              Log.Information("ORM sent successfully to {Host}:{Port}", host, port);
              return true;
            }

            if (string.IsNullOrWhiteSpace(response))
            {
              // Empty response but message was sent - consider successful
              Log.Warning("Empty response from receiver, but message was sent. Considering successful");
              return true;
            }

            Log.Error("HL7 server rejected message: {Response}", response);
            return false;
          }
          catch (Exception ex) when (ex is IOException || ex is TimeoutException)
          {
            // Timeout receiving response - message was sent, so consider it successful
            Log.Warning("No ACK received (timeout), but message was sent. Considering successful");
            return true;
          }
        }
        else
        {
          Log.Information("Not waiting for ACK (WaitForAck=false). Considering successful");
          return true;
        }
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error sending ORM to {Host}:{Port}: {ErrorMessage}", host, port, ex.Message);
        return false;
      }
    }

    /// <summary>
    /// Sends an ORU message to the specified HL7 receiver
    /// </summary>
    /// <param name="config">Application configuration</param>
    /// <param name="message">The HL7 message to send</param>
    /// <param name="host">The host to send to</param>
    /// <param name="port">The port to send to</param>
    /// <returns>True if the message was sent successfully, false otherwise</returns>
    public static bool SendOru<T>(T config, string message, string host, int port) where T : class
    {
      try
      {
        Log.Information("Sending ORU to {Host}:{Port}", host, port);

        // MLLP wrapping: prepend with VT (0x0B) and append with FS (0x1C) and CR (0x0D)
        string mllpMessage = $"{(char)0x0B}{message}{(char)0x1C}{(char)0x0D}";
        byte[] data = Encoding.UTF8.GetBytes(mllpMessage);

        using (TcpClient client = new TcpClient())
        {
          // Set timeout for connection attempt
          IAsyncResult result = client.BeginConnect(host, port, null, null);
          bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5));

          if (!success)
          {
            Log.Error("Failed to connect to HL7 receiver {Host}:{Port} (timeout)", host, port);
            return false;
          }

          // Complete the connection
          client.EndConnect(result);

          // Send the message
          using NetworkStream stream = client.GetStream();
          stream.Write(data, 0, data.Length);
          stream.Flush();

          // Check if we need to wait for acknowledgment
          bool waitForAck = true;
          try
          {
            // Try to get WaitForAck property from config
            object property = config.GetType().GetProperty("HL7")?.GetValue(config, null);
            if (property != null)
            {
              waitForAck = (bool)(property.GetType().GetProperty("WaitForAck")?.GetValue(property, null) ?? true);
            }
          }
          catch
          {
            // Default to true if we can't get the property
            waitForAck = true;
          }

          if (waitForAck)
          {
            byte[] buffer = new byte[4096];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            // Remove MLLP wrappers
            response = response.Trim((char)0x0B, (char)0x1C, (char)0x0D);

            Log.Debug("Received HL7 acknowledgment: {Response}", response);
          }
        }

        Log.Information("Successfully sent ORU message to {Host}:{Port}", host, port);
        return true;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error sending ORU message to {Host}:{Port}: {ErrorMessage}", host, port, ex.Message);
        return false;
      }
    }

    /// <summary>
    /// Cleans an HL7 message by removing empty lines and leading/trailing spaces from lines
    /// </summary>
    /// <param name="message">The HL7 message to clean</param>
    /// <returns>The cleaned HL7 message</returns>
    private static string CleanHL7Message(string message)
    {
      if (string.IsNullOrEmpty(message))
      {
        return message;
      }

      // Normalize line endings
      string normalized = Regex.Replace(message, @"\r\n|\n\r|\n|\r", "\r");

      // Process line by line to remove leading/trailing spaces and empty lines
      string[] lines = normalized.Split('\r');
      List<string> cleanedLines = [];

      foreach (string line in lines)
      {
        string trimmedLine = line.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedLine))
        {
          cleanedLines.Add(trimmedLine);
        }
      }

      // Join lines with carriage returns (standard HL7 line separator)
      return string.Join("\r", cleanedLines);
    }

    /// <summary>
    /// Sends an ORU message to the specified HL7 receiver asynchronously
    /// </summary>
    /// <param name="config">Application configuration</param>
    /// <param name="message">The HL7 message to send</param>
    /// <param name="host">The host to send to</param>
    /// <param name="port">The port to send to</param>
    /// <returns>True if the message was sent successfully, false otherwise</returns>
    public static async Task<bool> SendOruAsync<T>(T config, string message, string host, int port) where T : class
    {
      try
      {
        Log.Information("Sending ORU to {Host}:{Port} asynchronously", host, port);

        // Clean the message before sending
        string cleanedMessage = CleanHL7Message(message);

        // MLLP wrapping: prepend with VT (0x0B) and append with FS (0x1C) and CR (0x0D)
        string mllpMessage = $"{(char)0x0B}{cleanedMessage}{(char)0x1C}{(char)0x0D}";
        byte[] data = Encoding.UTF8.GetBytes(mllpMessage);

        using (TcpClient client = new TcpClient())
        {
          try
          {
            await client.ConnectAsync(host, port).ConfigureAwait(false);
          }
          catch (Exception ex)
          {
            Log.Error(ex, "Failed to connect to HL7 receiver {Host}:{Port}", host, port);
            return false;
          }

          // Send the message
          using NetworkStream stream = client.GetStream();
          await stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
          await stream.FlushAsync().ConfigureAwait(false);

          // Check if we need to wait for acknowledgment
          bool waitForAck = true;
          try
          {
            // Try to get WaitForAck property from config
            object property = config.GetType().GetProperty("HL7")?.GetValue(config, null);
            if (property != null)
            {
              waitForAck = (bool)(property.GetType().GetProperty("WaitForAck")?.GetValue(property, null) ?? true);
            }
          }
          catch
          {
            // Default to true if we can't get the property
            waitForAck = true;
          }

          if (waitForAck)
          {
            byte[] buffer = new byte[4096];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            // Remove MLLP wrappers
            response = response.Trim((char)0x0B, (char)0x1C, (char)0x0D);

            Log.Debug("Received HL7 acknowledgment: {Response}", response);
          }
        }

        Log.Information("Successfully sent ORU message to {Host}:{Port}", host, port);
        return true;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error sending ORU message to {Host}:{Port}: {ErrorMessage}", host, port, ex.Message);
        return false;
      }
    }

    /// <summary>
    /// Sends an ORM message to the specified HL7 receiver asynchronously
    /// </summary>
    /// <param name="config">Application configuration</param>
    /// <param name="message">The HL7 message to send</param>
    /// <param name="host">The host to send to</param>
    /// <param name="port">The port to send to</param>
    /// <param name="senderName">The sender application name</param>
    /// <returns>True if the message was sent successfully, false otherwise</returns>
    public static async Task<bool> SendOrmAsync<T>(T config, string message, string host, int port, string senderName = null) where T : class
    {
      try
      {
        Log.Information("Sending ORM to {Host}:{Port} asynchronously", host, port);

        // Replace template values with configured values
        string processedMessage = ReplaceTemplateValues(config, message);

        // Wrap message in MLLP envelope
        string mllpMessage = $"{(char)0x0B}{processedMessage}{(char)0x1C}{(char)0x0D}";

        using TcpClient client = new TcpClient();
        try
        {
          await client.ConnectAsync(host, port).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
          Log.Error(ex, "Failed to connect to HL7 receiver {Host}:{Port}", host, port);
          return false;
        }

        using NetworkStream stream = client.GetStream();

        // Send the message
        byte[] data = Encoding.UTF8.GetBytes(mllpMessage);
        await stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);

        Log.Debug("Sent {ByteCount} bytes", data.Length);

        // Check if we need to wait for acknowledgment
        bool waitForAck = true;
        try
        {
          // Try to get WaitForAck property from config
          object property = config.GetType().GetProperty("HL7")?.GetValue(config, null);
          if (property != null)
          {
            waitForAck = (bool)(property.GetType().GetProperty("WaitForAck")?.GetValue(property, null) ?? true);
          }
        }
        catch
        {
          // Default to true if we can't get the property
          waitForAck = true;
        }

        if (waitForAck)
        {
          try
          {
            // Try to read acknowledgment
            byte[] buffer = new byte[4096];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            // Remove MLLP wrappers
            response = response.Trim((char)0x0B, (char)0x1C, (char)0x0D);

            if (response.Contains("AA") || response.Contains("ACK"))
            {
              Log.Information("ORM sent successfully to {Host}:{Port}", host, port);
              return true;
            }

            if (string.IsNullOrWhiteSpace(response))
            {
              // Empty response but message was sent - consider successful
              Log.Warning("Empty response from receiver, but message was sent. Considering successful");
              return true;
            }

            Log.Error("HL7 server rejected message: {Response}", response);
            return false;
          }
          catch (Exception ex) when (ex is IOException || ex is TimeoutException)
          {
            // Timeout receiving response - message was sent, so consider it successful
            Log.Warning("No ACK received (timeout), but message was sent. Considering successful");
            return true;
          }
        }
        else
        {
          Log.Information("Not waiting for ACK (WaitForAck=false). Considering successful");
          return true;
        }
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error sending ORM to {Host}:{Port}: {ErrorMessage}", host, port, ex.Message);
        return false;
      }
    }

    /// <summary>
    /// Replaces template values in the HL7 message with configured values
    /// </summary>
    /// <param name="config">Application configuration</param>
    /// <param name="message">The original HL7 message</param>
    /// <returns>The processed HL7 message with template values replaced</returns>
    private static string ReplaceTemplateValues<T>(T config, string message) where T : class
    {
      string senderName = "DICOM7";
      string receiverName = "RECEIVER_APPLICATION";
      string receiverFacility = "RECEIVER_FACILITY";

      try
      {
        // Try to get HL7 configuration from config object
        object hl7Config = config.GetType().GetProperty("HL7")?.GetValue(config, null);
        if (hl7Config != null)
        {
          senderName = (string)(hl7Config.GetType().GetProperty("SenderName")?.GetValue(hl7Config, null) ?? "DICOM7");
          receiverName = (string)(hl7Config.GetType().GetProperty("ReceiverName")?.GetValue(hl7Config, null) ?? "RECEIVER_APPLICATION");
          receiverFacility = (string)(hl7Config.GetType().GetProperty("ReceiverFacility")?.GetValue(hl7Config, null) ?? "RECEIVER_FACILITY");
        }
      }
      catch
      {
        // Use defaults if we can't get the properties
      }

      // Split the message into lines to handle each segment separately
      string[] lines = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
      for (int i = 0; i < lines.Length; i++)
      {
        string line = lines[i];

        // Handle MSH segment specifically
        if (line.StartsWith("MSH|"))
        {
          // Split the MSH segment into fields
          string[] fields = line.Split('|');

          // Check if we have enough fields to process
          if (fields.Length < 5)
            continue;

          // Field 3 is the sending application
          if (fields[2].Equals("DICOM7", StringComparison.OrdinalIgnoreCase))
          {
            fields[2] = senderName;
          }

          // Fields 4 and 5 are the receiving application and facility
          if (fields[3].Equals("RECEIVER_APPLICATION", StringComparison.OrdinalIgnoreCase))
          {
            fields[3] = receiverName;
          }

          if (fields.Length >= 6 && fields[4].Equals("RECEIVER_FACILITY", StringComparison.OrdinalIgnoreCase))
          {
            fields[4] = receiverFacility;
          }

          // Reconstruct the MSH segment
          lines[i] = string.Join("|", fields);
        }
        else
        {
          // For non-MSH segments, use regex replacement
          lines[i] = Regex.Replace(line, @"\|DICOM7\|", $"|{senderName}|", RegexOptions.IgnoreCase);
          lines[i] = Regex.Replace(lines[i], @"\|RECEIVER_APPLICATION\|", $"|{receiverName}|", RegexOptions.IgnoreCase);
          lines[i] = Regex.Replace(lines[i], @"\|RECEIVER_FACILITY\|", $"|{receiverFacility}|", RegexOptions.IgnoreCase);
        }
      }

      // Reconstruct the message
      string result = string.Join("\r", lines);

      Log.Debug("Replaced template values: Sender={SenderName}, Receiver={ReceiverName}, Facility={Facility}",
          senderName, receiverName, receiverFacility);

      return result;
    }
  }
}
