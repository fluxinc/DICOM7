using System;
using System.IO;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DICOM7.Shared.Config
{
  /// <summary>
  /// Utility class for loading configuration from YAML files
  /// </summary>
  public static class ConfigLoader
  {
    /// <summary>
    /// Loads configuration from a YAML file using the common pattern in DICOM7 applications
    /// </summary>
    /// <typeparam name="T">The type of configuration to load</typeparam>
    /// <param name="appConfig">The application configuration (for paths)</param>
    /// <returns>The loaded configuration or null if failed</returns>
    public static T LoadConfiguration<T>(BaseAppConfig appConfig) where T : new()
    {
      // Only try to load the config from the common app folder (which is either the --path argument or ProgramData\Flux Inc\DICOM7\appname\)
      string configPath = appConfig.GetConfigFilePath();
      T config = default;

      IDeserializer deserializer = new DeserializerBuilder()
          .WithNamingConvention(PascalCaseNamingConvention.Instance)
          .Build();

      Log.Information("Loading configuration from path: {ConfigPath}", configPath);

      try
      {
        if (File.Exists(configPath))
        {
          config = deserializer.Deserialize<T>(File.ReadAllText(configPath));
          return config;
        }
        else
        {
          Log.Warning("Configuration file not found: {ConfigPath}", configPath);
          // Return default configuration
          return new T();
        }
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error loading configuration");

        // Failed to load configuration, return a default instance
        try
        {
          return new T();
        }
        catch
        {
          return default;
        }
      }
    }
  }
}
