using System.Diagnostics;
using System.IO;
using System.Text;

namespace FileCopy.Client.Backup;

/// <summary>Packs this machine's equipment folders into one 7z archive using 7-Zip.</summary>
public static class BackupService
{
    public static readonly string[] SourceFolders = [@"D:\Config", @"D:\Recipes", @"D:\MechanicalParameters"];

    /// <summary>Server folder (and folder under the local mirror) that holds the archives.</summary>
    public const string RemoteBackupDir = "backup";

    public static string ArchiveName(string equipmentId) => equipmentId + ".7z";

    private static string Find7Zip()
    {
        string?[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "7za.exe"),
            Path.Combine(AppContext.BaseDirectory, "7z.exe"),
            // ProgramW6432 is the 64-bit Program Files even when this process runs as 32-bit.
            Environment.GetEnvironmentVariable("ProgramW6432") is { Length: > 0 } pf64 ? Path.Combine(pf64, "7-Zip", "7z.exe") : null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
        ];
        return candidates.FirstOrDefault(p => p != null && File.Exists(p))
               ?? throw new FileNotFoundException("找不到 7-Zip。请安装 7-Zip，或把 7za.exe 放到 FileCopy.exe 同目录。");
    }

    /// <summary>
    /// Creates the archive at <paramref name="targetPath"/>, replacing any previous one.
    /// The archive is built next to the target as ".fcpart" (ignored by the sync scan) and then
    /// renamed on the same volume, so a refresh never sees a half-written file and a failed run
    /// leaves the previous backup intact. Cancelling <paramref name="ct"/> kills 7-Zip and removes the temp file.
    /// Returns the files 7-Zip could not read (e.g. locked by a running program).
    /// </summary>
    public static async Task<List<string>> CreateArchiveAsync(string targetPath, Action<string> log, CancellationToken ct = default)
    {
        var sources = new List<string>();
        foreach (var dir in SourceFolders)
        {
            if (Directory.Exists(dir)) sources.Add(dir);
            else log($"备份: 跳过不存在的文件夹 {dir}");
        }
        if (sources.Count == 0) throw new DirectoryNotFoundException("要备份的文件夹都不存在: " + string.Join(", ", SourceFolders));

        var exe = Find7Zip();
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        // Not plain ".fcpart": that name is the download temp file for the same path.
        var tmp = targetPath + ".tmp.fcpart";
        File.Delete(tmp);

        var psi = new ProcessStartInfo(exe)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8, // matches -sccUTF-8 below, so logged paths are not garbled
        };
        // -ssw: also read files another program has open for writing (equipment software often does).
        // -sccUTF-8: console output charset (7-Zip otherwise uses the OEM code page).
        foreach (var arg in new[] { "a", "-t7z", "-mx=5", "-ssw", "-y", "-bso0", "-bsp0", "-sccUTF-8", tmp }.Concat(sources))
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7-Zip");
        var stderr = proc.StandardError.ReadToEndAsync(); // completes when 7-Zip exits or is killed
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); proc.WaitForExit(); } catch (Exception) { } // keep the cancellation as the reported error
            File.Delete(tmp);
            throw;
        }
        var errors = (await stderr).Trim();

        // 7-Zip exit codes: 0 = ok, 1 = warning (e.g. some files locked), 2+ = error.
        if (proc.ExitCode >= 2)
        {
            File.Delete(tmp);
            throw new IOException($"7-Zip 压缩失败 (代码 {proc.ExitCode}): {errors}");
        }
        var skipped = proc.ExitCode == 1 ? SkippedFiles(errors) : [];
        foreach (var line in skipped) log("备份: 未能压缩 " + line);
        if (proc.ExitCode == 1 && skipped.Count == 0 && errors.Length > 0) log("备份: 7-Zip 警告 " + errors);

        // Replacing a file that another handle still has open fails on Windows (even with
        // FileShare.Delete; only POSIX rename semantics allow it, and File.Move does not use them).
        // ServerApi.UploadAsync holds the old archive open only for the few ms it takes to read a
        // chunk, so a short retry is enough; once replaced, that upload detects the change and aborts.
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                try { File.Move(tmp, targetPath, overwrite: true); break; }
                catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(200, ct);
                }
            }
        }
        catch { File.Delete(tmp); throw; }
        return skipped;
    }

    /// <summary>Per-file lines ("path : reason") from the summary 7-Zip prints after "WARNINGS for files:".</summary>
    private static List<string> SkippedFiles(string stderr)
    {
        var start = stderr.IndexOf("WARNINGS for files:", StringComparison.Ordinal);
        if (start < 0) return []; // a warning that is not about specific files; logged by the caller
        return stderr[start..].Split('\n').Skip(1)
            .Select(l => l.Trim())
            .TakeWhile(l => !l.StartsWith("---", StringComparison.Ordinal))
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
