using System;
using System.IO;
using System.Linq;
using System.Threading;
using DICOM7.ORM2DICOM;
using DICOM7.Shared.Config;
using FellowOakDicom;
using Xunit;

namespace ORM2DICOM.Tests
{
  public sealed class Orm2DicomTestFixture : IDisposable
  {
    public Orm2DicomTestFixture()
    {
      BasePath = Path.Combine(Path.GetTempPath(), "ORM2DICOM.Tests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(BasePath);

      AppConfig.Initialize("ORM2DICOM");
      AppConfig.SetBasePath(BasePath);

      CacheRoot = Path.Combine(BasePath, "cache");
      Directory.CreateDirectory(CacheRoot);

      CacheManager.SetConfiguredCacheFolder(CacheRoot);
      CacheManager.EnsureCacheFolder(CacheRoot);
      ClearCache();
    }

    public string BasePath { get; }

    public string CacheRoot { get; }

    public void ClearCache()
    {
      string activeFolder = Path.Combine(CacheRoot, "active");
      if (!Directory.Exists(activeFolder))
      {
        Directory.CreateDirectory(activeFolder);
        return;
      }

      foreach (string file in Directory.GetFiles(activeFolder, "*.hl7"))
      {
        File.Delete(file);
      }
    }

    public void Dispose()
    {
      try
      {
        if (Directory.Exists(BasePath))
        {
          Directory.Delete(BasePath, true);
        }
      }
      catch
      {
        // Ignore cleanup failures in tests
      }
    }
  }

  [CollectionDefinition("Orm2DicomSerial")]
  public sealed class Orm2DicomSerialCollection : ICollectionFixture<Orm2DicomTestFixture>
  {
  }

  [Collection("Orm2DicomSerial")]
  public class CachedOrmTests
  {
    private readonly Orm2DicomTestFixture _fixture;

    public CachedOrmTests(Orm2DicomTestFixture fixture)
    {
      _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
      _fixture.ClearCache();
    }

    [Fact]
    public void AsDicomDataset_MapsExpectedFields()
    {
      string accessionNumber = $"ACC{Guid.NewGuid():N}";
      string patientId = $"PID{Guid.NewGuid():N}";
      string messageControlId = $"MSG{Guid.NewGuid():N}";
      string message = CreateOrmMessage(messageControlId, patientId, "Doe^Jane", accessionNumber, "20240211103000");

      CachedORM cached = new CachedORM(message);
      DicomDataset dataset = cached.AsDicomDataset();

      Assert.NotNull(dataset);
      Assert.Equal(patientId, dataset.GetSingleValue<string>(DicomTag.PatientID));
      Assert.Equal("Doe^Jane", dataset.GetSingleValue<string>(DicomTag.PatientName));
      Assert.Equal("19800101", dataset.GetSingleValue<string>(DicomTag.PatientBirthDate));
      Assert.Equal("F", dataset.GetSingleValue<string>(DicomTag.PatientSex));
      Assert.True(dataset.Contains(DicomTag.AccessionNumber));
      Assert.Equal(accessionNumber.Substring(0, 16), dataset.GetSingleValue<string>(DicomTag.AccessionNumber));

      Assert.True(dataset.Contains(DicomTag.ScheduledProcedureStepSequence));
      DicomSequence sequence = dataset.GetSequence(DicomTag.ScheduledProcedureStepSequence);
      Assert.NotNull(sequence);
      DicomDataset sps = sequence.First();
      Assert.Equal("Chest X-ray", sps.GetSingleValue<string>(DicomTag.ScheduledProcedureStepDescription));
      Assert.Equal("20240211", sps.GetSingleValue<string>(DicomTag.ScheduledProcedureStepStartDate));
    }

    [Fact]
    public void SaveThenLoad_PersistsMessageToDisk()
    {
      string accessionNumber = $"ACC{Guid.NewGuid():N}";
      string patientId = $"PID{Guid.NewGuid():N}";
      string messageControlId = $"MSG{Guid.NewGuid():N}";
      string message = CreateOrmMessage(messageControlId, patientId, "Smith^John", accessionNumber);

      CachedORM cached = new CachedORM(message);
      Assert.True(cached.Save());
      string filePath = cached.FileInfo.FullName;
      Assert.True(File.Exists(filePath));

      CachedORM rehydrated = new CachedORM(new FileInfo(filePath));
      Assert.Equal(message, rehydrated.Text);
      Assert.Equal(cached.UUID, rehydrated.UUID);
    }

    [Fact]
    public void Save_WhenMessageAlreadyCached_TouchesExistingFile()
    {
      string accessionNumber = $"ACC{Guid.NewGuid():N}";
      string patientId = $"PID{Guid.NewGuid():N}";
      string messageControlId = $"MSG{Guid.NewGuid():N}";
      string message = CreateOrmMessage(messageControlId, patientId, "Roe^Janet", accessionNumber);

      CachedORM cached = new CachedORM(message);
      Assert.True(cached.Save());
      string filePath = cached.FileInfo.FullName;
      DateTime initialWrite = File.GetLastWriteTimeUtc(filePath);

      Thread.Sleep(1100);

      CachedORM duplicate = new CachedORM(message);
      Assert.True(duplicate.Save());
      DateTime updatedWrite = File.GetLastWriteTimeUtc(filePath);

      Assert.True(updatedWrite > initialWrite);
    }

