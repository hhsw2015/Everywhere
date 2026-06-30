using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;

namespace Everywhere.Mcp.OpenCli;

/// <summary>
/// SPEC §8 Phase 1 — module loader for the V8 isolate.
///
/// Adapters do <c>import { cli, Strategy } from '@jackwener/opencli/registry'</c>
/// and (rarely) <c>'@jackwener/opencli/errors'</c>. Both must resolve to the
/// in-host <see cref="HostShim"/> objects so adapters never reach upstream
/// runtime code. Relative imports (<c>./utils.js</c> etc.) resolve against
/// the bundled adapter tree at <see cref="RootDir"/>, with a hard guard that
/// the resolved path stays under <see cref="RootDir"/> (OCR review #2 —
/// otherwise <c>../../../../etc/passwd.js</c> would escape the sandbox).
/// </summary>
public sealed class OpenCliDocumentLoader : DefaultDocumentLoader
{
    private static readonly string[] CandidateExtensions = [".js", ".mjs", ".cjs"];

    private readonly string _rootDir;
    private readonly Dictionary<string, string> _shims;

    public string RootDir => _rootDir;

    public OpenCliDocumentLoader(string rootDir, IReadOnlyDictionary<string, string> shims)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDir);
        ArgumentNullException.ThrowIfNull(shims);
        _rootDir = Path.GetFullPath(rootDir);
        _shims = new Dictionary<string, string>(shims, StringComparer.Ordinal);
    }

    public override async Task<Document> LoadDocumentAsync(
        DocumentSettings settings,
        DocumentInfo? sourceInfo,
        string specifier,
        DocumentCategory? category,
        DocumentContextCallback? contextCallback)
    {
        if (_shims.TryGetValue(specifier, out var src))
        {
            var info = new DocumentInfo(new Uri($"opencli-shim:{specifier}"))
            {
                Category = ModuleCategory.Standard,
                ContextCallback = contextCallback,
            };
            return new StringDocument(info, src);
        }

        if (specifier.StartsWith("./", StringComparison.Ordinal) ||
            specifier.StartsWith("../", StringComparison.Ordinal))
        {
            var baseDir = sourceInfo?.Uri?.IsFile == true
                ? Path.GetDirectoryName(sourceInfo.Value.Uri.LocalPath) ?? _rootDir
                : _rootDir;
            var resolved = Path.GetFullPath(Path.Combine(baseDir, specifier));

            // Containment guard — the resolved path must live under the
            // adapter root. Without this an adapter could
            // `import '../../../../etc/passwd.js'` and execute it inside
            // the isolate. Comparison is case-insensitive on Windows /
            // macOS where the filesystem is also case-insensitive.
            var pathCmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var rootWithSep = _rootDir.EndsWith(Path.DirectorySeparatorChar) ? _rootDir : _rootDir + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(rootWithSep, pathCmp))
                throw new UnauthorizedAccessException(
                    $"opencli loader: import '{specifier}' resolves outside the adapter root ({_rootDir})");

            // Try the literal path, then the standard JS extensions —
            // unconditionally, since adapter dirs / files can legitimately
            // contain dots (e.g. `./submodule.v2`).
            string? candidate = null;
            if (File.Exists(resolved)) candidate = resolved;
            if (candidate is null)
            {
                foreach (var ext in CandidateExtensions)
                {
                    var p = resolved + ext;
                    if (File.Exists(p)) { candidate = p; break; }
                }
            }

            if (candidate is null)
                throw new FileNotFoundException(
                    $"opencli loader: relative import '{specifier}' did not resolve to any file under {_rootDir} (tried {resolved} + {string.Join("/", CandidateExtensions)})",
                    resolved);

            // Block symlinks pointing outside the adapter root.
            try
            {
                var fi = new FileInfo(candidate);
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException($"opencli loader: refusing to follow symlink '{candidate}'");
            }
            catch (UnauthorizedAccessException) { throw; }
            catch { /* attribute read failed — proceed and let read fail loudly */ }

            var info = new DocumentInfo(new Uri(candidate))
            {
                Category = ModuleCategory.Standard,
                ContextCallback = contextCallback,
            };
            var text = await File.ReadAllTextAsync(candidate).ConfigureAwait(false);
            return new StringDocument(info, text);
        }

        return await base.LoadDocumentAsync(settings, sourceInfo, specifier, category, contextCallback).ConfigureAwait(false);
    }
}
