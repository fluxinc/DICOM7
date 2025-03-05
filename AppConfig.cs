using System;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Serilog;

namespace OrderORM
{
    public static class AppConfig
    {
        private static string _commonAppFolder;

        /// <summary>
        /// Gets or sets the common application folder path used for storing configuration and cache data.
        /// On Windows, this is under CommonApplicationData; on other platforms, it's under $HOME/.local
        /// </summary>
        public static string CommonAppFolder
        {
            get
            {
                if (!string.IsNullOrEmpty(_commonAppFolder)) return _commonAppFolder;

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

                var tmp = Path.Combine(baseFolder, "Flux Inc", "OrderORM");
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
        public static string GetConfigFilePath(string fileName = "config.yaml")
        {
            return Path.Combine(CommonAppFolder, fileName);
        }
    }
}