    [Fact]
    public void RemoveExpired_RemovesOnlyStaleMessages()
    {
      string staleMessage = CreateOrmMessage($"MSG{Guid.NewGuid():N}", $"PID{Guid.NewGuid():N}", "Old^Patient", $"ACC{Guid.NewGuid():N}");
      CachedORM stale = new CachedORM(staleMessage);
      Assert.True(stale.Save());
      File.SetLastWriteTimeUtc(stale.FileInfo.FullName, DateTime.UtcNow.AddHours(-5));

      string freshMessage = CreateOrmMessage($"MSG{Guid.NewGuid():N}", $"PID{Guid.NewGuid():N}", "Fresh^Patient", $"ACC{Guid.NewGuid():N}");
      CachedORM fresh = new CachedORM(freshMessage);
      Assert.True(fresh.Save());
      File.SetLastWriteTimeUtc(fresh.FileInfo.FullName, DateTime.UtcNow.AddMinutes(-30));

      int removed = CachedORM.RemoveExpired(2);

      Assert.Equal(1, removed);
      Assert.False(File.Exists(stale.FileInfo.FullName));
      Assert.True(File.Exists(fresh.FileInfo.FullName));
    }

    [Fact]
    public void GetActiveORMs_ReturnsCachedMessagesInWriteOrder()
    {
      string olderMessage = CreateOrmMessage($"MSG{Guid.NewGuid():N}", $"PID{Guid.NewGuid():N}", "Older^Patient", $"ACC{Guid.NewGuid():N}");
      CachedORM older = new CachedORM(olderMessage);
      Assert.True(older.Save());
      File.SetLastWriteTime(older.FileInfo.FullName, DateTime.Now.AddMinutes(-10));

      string newerMessage = CreateOrmMessage($"MSG{Guid.NewGuid():N}", $"PID{Guid.NewGuid():N}", "Newer^Patient", $"ACC{Guid.NewGuid():N}");
      CachedORM newer = new CachedORM(newerMessage);
      Assert.True(newer.Save());
      File.SetLastWriteTime(newer.FileInfo.FullName, DateTime.Now.AddMinutes(-5));

      string tmpPath = Path.Combine(Path.GetDirectoryName(older.FileInfo.FullName) ?? _fixture.CacheRoot, $"{Guid.NewGuid():N}.hl7.tmp");
      File.WriteAllText(tmpPath, "ignore");

      try
      {
        CachedORM[] active = CachedORM.GetActiveORMs().ToArray();

        Assert.Equal(new[] { older.UUID, newer.UUID }, active.Select(a => a.UUID).ToArray());
        Assert.All(active, item => Assert.False(item.FileInfo.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)));
      }
      finally
      {
        if (File.Exists(tmpPath))
        {
          File.Delete(tmpPath);
        }
      }
    }

    [Fact]
    public void RemoveExpired_WhenExpiryIsNotPositive_DoesNotDeleteMessages()
    {
      string message = CreateOrmMessage($"MSG{Guid.NewGuid():N}", $"PID{Guid.NewGuid():N}", "Keep^Patient", $"ACC{Guid.NewGuid():N}");
      CachedORM cached = new CachedORM(message);
      Assert.True(cached.Save());
      string path = cached.FileInfo.FullName;

      int removed = CachedORM.RemoveExpired(0);

      Assert.Equal(0, removed);
      Assert.True(File.Exists(path));
    }

    [Fact]
    public void Touch_WithCreationTimeFlag_UpdatesTimestamps()
    {
      string message = CreateOrmMessage($"MSG{Guid.NewGuid():N}", $"PID{Guid.NewGuid():N}", "Touch^Patient", $"ACC{Guid.NewGuid():N}");
      CachedORM cached = new CachedORM(message);
      Assert.True(cached.Save());

      DateTime past = new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc);
      File.SetCreationTimeUtc(cached.FileInfo.FullName, past);
      File.SetLastWriteTimeUtc(cached.FileInfo.FullName, past);

      cached.Touch(true);

      DateTime creationAfter = File.GetCreationTimeUtc(cached.FileInfo.FullName);
      DateTime writeAfter = File.GetLastWriteTimeUtc(cached.FileInfo.FullName);

      Assert.True(creationAfter >= past);
      Assert.True(writeAfter > past);
    }

