using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Multiplayer.Authoritative;

// The full directory tree is hashed, including About, assemblies, XML, and
// assets. No filename or timestamp from the host is used as a substitute for
// file bytes. No source/DLL from any third-party multiplayer mod is required.
public sealed record ContentTreeLimits(
    int MaxFiles = 100_000,
    int MaxDepth = 32,
    long MaxSingleFileBytes = 512L * 1024 * 1024,
    long MaxTotalBytes = 4L * 1024 * 1024 * 1024,
    int MaxRelativePathBytes = 1024);

public sealed record LoadedModDirectory(
    string PackageId, string Version, string AbsoluteRootPath);

public sealed record ResolvedDefIdentity(
    string TypeFullName, string DefName, ushort ShortHash);

public static class ContentFingerprintBuilder
{
    private const int MaxResolvedDefs = 250_000;
    private static readonly byte[] DirectoryHeader = Encoding.ASCII.GetBytes(
        "RW-AUTH-MOD-FILETREE-v1\0");
    private static readonly byte[] DefsHeader = Encoding.ASCII.GetBytes(
        "RW-AUTH-RESOLVED-DEF-IDENTITIES-v1\0");

    private sealed record FileEntry(
        string RelativePath, string FullPath, long Length, DateTime LastWriteUtc);

    // Does not follow directory or file symlinks / reparse points. Rejects
    // potentially nonportable case-insensitive or Unicode-normalized collisions.
    // The entire tree is read once; changing files cause an exception.
    public static string HashModDirectory(
        string absoluteRootPath, ContentTreeLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(absoluteRootPath))
            throw new ArgumentException("Mod root is required", nameof(absoluteRootPath));
        limits ??= new ContentTreeLimits();
        if (limits.MaxFiles <= 0 || limits.MaxFiles > 500_000 ||
            limits.MaxDepth <= 0 || limits.MaxDepth > 128 ||
            limits.MaxSingleFileBytes <= 0 || limits.MaxTotalBytes <= 0 ||
            limits.MaxRelativePathBytes <= 0 || limits.MaxRelativePathBytes > 4096)
            throw new ArgumentOutOfRangeException(nameof(limits));

        var root = new DirectoryInfo(Path.GetFullPath(absoluteRootPath));
        if (!root.Exists)
            throw new DirectoryNotFoundException("A loaded mod root does not exist: " + root.FullName);

        // An ancestor symlink can also redirect the trusted game mod folder.
        for (DirectoryInfo? parent = root; parent != null; parent = parent.Parent)
            RejectLinks(parent);

