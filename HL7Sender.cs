using System;
using System.Net.Sockets;
using System.Text;

namespace OrderORM
{
    public class HL7Sender
    {
        public static bool SendOrm(string ormMessage, string host, int port)
        {
            try
            {
                Console.WriteLine($"{DateTime.Now} - Sending ORM to {host}:{port}");
                // Wrap message in MLLP envelope
                string mllpMessage = $"\x0B{ormMessage}\x1C\x0D";

                using (var client = new TcpClient(host, port))
                using (var stream = client.GetStream())
                {
                    byte[] data = Encoding.ASCII.GetBytes(mllpMessage);
                    stream.Write(data, 0, data.Length);

                    // Read acknowledgment
                    byte[] buffer = new byte[1024];
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    string response = Encoding.ASCII.GetString(buffer, 0, bytesRead);

                    if (response.Contains("AA"))
                    {
                        Console.WriteLine($"{DateTime.Now} - ORM sent successfully to {host}:{port}");
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"{DateTime.Now} - ERROR: HL7 server rejected message: {response}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now} - ERROR sending ORM: {ex.Message}");
                return false;
            }
        }
    }
}