    [Fact]
    public void AsDicomDataset_TruncatesValuesToVrLimits()
    {
      string messageControlId = $"MSG{Guid.NewGuid():N}";
      string patientId = $"PID{Guid.NewGuid():N}";
      string accessionNumber = new string('A', 40);
      string longFamily = new string('L', 80);
      string longGiven = new string('G', 80);
      string longAddress = new string('Z', 120);

      string message = string.Join("\r", new[]
      {
        $"MSH|^~\\&|HIS|MedCenter|LIS|MedCenter|20240211103000||ORM^O01|{messageControlId}|P|2.3",
        $"PID|||{patientId}||{longFamily}^{longGiven}||19800101|F|||{longAddress}",
        "PV1|1|O|RAD^ER^101^FluxHospital",
        $"ORC|NW|{accessionNumber}|ORD4488||||||20240211100000|||5678^Requesting^Phys",
        $"OBR|1|{accessionNumber}|{accessionNumber}||Chest X-ray|||20240211103000"
      });

      CachedORM cached = new CachedORM(message);
      DicomDataset dataset = cached.AsDicomDataset();

      Assert.NotNull(dataset);
      Assert.Equal(accessionNumber.Substring(0, 16), dataset.GetSingleValue<string>(DicomTag.AccessionNumber));
      string patientName = dataset.GetSingleValue<string>(DicomTag.PatientName);
      Assert.Equal($"{longFamily}^{longGiven}".Substring(0, 64), patientName);
      string address = dataset.GetSingleValue<string>(DicomTag.PatientAddress);
      Assert.Equal(longAddress.Substring(0, 64), address);
    }

    [Fact]
    public void AsDicomDataset_FormatsExtendedNameComponents()
    {
      string messageControlId = $"MSG{Guid.NewGuid():N}";
      string patientId = $"PID{Guid.NewGuid():N}";
      string accessionNumber = $"ACC{Guid.NewGuid():N}";

      string message = string.Join("\r", new[]
      {
        $"MSH|^~\\&|HIS|MedCenter|LIS|MedCenter|20240211103000||ORM^O01|{messageControlId}|P|2.3",
        $"PID|||{patientId}||Doe^Jane||19800101|F|||123 Main St^^Metropolis^IL^62960^USA",
        "PV1|1|O|RAD^ER^101^FluxHospital||||7777^SchedLast^SchedFirst^SchedMiddle^SchedSuffix^SchedPrefix|8888^RefLast^RefFirst^RefMiddle^RefSuffix^RefPrefix",
        $"ORC|NW|{accessionNumber}|ORD4488||||||20240211100000|||5678^ReqLast^ReqFirst^ReqMiddle^ReqSuffix^ReqPrefix",
        $"OBR|1|{accessionNumber}|{accessionNumber}||Chest X-ray|||20240211103000"
      });

      CachedORM cached = new CachedORM(message);
      DicomDataset dataset = cached.AsDicomDataset();

      Assert.NotNull(dataset);
      Assert.Equal("FluxHospital", dataset.GetSingleValue<string>(DicomTag.InstitutionName));
      Assert.Equal("RefLast^RefFirst^RefMiddle^RefPrefix^RefSuffix", dataset.GetSingleValue<string>(DicomTag.ReferringPhysicianName));
      Assert.Equal("ReqLast^ReqFirst^ReqMiddle^ReqPrefix^ReqSuffix", dataset.GetSingleValue<string>(DicomTag.RequestingPhysician));

      DicomSequence sequence = dataset.GetSequence(DicomTag.ScheduledProcedureStepSequence);
      Assert.NotNull(sequence);
      DicomDataset sps = sequence.First();
      Assert.Equal("SchedLast^SchedFirst^SchedMiddle^SchedPrefix^SchedSuffix", sps.GetSingleValue<string>(DicomTag.ScheduledPerformingPhysicianName));
    }

    [Fact]
    public void AsDicomDataset_WhenMissingPidSegment_ReturnsNull()
    {
      string messageControlId = $"MSG{Guid.NewGuid():N}";
      string accessionNumber = $"ACC{Guid.NewGuid():N}";
      string message = string.Join("\r", new[]
      {
        $"MSH|^~\\&|HIS|MedCenter|LIS|MedCenter|20240211103000||ORM^O01|{messageControlId}|P|2.3",
        $"ORC|NW|{accessionNumber}|ORD4488",
        $"OBR|1|{accessionNumber}|{accessionNumber}||Chest X-ray"
      });

      CachedORM cached = new CachedORM(message);
      DicomDataset dataset = cached.AsDicomDataset();

      Assert.Null(dataset);
    }

    private static string CreateOrmMessage(string messageControlId, string patientId, string patientName, string accessionNumber, string observationDateTime = "20240211103000")
    {
      string[] segments = new[]
      {
        $"MSH|^~\\&|HIS|MedCenter|LIS|MedCenter|20240211103000||ORM^O01|{messageControlId}|P|2.3",
        $"PID|||{patientId}||{patientName}||19800101|F|||123 Main St^^Metropolis^IL^62960^USA",
        "PV1|1|O|RAD^ER^101^FluxHospital",
        $"ORC|NW|{accessionNumber}|ORD4488||||||20240211100000|||5678^Requesting^Phys",
        $"OBR|1|{accessionNumber}|{accessionNumber}|Chest X-ray|||{observationDateTime}"
      };

      return string.Join("\r", segments);
    }
  }
}
