using PhoneBox.Agent;
using Xunit;

public class StorageMetricsTests
{
    // Columns: major minor name reads reads_merged sectors_read ms_reading writes writes_merged sectors_written
    //         ms_writing ios_in_progress ms_doing_io …
    private const string DiskStats = """
        259       0 nvme0n1 12345 0 200000 6789 100000 0 90000 5000 0 0 0 0 0 0 0
        259       1 nvme0n1p1 100 0 1000 50 500 0 0 100 0 0 0 0 0 0 0
          8       0 sda 10 0 2048 20 4096 0 250 250 0 0 0 0 0 0
          7       0 loop0 999 0 999999 0 0 0 0 0 0 0 0 0 0 0 0
          7       1 loop1 999 0 999999 0 0 0 0 0 0 0 0 0 0 0 0
        253       0 dm-0 5 0 1024 5 1024 0 1024 60 0 0 0 0 0 0
        """;

    [Fact]
    public void PartitionsAndVirtualDevicesAreNotCountedAsDisks()
    {
        var counters = SystemMonitor.ParseDiskStats(DiskStats);
        Assert.Equal(["nvme0n1", "sda"], counters.Keys.OrderBy(k => k));
        Assert.Equal(12345, counters["nvme0n1"].Reads);
        Assert.Equal(200000, counters["nvme0n1"].ReadSectors);
        Assert.Equal(6789, counters["nvme0n1"].ReadMs);
        Assert.Equal(100000, counters["nvme0n1"].Writes);
        Assert.Equal(90000, counters["nvme0n1"].WriteSectors);
    }

    [Fact]
    public void RatesAreBytesPerSecondAndBusyIsTheShareOfTheWindow()
    {
        var before = new Dictionary<string, SystemMonitor.DiskCounter>
        {
            ["nvme0n1"] = new(1000, 400, 10240, 1024, 0, 0),
        };
        var after = new Dictionary<string, SystemMonitor.DiskCounter>
        {
            ["nvme0n1"] = new(1040, 450, 10496, 1049, 20, 0),
        };

        var rate = Assert.Single(SystemMonitor.DiskRates(before, after, seconds: 2));

        Assert.Equal("nvme0n1", rate.Name);
        Assert.Equal(65536, rate.ReadBps);    // 256 sectors × 512 B over 2 s
        Assert.Equal(6400, rate.WriteBps);    // 25 sectors × 512 B over 2 s
        Assert.Equal(1, rate.BusyPct);        // 20 ms of I/O in a 2000 ms window
        Assert.Equal(45, rate.Iops);          // 90 completed I/Os over 2 s
    }

    [Fact]
    public void AResetCounterDoesNotProduceNegativeTraffic()
    {
        var before = new Dictionary<string, SystemMonitor.DiskCounter> { ["sda"] = new(1000, 500, 10240, 1024, 0, 0) };
        var after = new Dictionary<string, SystemMonitor.DiskCounter> { ["sda"] = new(10, 20, 2048, 1024, 0, 0) };

        var rate = Assert.Single(SystemMonitor.DiskRates(before, after, seconds: 1));

        Assert.Equal(0, rate.ReadBps);
        Assert.Equal(0, rate.WriteBps);
        Assert.Equal(0, rate.BusyPct);
        Assert.Equal(0, rate.Iops);
    }

    [Fact]
    public void ANewDeviceHasNoRateUntilThereIsAPreviousReading()
    {
        var after = new Dictionary<string, SystemMonitor.DiskCounter> { ["nvme0n1"] = new(10, 10, 10, 10, 0, 0) };
        Assert.Empty(SystemMonitor.DiskRates(new Dictionary<string, SystemMonitor.DiskCounter>(), after, seconds: 1));
    }

    [Fact]
    public void CleanRemovesOnlyEntriesOlderThanTheCutoffAndLeavesProtectedOnes()
    {
        var root = Fixture();
        try
        {
            var old = Path.Combine(root, "old.bin");
            var recent = Path.Combine(root, "recent.bin");
            var hidden = Path.Combine(root, ".X11-unix");
            var live = Path.Combine(root, "opencode-pty-1000");
            var oldDir = Path.Combine(root, "puppeteer_profile");
            File.WriteAllBytes(old, new byte[4096]);
            File.WriteAllBytes(recent, new byte[4096]);
            Directory.CreateDirectory(hidden);
            Directory.CreateDirectory(live);
            Directory.CreateDirectory(oldDir);
            File.WriteAllBytes(Path.Combine(oldDir, "blob"), new byte[8192]);

            var twoHoursAgo = DateTime.UtcNow.AddHours(-2);
            File.SetLastWriteTimeUtc(old, twoHoursAgo);
            File.SetLastWriteTimeUtc(Path.Combine(oldDir, "blob"), twoHoursAgo);
            File.SetLastWriteTimeUtc(oldDir, twoHoursAgo);
            File.SetLastWriteTimeUtc(hidden, twoHoursAgo);
            File.SetLastWriteTimeUtc(live, twoHoursAgo);

            var result = SystemMonitor.CleanTmp(root, TimeSpan.FromMinutes(60), dry: false);

            Assert.False(File.Exists(old));
            Assert.False(Directory.Exists(oldDir));
            Assert.True(File.Exists(recent));
            Assert.True(Directory.Exists(hidden));
            Assert.True(Directory.Exists(live));
            Assert.Equal(2, result.Deleted);
            Assert.True(result.FreedMb > 0, $"A pass that deleted two entries must report freed space: {result.FreedMb}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ADryRunReportsWhatWouldGoAndDeletesNothing()
    {
        var root = Fixture();
        try
        {
            var old = Path.Combine(root, "leftover.bin");
            File.WriteAllBytes(old, new byte[2048]);
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));

            var result = SystemMonitor.CleanTmp(root, TimeSpan.FromMinutes(60), dry: true);

            Assert.True(File.Exists(old));
            Assert.Equal(1, result.Deleted);
            Assert.Null(result.Tmp);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ScanListsTheLargestEntriesFirst()
    {
        var root = Fixture();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "small"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(root, "large"), new byte[64 * 1024]);
            File.WriteAllBytes(Path.Combine(root, ".hidden"), new byte[32 * 1024]);

            var info = SystemMonitor.ScanTmp(root, maxTop: 2);

            Assert.Equal(root, info.Path);
            Assert.Equal(3, info.Entries);
            Assert.Equal("large", info.Top[0].Name);
            Assert.Equal(".hidden", info.Top[1].Name);
            // Dot entries are protected, so a clean pass skips them, but they still show up as "keep" in the list.
            Assert.True(info.Top[1].Protected);
            Assert.Equal(2, info.Top.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Fixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
