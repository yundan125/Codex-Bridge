namespace CloudLight.CodexBridge.Models;

public sealed class BackupManifest
{
    public int FormatVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public string AppVersion { get; set; } = "";
    public string CodexVersion { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string CodexHome { get; set; } = "";
    public bool IncludedCodex { get; set; }
    public bool IncludedBridge { get; set; }
    public int FileCount { get; set; }
    public long TotalSize { get; set; }
    public List<BackupFileRecord> Files { get; set; } = [];
    public List<BackupFailure> Failures { get; set; } = [];
}

public sealed class BackupFileRecord
{
    public string RelativePath { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTimeOffset LastWriteTime { get; set; }
}

public sealed class BackupFailure
{
    public string RelativePath { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed record BackupProgress(string Stage, int ProcessedFiles, int TotalFiles, long ProcessedBytes, long TotalBytes)
{
    public int Percent => TotalBytes > 0 ? (int)Math.Clamp(ProcessedBytes * 100L / TotalBytes, 0, 100) : 0;
}

public sealed class BackupResult
{
    public required string FilePath { get; init; }
    public required BackupManifest Manifest { get; init; }
    public bool IsComplete => Manifest.Failures.Count == 0;
}

public sealed class RestoreOptions
{
    public bool RestoreCodex { get; set; } = true;
    public bool RestoreBridge { get; set; } = true;
    public bool Replace { get; set; } = true;
    public bool VerifyNoExternalCodex { get; set; } = true;
    public required string PreRestoreDirectory { get; set; }
}

public sealed class RestoreResult
{
    public required BackupManifest Manifest { get; init; }
    public required string PreRestoreBackupPath { get; init; }
    public int RestoredFiles { get; init; }
}
