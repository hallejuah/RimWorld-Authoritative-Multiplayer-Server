using System.IO;
using Multiplayer.Authoritative;

internal static class ContentFingerprintTests
{
    public static void BytesAndOrdering()
    {
        WithTemp(root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "Defs"));
            File.WriteAllText(Path.Combine(root, "Defs", "B.xml"), "<Def>B</Def>");
            File.WriteAllText(Path.Combine(root, "Defs", "A.xml"), "<Def>A</Def>");
            var first = ContentFingerprintBuilder.HashModDirectory(root);
            var firstTime = File.GetLastWriteTimeUtc(Path.Combine(root, "Defs", "B.xml"));
            File.SetLastWriteTimeUtc(Path.Combine(root, "Defs", "B.xml"),
                firstTime.AddSeconds(-12));
            Equal(first, ContentFingerprintBuilder.HashModDirectory(root),
                "file modification timestamp affected digest");

            File.WriteAllText(Path.Combine(root, "Defs", "A.xml"), "<Def>CHANGED</Def>");
            Different(first, ContentFingerprintBuilder.HashModDirectory(root),
                "changed content not included in digest");
            File.WriteAllText(Path.Combine(root, "Defs", "A.xml"), "<Def>A</Def>");
            Equal(first, ContentFingerprintBuilder.HashModDirectory(root),
                "restored bytes didn't restore canonical hash");

            File.Move(Path.Combine(root, "Defs", "A.xml"),
                Path.Combine(root, "Defs", "C.xml"));
            Different(first, ContentFingerprintBuilder.HashModDirectory(root),
                "file name not included in digest");
        });
        WithTemp(a => WithTemp(b =>
        {
            Directory.CreateDirectory(Path.Combine(a, "Patch"));
            Directory.CreateDirectory(Path.Combine(b, "Patch"));
            File.WriteAllText(Path.Combine(a, "z.txt"), "Z");
            File.WriteAllText(Path.Combine(a, "Patch", "a.txt"), "A");
            File.WriteAllText(Path.Combine(b, "Patch", "a.txt"), "A");
            File.WriteAllText(Path.Combine(b, "z.txt"), "Z");
            Equal(ContentFingerprintBuilder.HashModDirectory(a),
                ContentFingerprintBuilder.HashModDirectory(b),
                "enumeration order made content hash unstable");
        }));
    }

    public static void Safety()
    {
        WithTemp(root =>
        {
            var file = Path.Combine(root, "Content.txt");
            File.WriteAllText(file, "123456789");
            Throws<InvalidDataException>(() =>
                ContentFingerprintBuilder.HashModDirectory(root,
                    new ContentTreeLimits(MaxFiles: 1, MaxSingleFileBytes: 3)));
            Throws<InvalidDataException>(() =>
                ContentFingerprintBuilder.HashModDirectory(root,
                    new ContentTreeLimits(MaxFiles: 1, MaxTotalBytes: 3)));
            Throws<ArgumentOutOfRangeException>(() =>
                ContentFingerprintBuilder.HashModDirectory(root,
                    new ContentTreeLimits(MaxDepth: -1)));
            File.WriteAllText(Path.Combine(root, "second.txt"), "2");
            Throws<InvalidDataException>(() =>
                ContentFingerprintBuilder.HashModDirectory(root,
                    new ContentTreeLimits(MaxFiles: 1)));
            Throws<OperationCanceledException>(() =>
                ContentFingerprintBuilder.HashModDirectory(root,
                    cancellationToken: new CancellationToken(true)));

            // This is run on Linux CI where POSIX symlinks can be created.
            var target = Path.Combine(root, "Content.txt");
            var link = Path.Combine(root, "link.txt");
            try
            {
                File.CreateSymbolicLink(link, target);
                Throws<InvalidDataException>(() =>
                    ContentFingerprintBuilder.HashModDirectory(root));
            }
            catch (UnauthorizedAccessException) { /* Windows developer without symlink right */ }
            catch (PlatformNotSupportedException) { /* unsupported local test OS */ }
            catch (NotSupportedException) { /* unsupported local file system */ }
        });
        WithTemp(empty =>
        {
            Throws<InvalidDataException>(() =>
                ContentFingerprintBuilder.HashModDirectory(empty));
        });
    }

    public static void DefIdentities()
    {
        var defs = new[]
        {
            new ResolvedDefIdentity("Verse.ThingDef", "Steel", 123),
            new ResolvedDefIdentity("Verse.TerrainDef", "Soil", 456)
        };
        var digest = ContentFingerprintBuilder.HashResolvedDefIdentities(defs);
        Equal(digest, ContentFingerprintBuilder.HashResolvedDefIdentities(defs.Reverse().ToArray()),
            "resolved Def enumeration order changed digest");
        Different(digest, ContentFingerprintBuilder.HashResolvedDefIdentities(
            new[] { defs[0] with { ShortHash = 124 }, defs[1] }),
            "changed Def short hash ignored");
        Throws<ArgumentException>(() =>
            ContentFingerprintBuilder.HashResolvedDefIdentities(
                new[] { defs[0], defs[0] }));
        Throws<ArgumentException>(() =>
            ContentFingerprintBuilder.HashResolvedDefIdentities(Array.Empty<ResolvedDefIdentity>()));
    }

    public static void Manifest()
    {
        WithTemp(a => WithTemp(b =>
        {
            File.WriteAllText(Path.Combine(a, "About.xml"), "mod alpha");
            File.WriteAllText(Path.Combine(b, "About.xml"), "mod beta");
            var defs = new[] { new ResolvedDefIdentity("Verse.ThingDef", "Steel", 123) };
            var list = new[]
            {
                new LoadedModDirectory("owner.alpha", "1.0", a),
                new LoadedModDirectory("owner.beta", "2.0", b)
            };
            var one = ContentFingerprintBuilder.BuildManifest("1.6.4566", list, defs);
            var two = ContentFingerprintBuilder.BuildManifest("1.6.4566", list, defs);
            Verify(ClientWorldBootstrapReplica.Compatible(one, two),
                "identical installed content denied");
            var backwards = ContentFingerprintBuilder.BuildManifest("1.6.4566",
                list.Reverse().ToArray(), defs);
            Verify(!ClientWorldBootstrapReplica.Compatible(one, backwards),
                "different loaded mod order accepted");
            File.WriteAllText(Path.Combine(b, "About.xml"), "mod beta altered");
            var altered = ContentFingerprintBuilder.BuildManifest("1.6.4566", list, defs);
            Verify(!ClientWorldBootstrapReplica.Compatible(one, altered),
                "mutated mod files were accepted");
            Throws<ArgumentException>(() =>
                ContentFingerprintBuilder.BuildManifest("1.6.4566",
                    new[] { list[0], list[0] }, defs));
            Throws<ArgumentException>(() =>
                ContentFingerprintBuilder.BuildManifest("1.7.0", list, defs));
            Throws<OperationCanceledException>(() =>
                ContentFingerprintBuilder.BuildManifest("1.6.4566", list, defs,
                    cancellationToken: new CancellationToken(true)));
        }));
    }

    private static void WithTemp(Action<string> work)
    {
        var root = Path.Combine(Path.GetTempPath(), "rw-auth-hash-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { work(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Verify(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    private static void Equal(string left, string right, string message) =>
        Verify(left == right, message);
    private static void Different(string left, string right, string message) =>
        Verify(left != right, message);

    private static void Throws<T>(Action work) where T : Exception
    {
        try { work(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
