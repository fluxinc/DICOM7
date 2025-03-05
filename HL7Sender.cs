using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace OrderORM
{
    public class HL7Sender
    {
        public static bool SendOrm(string ormMessage, string host, int port, string senderName = null)
        {
            try
            {
                Console.WriteLine($"{DateTime.Now} - Sending ORM to {host}:{port}");

                // Replace template values with configured values
                string processedMessage = ReplaceTemplateValues(ormMessage, host, senderName);

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

                try
                {
                    // Try to read acknowledgment
                    byte[] buffer = new byte[1024];
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    string response = Encoding.ASCII.GetString(buffer, 0, bytesRead);

                    // Debug - log raw response
                    Console.WriteLine($"{DateTime.Now} - Received response: '{response}'");

                    if (response.Contains("AA") || response.Contains("ACK"))
                    {
                        Console.WriteLine($"{DateTime.Now} - ORM sent successfully to {host}:{port}");
                        return true;
                    }

                    if (string.IsNullOrWhiteSpace(response))
                    {
                        // Empty response but message was sent - consider successful
                        Console.WriteLine($"{DateTime.Now} - Empty response from receiver, but message was sent. Considering successful.");
                        return true;
                    }

                    Console.WriteLine($"{DateTime.Now} - ERROR: HL7 server rejected message: {response}");
                    return false;
                }
                catch (IOException ex)
                {
                    // Timeout receiving response - message was sent, so consider it successful
                    Console.WriteLine($"{DateTime.Now} - No ACK received (timeout), but message was sent. Considering successful.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now} - ERROR sending ORM: {ex.Message}");
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
        private static string ReplaceTemplateValues(string message, string receiverHost, string senderName)
        {
            // Default sender name if not provided
            if (string.IsNullOrEmpty(senderName))
            {
                // Try to get from config if available
                try
                {
                    senderName = System.Configuration.ConfigurationManager.AppSettings["SenderName"] ?? "ORDERORM";
                }
                catch
                {
                    senderName = "ORDERORM";
                }
            }

            // Extract receiver name from host if possible, otherwise use "RECEIVER"
            string receiverName = "RECEIVER";
            if (!string.IsNullOrEmpty(receiverHost))
            {
                // Use the hostname part without domain as receiver name if possible
                try
                {
                    string hostPart = receiverHost.Split('.')[0].ToUpperInvariant();
                    if (!string.IsNullOrEmpty(hostPart))
                    {
                        receiverName = hostPart;
                    }
                }
                catch
                {
                    // If parsing fails, keep default
                }
            }

            // Split the message into lines to handle each segment separately
            string[] lines = message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                // Handle MSH segment specifically
                if (line.StartsWith("MSH|"))
                {
                    // Split the MSH segment into fields
                    string[] fields = line.Split('|');

                    // Check if we have enough fields to process
                    if (fields.Length >= 5)
                    {
                        // Field 3 is the sending application
                        if (fields[2].Equals("ORDERORM", StringComparison.OrdinalIgnoreCase))
                        {
                            fields[2] = senderName;
                        }

                        // Fields 4 and 5 are the receiving application and facility
                        if (fields[3].Equals("RECEIVER", StringComparison.OrdinalIgnoreCase))
                        {
                            fields[3] = receiverName;
                        }

                        if (fields.Length >= 6 && fields[4].Equals("RECEIVER", StringComparison.OrdinalIgnoreCase))
                        {
                            fields[4] = receiverName;
                        }

                        // Reconstruct the MSH segment
                        lines[i] = string.Join("|", fields);
                    }
                }
                else
                {
                    // For non-MSH segments, use regex replacement
                    lines[i] = Regex.Replace(line, @"\|ORDERORM\|", $"|{senderName}|", RegexOptions.IgnoreCase);
                    lines[i] = Regex.Replace(lines[i], @"\|RECEIVER\|", $"|{receiverName}|", RegexOptions.IgnoreCase);
                }
            }

            // Reconstruct the message
            string result = string.Join("\r", lines);

            Console.WriteLine($"{DateTime.Now} - Replaced template values: Sender={senderName}, Receiver={receiverName}");

            return result;
        }
    }
}