        var files = new List<FileEntry>();
        var directories = new Stack<(DirectoryInfo Directory, string Relative, int Depth)>();
        directories.Push((root, "", 0));
        var visitedDirectories = 0;
        long totalBytes = 0;
        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, prefix, depth) = directories.Pop();
            if (++visitedDirectories > limits.MaxFiles || depth > limits.MaxDepth)
                throw new InvalidDataException("Mod directory tree exceeds depth or entry budget");

            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectLinks(item);
                if (!item.Name.IsNormalized(NormalizationForm.FormC) ||
                    item.Name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
                    throw new InvalidDataException("Noncanonical mod file path");
                var rel = prefix.Length == 0 ? item.Name : prefix + "/" + item.Name;
                if (Encoding.UTF8.GetByteCount(rel) > limits.MaxRelativePathBytes)
                    throw new InvalidDataException("Mod relative path exceeds byte budget");

                if ((item.Attributes & FileAttributes.Directory) != 0)
                {
                    if (depth >= limits.MaxDepth)
                        throw new InvalidDataException("Mod subdirectory nesting exceeds budget");
                    directories.Push((new DirectoryInfo(item.FullName), rel, depth + 1));
                }
                else
                {
                    var info = new FileInfo(item.FullName);
                    var length = info.Length;
                    if (length > limits.MaxSingleFileBytes || length < 0)
                        throw new InvalidDataException("Mod file exceeds per-file size budget");
                    totalBytes = checked(totalBytes + length);
                    if (totalBytes > limits.MaxTotalBytes || files.Count >= limits.MaxFiles)
                        throw new InvalidDataException("Mod content exceeds total size or file budget");
                    files.Add(new FileEntry(rel, item.FullName, length, info.LastWriteTimeUtc));
                }
            }
        }
        if (files.Count == 0)
            throw new InvalidDataException("Mod content directory contains no files");

        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            if (!unique.Add(file.RelativePath))
                throw new InvalidDataException("Case-colliding mod file paths");

        using (var digest = SHA256.Create())
        {
            using (var writer = new CryptoStream(Stream.Null, digest, CryptoStreamMode.Write))
            {
                writer.Write(DirectoryHeader, 0, DirectoryHeader.Length);
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] bodyHash;
                    using (var source = new FileStream(file.FullPath, FileMode.Open,
                        FileAccess.Read, FileShare.Read, 64 * 1024))
                    {
                        if (source.Length != file.Length)
                            throw new IOException("Mod file changed while hashing");
                        using (var fileDigest = SHA256.Create())
                            bodyHash = fileDigest.ComputeHash(source);
                        if (source.Length != file.Length)
                            throw new IOException("Mod file size changed during read");
                    }
                    var after = new FileInfo(file.FullPath);
                    RejectLinks(after);
                    if (after.Length != file.Length ||
                        after.LastWriteTimeUtc != file.LastWriteUtc)
                        throw new IOException("Mod file changed while hashing");

                    AppendString(writer, file.RelativePath);
                    AppendLength(writer, file.Length);
                    writer.Write(bodyHash, 0, bodyHash.Length);
                }
                writer.FlushFinalBlock();
            }
            return Hex(digest.Hash ??
                throw new InvalidOperationException("Tree digest was not finalized"));
        }
    }

    // This hashes the identities and 16-bit network short hashes of all
    // enumerated runtime Defs. It does NOT hash semantic Def field values and
    // is only an admission hint until full Def canonicalization is implemented.
    public static string HashResolvedDefIdentities(
        IReadOnlyList<ResolvedDefIdentity> definitions)
    {
        if (definitions is null || definitions.Count == 0 ||
            definitions.Count > MaxResolvedDefs)
            throw new ArgumentException("Invalid resolved Def identity count", nameof(definitions));
        var sorted = new List<ResolvedDefIdentity>(definitions.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in definitions)
        {
            if (def is null ||
                !SafeToken(def.TypeFullName, 256) ||
                !SafeToken(def.DefName, 128) ||
                !seen.Add(def.TypeFullName + "\0" + def.DefName))
                throw new ArgumentException("Duplicate or invalid loaded Def identity",
                    nameof(definitions));
            sorted.Add(def);
        }
        sorted.Sort((a, b) =>
        {
            var t = StringComparer.Ordinal.Compare(a.TypeFullName, b.TypeFullName);
            return t != 0 ? t : StringComparer.Ordinal.Compare(a.DefName, b.DefName);
        });

        using (var digest = SHA256.Create())
        {
            using (var writer = new CryptoStream(Stream.Null, digest, CryptoStreamMode.Write))
            {
                writer.Write(DefsHeader, 0, DefsHeader.Length);
                foreach (var def in sorted)
                {
                    AppendString(writer, def.TypeFullName);
                    AppendString(writer, def.DefName);
                    AppendLength(writer, def.ShortHash);
                }
                writer.FlushFinalBlock();
            }
            return Hex(digest.Hash ??
                throw new InvalidOperationException("Def digest was not finalized"));
        }
    }

    // Sources must come from the game's ACTUALLY loaded mod list, not a client.
    // This procedure is intentionally I/O-heavy and should be run at startup,
    // not on each Unity frame or while the game loop is pumping.
    public static ContentManifest BuildManifest(
        string gameVersion,
        IReadOnlyList<LoadedModDirectory> loadedModsInOrder,
        IReadOnlyList<ResolvedDefIdentity> actualResolvedDefs,
        ContentTreeLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        if (loadedModsInOrder is null || loadedModsInOrder.Count == 0 ||
            loadedModsInOrder.Count > 1024)
            throw new ArgumentException("Missing loaded mod directories",
                nameof(loadedModsInOrder));
        var loaded = new List<ModFingerprint>(loadedModsInOrder.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in loadedModsInOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mod is null || !SafeToken(mod.PackageId, 128) ||
                !SafeToken(mod.Version, 128) || !ids.Add(mod.PackageId))
                throw new ArgumentException("Invalid loaded mod or duplicate package ID",
                    nameof(loadedModsInOrder));
            loaded.Add(new ModFingerprint(mod.PackageId, mod.Version,
                HashModDirectory(mod.AbsoluteRootPath, limits, cancellationToken)));
        }
        return WorldBootstrapValidator.ValidateManifest(
            new ContentManifest(gameVersion, loaded,
                HashResolvedDefIdentities(actualResolvedDefs)));
    }

    private static bool SafeToken(string? value, int limit)
    {
        if (value is null || value.Length == 0 || value.Length > limit)
            return false;
        foreach (var c in value)
            if (char.IsControl(c) || char.IsWhiteSpace(c))
                return false;
        return true;
    }

    private static void RejectLinks(FileSystemInfo path)
    {
        if ((path.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Symlinks and reparse points are not allowed in mod tree");
    }

    private static void AppendString(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        AppendLength(stream, bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void AppendLength(Stream stream, long length)
    {
        var b = new byte[8];
        for (var i = 0; i < b.Length; i++)
            b[i] = (byte)(length >> (i * 8));
        stream.Write(b, 0, b.Length);
    }

    private static string Hex(byte[] hash) =>
        BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
}
