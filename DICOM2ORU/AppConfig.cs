using System;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Serilog;

namespace DICOM2ORU
{
    public static class AppConfig
    {
        private static string _commonAppFolder;
        private static string _customBasePath;

        /// <summary>
        /// Sets a custom base path for the application
        /// </summary>
        /// <param name="basePath">The custom base path to use</param>
        public static void SetBasePath(string basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath))
                throw new ArgumentException("Base path cannot be null or empty", nameof(basePath));

            _customBasePath = Path.GetFullPath(basePath);

            // Reset the common app folder so it will be recalculated using the new base path
            _commonAppFolder = null;
        }

        /// <summary>
        /// Gets or sets the common application folder path used for storing configuration and cache data.
        /// If a custom base path is set, it uses that; otherwise:
        /// On Windows, this is under CommonApplicationData; on other platforms, it's under $HOME/.local
        /// </summary>
        public static string CommonAppFolder
        {
            get
            {
                if (!string.IsNullOrEmpty(_commonAppFolder)) return _commonAppFolder;

                // If a custom base path is set, use that as the base folder
                if (!string.IsNullOrEmpty(_customBasePath))
                {
                  _commonAppFolder = _customBasePath;
                  return _commonAppFolder;
                }

                // Use default path if no custom path is set or if there was an error
                string baseFolder;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                }
                else
                {
                    var home = Environment.GetEnvironmentVariable("HOME");
                    if (string.IsNullOrEmpty(home)) home = "/tmp";
                    baseFolder = Path.Combine(home, ".local");
                }

                var tmp = Path.Combine(baseFolder, "Flux Inc", "DICOM7", "DICOM2ORU");
                try
                {
                    if (!Directory.Exists(tmp))
                    {
                        Directory.CreateDirectory(tmp);
                        Log.Information("Created common application folder: {Path}", tmp);
                    }

                    _commonAppFolder = tmp;
                }
                catch (Exception e)
                {
                    _commonAppFolder = null;
                    Log.Error(e, "Failed to create common application folder: {Path}", tmp);
                    throw;
                }

                return _commonAppFolder;
            }
            set => _commonAppFolder = value;
        }

    /// <summary>
    /// Gets the path to the configuration file in the common application folder
    /// </summary>
    /// <param name="fileName">Name of the configuration file (default: config.yaml)</param>
    /// <returns>Full path to the configuration file</returns>
    public static string GetConfigFilePath(string fileName = "config.yaml") => Path.Combine(CommonAppFolder, fileName);
  }
}
