namespace FantasyMontior.Core;

public sealed record DriveDescriptor(string RootPath, DriveType Type, bool IsReady);
public sealed record DriveCapacity(long TotalBytes, long FreeBytes);

// Keep operating-system access injectable, and on the existing collector worker.
public interface IDriveSpaceReader
{
    IReadOnlyList<DriveDescriptor> GetDrives();
    DriveCapacity Read(string rootPath);
}

public sealed class SystemDriveSpaceReader : IDriveSpaceReader
{
    public IReadOnlyList<DriveDescriptor> GetDrives() => DriveInfo.GetDrives().Select(d =>
    {
        // Do not query readiness/capacity on network, removable or optical drives.
        var type = d.DriveType;
        return new DriveDescriptor(d.Name, type, type == DriveType.Fixed && d.IsReady);
    }).ToArray();

    public DriveCapacity Read(string rootPath)
    {
        var drive = new DriveInfo(rootPath);
        if (!drive.IsReady) throw new IOException("The selected drive is not ready.");
        return new(drive.TotalSize, drive.TotalFreeSpace);
    }
}

public sealed class DriveSpaceHardware : IMonitorHardware
{
    public const string HardwareKind = "LogicalDrive";
    public const string UsageName = "Used Space";
    public const string UsageSuffix = "/load/used";
    public const string UsedSuffix = "/data/used";
    public const string TotalSuffix = "/data/total";
    public const double BytesPerGiB = 1024d * 1024 * 1024;
    private readonly IDriveSpaceReader _reader;
    private readonly string? _rootPath;
    private readonly string? _discoveryError;
    public string Id { get; }
    public string Name { get; }
    public string Kind => HardwareKind;
    public IReadOnlyList<IMonitorHardware> Children => [];
    public IReadOnlyList<RawSensor> Sensors { get; private set; } = [];

    private DriveSpaceHardware(IDriveSpaceReader reader, string? rootPath, string? error = null)
    {
        _reader = reader;
        _rootPath = rootPath?.ToUpperInvariant();
        _discoveryError = error;
        Name = _rootPath?.TrimEnd('\\', '/') ?? "Disk";
        Id = _rootPath is null ? "/logical-drive/unavailable" : "/logical-drive/" + Name.TrimEnd(':');
    }

    // Discover once per backend lifetime. A read failure must not select a different volume.
    public static DriveSpaceHardware Discover(IDriveSpaceReader reader)
    {
        try
        {
            var drives = reader.GetDrives().Where(d => d.Type == DriveType.Fixed)
                .OrderBy(d => d.RootPath, StringComparer.OrdinalIgnoreCase).ToArray();
            var selected = drives.FirstOrDefault(d => string.Equals(d.RootPath, @"C:\", StringComparison.OrdinalIgnoreCase))
                ?? drives.FirstOrDefault(d => d.IsReady);
            return new(reader, selected?.RootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Surface discovery errors on this node, leaving CPU/GPU collection running.
            return new(reader, null, $"Drive discovery failed: {ex.Message}");
        }
    }

    public void Update()
    {
        if (_rootPath is null) throw new IOException(_discoveryError ?? "No local fixed drive is available. Retry discovery after connecting a drive.");
        var capacity = _reader.Read(_rootPath);
        if (capacity.TotalBytes <= 0 || capacity.FreeBytes < 0 || capacity.FreeBytes > capacity.TotalBytes)
            throw new IOException("The selected drive reported invalid capacity.");
        var usedBytes = capacity.TotalBytes - capacity.FreeBytes;
        Sensors =
        [
            new(Id + UsageSuffix, UsageName, "Load", "%", usedBytes * 100d / capacity.TotalBytes),
            new(Id + UsedSuffix, UsageName, "Data", "GiB", usedBytes / BytesPerGiB),
            new(Id + TotalSuffix, "Total Space", "Data", "GiB", capacity.TotalBytes / BytesPerGiB)
        ];
    }
}
