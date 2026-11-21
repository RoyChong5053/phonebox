using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>Throughput of one physical disk. Rates are bytes per second, BusyPct is the share of the last interval
/// the device was busy (0–100).</summary>
public sealed record DiskIoInfo(string Name, double ReadBps, double WriteBps, double BusyPct, double Iops);

/// <summary>One top-level entry of the temp folder, for the "who is eating /tmp" list.</summary>
public sealed record TmpEntry(string Name, double SizeMb, long AgeSec, bool Protected);

/// <summary>The temp folder as the phone sees it: total/used from statvfs, the largest entries from a bounded scan.</summary>
public sealed record TmpInfo(string Path, string Fs, double UsedMb, double TotalMb, int Entries, long OldestSec,
    IReadOnlyList<TmpEntry> Top);

/// <summary>What a clean pass did; Tmp is the fresh snapshot after a real pass (null for a dry run).</summary>
public sealed record TmpCleanResult(int Deleted, double FreedMb, int Skipped, TmpInfo? Tmp);

// Disk I/O and the temp folder: the parts of the Computer widgets that are not in the main sampling loop.
public sealed partial class SystemMonitor
{
    private const double Mb = 1024d * 1024;

    private IReadOnlyList<DiskIoInfo> _diskIo = [];
    private volatile TmpInfo? _tmp;
    private Dictionary<string, DiskCounter> _diskPrev = [];
    private long _diskPrevAt;

    /// <summary>The counter fields of one device in /proc/diskstats that we difference between samples. Columns are
    /// 1-based in the kernel's stat group order and 0-based here after the major/minor/name prefix: 4 reads completed,
    /// 6 sectors read, 7 ms reading, 8 writes completed, 10 sectors written, 11 ms writing.</summary>
    internal readonly record struct DiskCounter(long Reads, long Writes, long ReadSectors, long WriteSectors, long ReadMs, long WriteMs);

    /// <summary>Whole disks only: partitions would count their parent's traffic twice, and loop/ram/dm/md are not the
    /// device the user thinks of when they ask how busy the SSD is.</summary>
    [GeneratedRegex(@"^(?:sd[a-z]+|hd[a-z]+|vd[a-z]+|xvd[a-z]+|nvme\d+n\d+|mmcblk\d+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PhysicalDisk();

    /// <summary>Parses /proc/diskstats. Sectors are 512 bytes on every kernel that writes this file; the ms columns
    /// are milliseconds spent on the device. Reads completed and sectors read are separate counters: one merged read
    /// moves many sectors.</summary>
    internal static Dictionary<string, DiskCounter> ParseDiskStats(string text)
    {
        var map = new Dictionary<string, DiskCounter>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // 0 major, 1 minor, 2 name, 3 reads completed, 4 reads merged, 5 sectors read, 6 ms reading,
            // 7 writes completed, 8 writes merged, 9 sectors written, 10 ms writing, 11 ios in progress, 12 ms doing io
            if (f.Length < 14 || !PhysicalDisk().IsMatch(f[2])) continue;
            if (!long.TryParse(f[3], out var reads) || !long.TryParse(f[5], out var readSectors)
                || !long.TryParse(f[6], out var readMs) || !long.TryParse(f[7], out var writes)
                || !long.TryParse(f[9], out var writeSectors) || !long.TryParse(f[10], out var writeMs)) continue;
            map[f[2]] = new DiskCounter(reads, writes, readSectors, writeSectors, readMs, writeMs);
        }
        return map;
    }

    /// <summary>Rates between two readings. A counter that went backwards (a device reset) counts as no traffic
    /// instead of a negative burst, the same way the network rates treat a reset adapter.</summary>
    internal static IReadOnlyList<DiskIoInfo> DiskRates(IReadOnlyDictionary<string, DiskCounter> before,
        IReadOnlyDictionary<string, DiskCounter> after, double seconds)
    {
        if (seconds <= 0) return [];
        var list = new List<DiskIoInfo>(after.Count);
        foreach (var (name, now) in after.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!before.TryGetValue(name, out var prev)) continue;
            var reads = Math.Max(0, now.Reads - prev.Reads);
            var writes = Math.Max(0, now.Writes - prev.Writes);
            var readSectors = Math.Max(0, now.ReadSectors - prev.ReadSectors);
            var writeSectors = Math.Max(0, now.WriteSectors - prev.WriteSectors);
            var readMs = Math.Max(0, now.ReadMs - prev.ReadMs);
            var writeMs = Math.Max(0, now.WriteMs - prev.WriteMs);
            list.Add(new DiskIoInfo(name,
                Math.Round(Math.Max(0, readSectors) * 512d / seconds),
                Math.Round(Math.Max(0, writeSectors) * 512d / seconds),
                Math.Round(Math.Min(100, Math.Max(0, readMs + writeMs) / (seconds * 1000) * 100), 1),
                Math.Round((reads + writes) / seconds, 1)));
        }
        return list;
    }

