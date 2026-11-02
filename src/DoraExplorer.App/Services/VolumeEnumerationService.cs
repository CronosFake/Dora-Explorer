using System.Management;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

public sealed class VolumeEnumerationService
{
    public sealed record UnmappedVolume(
        string DevicePath,
        string? Label,
        long CapacityBytes,
        long FreeBytes,
        string? FileSystem);

    public IReadOnlyList<UnmappedVolume> GetUnmappedVolumes()
    {
        var results = new List<UnmappedVolume>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Label, Capacity, FreeSpace, FileSystem, DriveType FROM Win32_Volume WHERE DriveLetter IS NULL");

            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                using (obj)
                {
                    var deviceId = obj["DeviceID"]?.ToString();
                    if (string.IsNullOrWhiteSpace(deviceId))
                    {
                        continue;
                    }

                    var driveType = Convert.ToUInt32(obj["DriveType"]);
                    if (driveType is 5 or 0)
                    {
                        continue;
                    }

                    if (obj["Capacity"] is not ulong capacity || capacity == 0)
                    {
                        continue;
                    }

                    var free = obj["FreeSpace"] is ulong freeSpace ? (long)freeSpace : 0L;
                    var label = obj["Label"]?.ToString();
                    var fileSystem = obj["FileSystem"]?.ToString();

                    if (!Directory.Exists(deviceId))
                    {
                        continue;
                    }

                    results.Add(new UnmappedVolume(
                        deviceId,
                        string.IsNullOrWhiteSpace(label) ? null : label,
                        (long)capacity,
                        free,
                        string.IsNullOrWhiteSpace(fileSystem) ? null : fileSystem));
                }
            }
        }
        catch (Exception ex)
        {
            AppLogService.Error("Falha ao enumerar partições não mapeadas", ex);
        }

        return results
            .OrderBy(v => v.Label ?? v.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
