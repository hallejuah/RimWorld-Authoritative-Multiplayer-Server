using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using Multiplayer.Authoritative;
using Verse;

namespace Multiplayer.Authoritative.RimWorldBridge;

// Captures authoritative mod paths and loaded Def identities from RimWorld.
// CaptureOnGameThread must be invoked by the owner-thread bridge, after
// PlayDataLoader finishes. The potentially slow disk hashing is a separate,
// pure step that can run on a worker after the input lists have been copied.
public sealed class VerseContentManifestInputs
{
    private readonly LoadedModDirectory[] mods;
    private readonly ResolvedDefIdentity[] defs;
    public string GameVersion { get; }
    public int ModCount => mods.Length;
    public int DefIdentityCount => defs.Length;

    private VerseContentManifestInputs(string version,
        LoadedModDirectory[] mods, ResolvedDefIdentity[] defs)
    {
        GameVersion = version;
        this.mods = mods;
        this.defs = defs;
    }

    internal static VerseContentManifestInputs CaptureOnGameThread()
    {
        if (Current.ProgramState != ProgramState.Playing ||
            Current.Game == null || Find.TickManager == null)
            throw new InvalidOperationException("Play data and a game must already be loaded");

        var version = ReadActualGameVersion();
        var mods = LoadedModManager.RunningModsListForReading
            .Select(mod => new LoadedModDirectory(
                mod.PackageId, ReadDeclaredVersion(mod.RootDir), mod.RootDir))
            .ToArray();
        if (mods.Length == 0)
            throw new InvalidOperationException("No active RimWorld content packs were found");

        var identities = new List<ResolvedDefIdentity>();
        // Reflection allows the runtime to discover modded DefDatabase<T>
        // categories in addition to known vanilla types. It fails closed if
        // it cannot enumerate all databases; no fake fallback hash is used.
        var assembly = typeof(Def).Assembly;
        var genType = assembly.GetType("Verse.GenDefDatabase", throwOnError: true)
            ?? throw new InvalidOperationException("Verse.GenDefDatabase not found");
        var allTypes = genType.GetMethod("AllDefTypesWithDatabases",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Def database enumeration API unavailable");
        if (allTypes.Invoke(null, null) is not IEnumerable types)
            throw new InvalidOperationException("Def database type list is unavailable");

        foreach (var item in types)
        {
            if (item is not Type type || !typeof(Def).IsAssignableFrom(type) ||
                type.FullName is null)
                throw new InvalidOperationException("Invalid runtime Def database type");

            var closed = typeof(DefDatabase<>).MakeGenericType(type);
            const BindingFlags flags =
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var property = closed.GetProperty("AllDefsListForReading", flags);
            var field = closed.GetField("AllDefsListForReading", flags);
            var list = property?.GetValue(null, null) ?? field?.GetValue(null);
            if (list is not IEnumerable resolved)
                throw new InvalidOperationException("Cannot inspect live DefDatabase: " + type.FullName);

            foreach (var entry in resolved)
            {
                if (entry is not Def def || string.IsNullOrWhiteSpace(def.defName))
                    throw new InvalidOperationException("Loaded Def has no valid identity");
                identities.Add(new ResolvedDefIdentity(
                    type.FullName, def.defName, def.shortHash));
            }
        }
        if (identities.Count == 0)
            throw new InvalidOperationException("No resolved game Defs found");

        return new VerseContentManifestInputs(version, mods, identities.ToArray());
    }

    // Never do this on a Unity frame; full mod-tree scans can be expensive.
    public ContentManifest HashOnWorker(
        ContentTreeLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        return ContentFingerprintBuilder.BuildManifest(GameVersion,
            mods, defs, limits, cancellationToken);
    }

    private static string ReadActualGameVersion()
    {
        var versionType = typeof(Def).Assembly.GetType(
            "RimWorld.VersionControl", throwOnError: true)
            ?? throw new InvalidOperationException("RimWorld version API unavailable");
        foreach (var name in new[] {
            "CurrentVersionString",
            "CurrentVersionStringWithoutRev",
            "CurrentVersionStringWithoutBuild"
        })
        {
            const BindingFlags flags =
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var member = versionType.GetProperty(name, flags);
            var raw = member?.GetValue(null, null) as string ??
                versionType.GetField(name, flags)?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            // VersionControl can append 'rev...' after its numeric version.
            // Preserve the actual numeric build when the API reports one.
            var found = Regex.Match(raw, @"(?<![0-9])1\.6(?:\.[0-9]+)?(?![0-9])");
            if (found.Success)
                return found.Value;
        }
        throw new InvalidOperationException(
            "A genuine RimWorld 1.6 version could not be established");
    }

    private static string ReadDeclaredVersion(string modRoot)
    {
        var about = Path.Combine(modRoot, "About", "About.xml");
        if (!File.Exists(about))
            return "unspecified"; // Explicitly absent; never fabricate a version.
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1024 * 1024,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };
        using (var reader = XmlReader.Create(about, settings))
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element ||
                    !string.Equals(reader.LocalName, "modVersion",
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var version = reader.ReadElementContentAsString().Trim();
                if (version.Length == 0)
                    return "unspecified";
                if (version.Length > 128 ||
                    version.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                    throw new InvalidDataException("Mod declares an invalid version token");
                return version;
            }
        }
        return "unspecified";
    }
}