    private IReadOnlyList<DiskIoInfo> SampleDiskIo()
    {
        // Only Linux exposes per-device counters cheaply. Elsewhere the widget reports that it is unavailable.
        if (!OperatingSystem.IsLinux()) return _diskIo;
        try
        {
            var now = Stopwatch.GetTimestamp();
            var next = ParseDiskStats(File.ReadAllText("/proc/diskstats"));
            var seconds = _diskPrevAt == 0 ? 0 : (now - _diskPrevAt) / (double)Stopwatch.Frequency;
            _diskPrevAt = now;
            if (seconds <= 0 || _diskPrev.Count == 0)
            {
                // first sample: a zeroed start makes the rates come out as the real ones instead of an empty list
                _diskPrev = next.Keys.Count > 0 ? next.ToDictionary(k => k.Key, k => new DiskCounter(0, 0, 0, 0, 0, 0)) : [];
                return _diskIo;
            }
            var rates = DiskRates(_diskPrev, next, seconds);
            _diskPrev = next;
            return rates;
        }
        catch
        {
            return _diskIo;
        }
    }

    private async Task TmpLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { _tmp = ScanTmp(TmpPath()); }
            catch (Exception ex) { log.LogDebug("tmp: {Msg}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }

    /// <summary>The folder the Temp widget watches: TMPDIR when it is set, otherwise /tmp on Unix and %TEMP% on
    /// Windows. The camera and the tools the agent spawns use the same TMPDIR, so a moved temp folder stays together.</summary>
    internal static string TmpPath()
    {
        var env = Environment.GetEnvironmentVariable("TMPDIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        return OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp";
    }

    /// <summary>Names a clean pass must never touch: X/Wayland sockets, systemd's private mounts and the PTY daemon's
    /// live directory. Dot entries are skipped too — in the temp folder they are sockets, not user files.</summary>
    private static readonly string[] ProtectedPrefixes =
        ["systemd-private-", "snap-private-", "opencode-pty", "ssh-"];

    internal static bool IsProtected(string name) =>
        name.StartsWith('.') || ProtectedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sizes an entry without following links. A scan never spends more than <paramref name="maxMs"/> on it,
    /// so one huge directory (a downloaded model, a browser profile) cannot stall the sampling loop.</summary>
    private static long SizeOf(FileSystemInfo entry, Stopwatch budget, long maxMs)
    {
        try
        {
            if (entry is FileInfo file) return file.Length;
            if (entry is not DirectoryInfo dir) return 0;
            long sum = 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MaxRecursionDepth = 8,
            };
            foreach (var f in dir.EnumerateFiles("*", options))
            {
                if (budget.ElapsedMilliseconds > maxMs) break;
                try { sum += f.Length; } catch { }
            }
            return sum;
        }
        catch { return 0; }
    }

    internal static TmpInfo ScanTmp(string path, int maxTop = 5)
    {
        var fs = "";
        double totalMb = 0, freeMb = 0;
        try
        {
            // The temp folder may sit on its own filesystem (/tmp on tmpfs), so take it from the mount point
            // and not from whichever drive GetDrives happens to list first.
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => string.Equals(d.RootDirectory?.FullName, path, StringComparison.OrdinalIgnoreCase))
                ?? new DriveInfo(path);
            fs = drive.DriveFormat;
            totalMb = drive.TotalSize / Mb;
            freeMb = drive.TotalFreeSpace / Mb;
        }
        catch { }

        var top = new List<TmpEntry>();
        var entries = 0;
        long oldest = long.MaxValue;
        var budget = Stopwatch.StartNew();
        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                entries++;
                var age = (long)Math.Max(0, (DateTime.UtcNow - entry.LastWriteTimeUtc).TotalSeconds);
                if (age < oldest) oldest = age;
                // stop sizing once the budget is gone, but keep counting entries: that part is cheap
                var size = budget.ElapsedMilliseconds > 200 ? 0 : SizeOf(entry, budget, 200);
                top.Add(new TmpEntry(entry.Name, Math.Round(size / Mb, 1), age, IsProtected(entry.Name)));
            }
        }
        catch { }

        top.Sort((a, b) => b.SizeMb.CompareTo(a.SizeMb));
        return new TmpInfo(path, fs, Math.Round(totalMb - freeMb, 1), Math.Round(totalMb, 1), entries,
            oldest == long.MaxValue ? 0 : oldest, top.Take(maxTop).ToList());
    }

    /// <summary>
    /// Removes entries older than <paramref name="olderThan"/>. Directories are removed recursively; symlinks,
    /// sockets, dot entries and the protected prefixes are left alone. A dry run reports what would go without
    /// touching anything, which is what the phone shows before the real pass.
    /// </summary>
    internal static TmpCleanResult CleanTmp(string path, TimeSpan olderThan, bool dry, int max = 5000)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        var deleted = 0;
        var skipped = 0;
        long freed = 0;
        var budget = Stopwatch.StartNew();
        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos().Take(max))
            {
                if (budget.ElapsedMilliseconds > 4000) { skipped++; break; }
                if (IsProtected(entry.Name) || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skipped++;
                    continue;
                }
                DateTime modified;
                try { modified = entry.LastWriteTimeUtc; }
                catch { skipped++; continue; }
                if (modified > cutoff) { skipped++; continue; }

                var size = SizeOf(entry, budget, 4000);
                if (!dry)
                {
                    try
                    {
                        if (entry is DirectoryInfo dir) dir.Delete(true);
                        else entry.Delete();
                    }
                    catch { skipped++; continue; }
                }
                freed += size;
                deleted++;
            }
        }
        catch { }

        return new TmpCleanResult(deleted, Math.Round(freed / Mb, 2), skipped, dry ? null : ScanTmp(path));
    }
}
