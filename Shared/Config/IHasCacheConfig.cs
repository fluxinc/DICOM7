using System;

namespace DICOM7.Shared.Config
{
    /// <summary>
    /// Interface for configuration classes that include cache settings
    /// </summary>
    public interface IHasCacheConfig
    {
        /// <summary>
        /// Cache-related configuration settings
        /// </summary>
        BaseCacheConfig Cache { get; set; }
    }
}
