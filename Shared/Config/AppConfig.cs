using System;

namespace DICOM7.Shared.Config
{
  /// <summary>
  /// Static access to application configuration for backward compatibility with existing code
  /// </summary>
  public static class AppConfig
  {
    private static BaseAppConfig _instance;

    /// <summary>
    /// Initialize the AppConfig with a specific application name
    /// </summary>
    /// <param name="applicationName">The name of the application</param>
    public static void Initialize(string applicationName)
    {
      _instance = new BaseAppConfig(applicationName);
    }

    /// <summary>
    /// Gets or sets the common application folder path
    /// </summary>
    public static string CommonAppFolder => _instance?.CommonAppFolder ??
        throw new InvalidOperationException("AppConfig must be initialized with Initialize() before use");

    /// <summary>
    /// Sets a custom base path for the application
    /// </summary>
    /// <param name="basePath">The custom base path to use</param>
    public static void SetBasePath(string basePath)
    {
      if (_instance == null)
        throw new InvalidOperationException("AppConfig must be initialized with Initialize() before use");

      _instance.SetBasePath(basePath);
    }

    /// <summary>
    /// Gets the path to the configuration file in the common application folder
    /// </summary>
    /// <param name="fileName">Name of the configuration file (default: config.yaml)</param>
    /// <returns>Full path to the configuration file</returns>
    public static string GetConfigFilePath(string fileName = "config.yaml")
    {
      if (_instance == null)
        throw new InvalidOperationException("AppConfig must be initialized with Initialize() before use");

      return _instance.GetConfigFilePath(fileName);
    }
  }
}
