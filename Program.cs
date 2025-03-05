using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OrderORM
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Try to load the config from the common app folder first
            string commonConfigPath = AppConfig.GetConfigFilePath("config.yaml");
            string configPath = null;
            Config config = null;

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .Build();

            // First try to load from common app folder
            if (File.Exists(commonConfigPath))
            {
                Console.WriteLine($"{DateTime.Now} - Loading configuration from common location: '{commonConfigPath}'");
                try
                {
                    config = deserializer.Deserialize<Config>(File.ReadAllText(commonConfigPath));
                    configPath = commonConfigPath;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{DateTime.Now} - Error loading configuration from common location: {ex.Message}");
                    // Will fall back to local config
                }
            }

            // If not found or failed to load, try local config
            if (config == null)
            {
                string localConfigPath = Path.GetFullPath("config.yaml");
                Console.WriteLine($"{DateTime.Now} - Loading configuration from local path: '{localConfigPath}'");

                try
                {
                    config = deserializer.Deserialize<Config>(File.ReadAllText(localConfigPath));
                    configPath = localConfigPath;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{DateTime.Now} - Error loading local configuration: {ex.Message}");
                    throw; // Cannot continue without configuration
                }
            }

            // Ensure Cache config is initialized
            if (config.Cache == null)
            {
                config.Cache = new CacheConfig
                {
                    RetentionDays = 30 // Default retention period
                };
            }

            // Set the configured cache folder if specified in config
            if (config.Cache != null && !string.IsNullOrWhiteSpace(config.Cache.Folder))
            {
                CacheManager.SetConfiguredCacheFolder(config.Cache.Folder);
            }

            // Clean up cache based on retention policy
            CacheManager.CleanUpCache(CacheManager.CacheFolder, config.Cache?.RetentionDays ?? 30);

            // Look for ormTemplate.hl7 in the same folder as config.yaml
            string configDirectory = Path.GetDirectoryName(configPath);
            string templatePath = Path.Combine(configDirectory, "ormTemplate.hl7");

            Console.WriteLine($"{DateTime.Now} - Looking for ORM template at '{templatePath}'");
            var ormTemplate = ORMGenerator.LoadTemplate(templatePath);

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
