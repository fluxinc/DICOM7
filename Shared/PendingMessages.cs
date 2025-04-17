namespace DICOM7.Shared
{
  /// <summary>
  /// Represents a pending ORM message that needs to be retried
  /// </summary>
  public class PendingOrmMessage
  {
    /// <summary>
    /// The Study Instance UID for the message
    /// </summary>
    public string StudyInstanceUid { get; set; }

    /// <summary>
    /// The HL7 ORM message content
    /// </summary>
    public string OrmMessage { get; set; }

    /// <summary>
    /// The current attempt count
    /// </summary>
    public int AttemptCount { get; set; }
  }

  /// <summary>
  /// Represents a pending ORU message that needs to be retried
  /// </summary>
  public class PendingOruMessage
  {
    /// <summary>
    /// The SOP Instance UID for the message
    /// </summary>
    public string SopInstanceUid { get; set; }

    /// <summary>
    /// The HL7 ORU message content
    /// </summary>
    public string OruMessage { get; set; }

    /// <summary>
    /// The current attempt count
    /// </summary>
    public int AttemptCount { get; set; }
  }
}
