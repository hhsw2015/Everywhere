using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mcp.Connector;

/// <summary>
/// SPEC docs/specs/everywhere-connector.md §6 — implements upstream's
/// TransitFileStore contract so provider actions that upload / download
/// binary payloads can execute inside the V8 isolate.
///
/// Layout: <c>~/.everywhere/connector/transit/&lt;fileId&gt;.blob</c>
/// with a sibling <c>&lt;fileId&gt;.meta.json</c> holding name + mimeType
/// + createdAt. TTL: 1 hour after creation, swept on every create.
///
/// downloadUrl points at the loopback daemon's
/// <c>/v1/files/&lt;fileId&gt;</c> endpoint, so provider APIs that expect
/// an HTTP URL (send-file-by-URL flows) can hand it back and hit the
/// same daemon.
/// </summary>
public sealed class TransitFileStore
{
    private readonly string _dir;
    private readonly ILogger<TransitFileStore>? _log;
    private readonly Func<string> _baseUrlFactory;

    // Upstream default is 25 MiB. Match it — enough for the vast majority
    // of doc/audio/image uploads, low enough to keep OOMs at bay.
    public int MaxBytes { get; } = 25 * 1024 * 1024;

    public TransitFileStore(Func<string> baseUrlFactory, ILogger<TransitFileStore>? log = null, string? overrideDir = null)
    {
        _baseUrlFactory = baseUrlFactory;
        _log = log;
        _dir = overrideDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".everywhere", "connector", "transit");
        System.IO.Directory.CreateDirectory(_dir);
    }

    public string DirectoryPath => _dir;

    /// <summary>Create a transit file from raw bytes. Returns metadata
    /// matching upstream's TransitFileStore.create() promise.</summary>
    public JsonObject Create(byte[] bytes, string name, string mimeType)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length > MaxBytes)
            throw new InvalidOperationException($"transit file exceeds {MaxBytes / (1024 * 1024)} MiB cap");

        SweepStale();
        var fileId = Guid.NewGuid().ToString("N");
        var blobPath = Path.Combine(_dir, fileId + ".blob");
        var metaPath = Path.Combine(_dir, fileId + ".meta.json");
        File.WriteAllBytes(blobPath, bytes);
        var meta = new JsonObject
        {
            ["name"] = name ?? fileId,
            ["mimeType"] = mimeType ?? "application/octet-stream",
            ["sizeBytes"] = bytes.Length,
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        File.WriteAllText(metaPath, meta.ToJsonString());
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(blobPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(metaPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch { /* best-effort */ }

        return new JsonObject
        {
            ["fileId"] = fileId,
            ["downloadUrl"] = $"{_baseUrlFactory().TrimEnd('/')}/v1/files/{fileId}",
            ["sizeBytes"] = bytes.Length,
            ["name"] = name ?? fileId,
            ["mimeType"] = mimeType ?? "application/octet-stream",
        };
    }

    public bool TryRead(string fileId, out byte[] bytes, out string name, out string mimeType)
    {
        bytes = Array.Empty<byte>();
        name = "";
        mimeType = "application/octet-stream";
        if (string.IsNullOrEmpty(fileId) || !IsSafeId(fileId)) return false;
        var blobPath = Path.Combine(_dir, fileId + ".blob");
        var metaPath = Path.Combine(_dir, fileId + ".meta.json");
        if (!File.Exists(blobPath)) return false;
        bytes = File.ReadAllBytes(blobPath);
        if (File.Exists(metaPath))
        {
            try
            {
                var meta = JsonNode.Parse(File.ReadAllText(metaPath)) as JsonObject;
                name = meta?["name"]?.GetValue<string>() ?? fileId;
                mimeType = meta?["mimeType"]?.GetValue<string>() ?? "application/octet-stream";
            }
            catch { /* keep defaults */ }
        }
        return true;
    }

    public bool Delete(string fileId)
    {
        if (string.IsNullOrEmpty(fileId) || !IsSafeId(fileId)) return false;
        var blobPath = Path.Combine(_dir, fileId + ".blob");
        var metaPath = Path.Combine(_dir, fileId + ".meta.json");
        var removed = false;
        if (File.Exists(blobPath)) { File.Delete(blobPath); removed = true; }
        if (File.Exists(metaPath)) { File.Delete(metaPath); }
        return removed;
    }

    private void SweepStale()
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
            foreach (var meta in System.IO.Directory.EnumerateFiles(_dir, "*.meta.json"))
            {
                try
                {
                    var stat = new FileInfo(meta);
                    if (stat.LastWriteTimeUtc >= cutoff) continue;
                    var id = Path.GetFileNameWithoutExtension(meta);
                    // meta filename is "<id>.meta" per Path convention;
                    // strip the trailing ".meta" too.
                    if (id.EndsWith(".meta", StringComparison.Ordinal))
                        id = id.Substring(0, id.Length - 5);
                    Delete(id);
                }
                catch { /* ignore per-file failure */ }
            }
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "connector transit sweep failed");
        }
    }

    private static bool IsSafeId(string s)
    {
        // GUID N format only — no path chars.
        if (s.Length != 32) return false;
        foreach (var c in s)
            if (!(char.IsDigit(c) || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }
}
