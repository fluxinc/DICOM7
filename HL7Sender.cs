using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Serilog;

namespace OrderORM
{
    public class HL7Sender
    {
        public static bool SendOrm(Config config, string ormMessage, string host, int port, string senderName = null)
        {
            try
            {
                Log.Information("Sending ORM to {Host}:{Port}", host, port);

                // Replace template values with configured values
                string processedMessage = ReplaceTemplateValues(config, ormMessage);

                // Wrap message in MLLP envelope
                string mllpMessage = $"\x0B{processedMessage}\x1C\x0D";

                using var client = new TcpClient(host, port);
                // Set connection timeout
                client.ReceiveTimeout = 5000;

                using var stream = client.GetStream();
                // Set read/write timeout
                stream.ReadTimeout = 5000;

                // Send the message
                var data = Encoding.ASCII.GetBytes(mllpMessage);
                stream.Write(data, 0, data.Length);

                Log.Debug("Sent {ByteCount} bytes", data.Length);

                try
                {
                    // Try to read acknowledgment
                    byte[] buffer = new byte[1024];
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    string response = Encoding.ASCII.GetString(buffer, 0, bytesRead);

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
                catch (IOException ex)
                {
                    // Timeout receiving response - message was sent, so consider it successful
                    Log.Warning("No ACK received (timeout), but message was sent. Considering successful");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error sending ORM to {Host}:{Port}", host, port);
                return false;
            }
        }

        /// <summary>
        /// Replaces template values in the HL7 message with configured values
        /// </summary>
        /// <param name="message">The original HL7 message</param>
        /// <param name="receiverHost">The receiver host name</param>
        /// <param name="senderName">The sender application name (optional)</param>
        /// <returns>The processed HL7 message with template values replaced</returns>
        private static string ReplaceTemplateValues(Config config, string message)
        {
            string senderName = string.IsNullOrEmpty(config.HL7.SenderName) ? "ORDERORM" : config.HL7.SenderName;
            string receiverName =string.IsNullOrEmpty(config.HL7.ReceiverName) ? "RECEIVER_APPLICATION" : config.HL7.ReceiverName;
            string receiverFacility =string.IsNullOrEmpty(config.HL7.ReceiverFacility) ? "RECEIVER_FACILITY" : config.HL7.ReceiverName;

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
                    if (fields.Length < 5) continue;

                    // Field 3 is the sending application
                    if (fields[2].Equals("ORDERORM", StringComparison.OrdinalIgnoreCase))
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
                    lines[i] = Regex.Replace(line, @"\|ORDERORM\|", $"|{senderName}|", RegexOptions.IgnoreCase);
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
