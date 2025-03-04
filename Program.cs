using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OrderORM
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Load configuration
            Console.WriteLine($"{DateTime.Now} - Loading configuration from 'config.yaml'");
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .Build();
            Config config = deserializer.Deserialize<Config>(File.ReadAllText("config.yaml"));

            // Initialize components
            CacheManager.EnsureCacheFolder(config.Cache.Folder);
            CacheManager.CleanUpCache(config.Cache.Folder, config.Cache.RetentionDays);
            string ormTemplate = OrmGenerator.LoadTemplate(config.OrmTemplatePath);
            var querier = new WorklistQuerier(config, ormTemplate);

            Console.WriteLine($"{DateTime.Now} - Starting HL7 ORM sender script with SCU AE Title '{config.Dicom.ScuAeTitle}'");

            // Main loop
            while (true)
            {
                // First process any pending messages that need to be retried
                querier.ProcessPendingMessages();
                
                // Then query for new messages
                await querier.QueryAsync();

                // The processing of results is now handled in the OnFindResponseReceived method
                // We can optionally access the results here if needed
                var results = querier.GetQueryResults();
                if (!results.Any())
                {
                    Console.WriteLine($"{DateTime.Now} - No new orders found in this query cycle");
                }

                Console.WriteLine($"{DateTime.Now} - Sleeping for {config.QueryInterval} seconds");
                await Task.Delay(TimeSpan.FromSeconds(config.QueryInterval));
            }
        }
    }
}
