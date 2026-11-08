using System;
using System.Linq;
using System.Security.Cryptography;
using Multiplayer.Authoritative;
using Multiplayer.Authoritative.RimWorldBridge;
using Multiplayer.Authoritative.Transport;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Authoritative.Probe;

// Opt-in mod-settings entrypoint, not an automatic launcher or a multiplayer
// replacement. A separate RimWorld 1.6 test profile is required.
public sealed class AuthoritativeProbeMod : Mod
{
    private LoadedSingleMapHost? host;
    private string? credential;
    private string error = "";
    private int port;

    public AuthoritativeProbeMod(ModContentPack content) : base(content) { }

    public override string SettingsCategory() => "Authoritative Probe (local only)";

    public override void DoSettingsWindowContents(Rect area)
    {
        var y = area.y;
        var width = Math.Min(area.width, 610f);
        Widgets.Label(new Rect(area.x, y, width, 48f),
            "Experimental authoritative server: ONE loaded map, local TCP only. " +
            "Not a playable multiplayer mod. Back up the colony before starting.");
        y += 56f;

        if (host == null)
        {
            Widgets.Label(new Rect(area.x, y, width, 34f),
                "Load a vanilla colony, pause time, then start the probe.");
            y += 40f;
            if (Widgets.ButtonText(new Rect(area.x, y, 245f, 35f),
                "Start local authoritative probe"))
            {
                try
                {
                    StartProbe();
                    error = "";
                }
                catch (Exception e)
                {
                    error = e.Message;
                    Log.Error("[Authoritative Probe] start failed: " + e);
                }
            }
            y += 43f;
        }
        else
        {
            Widgets.Label(new Rect(area.x, y, width, 36f),
                "Running on 127.0.0.1:" + port +
                " (credentials are not logged or saved).");
            y += 42f;
            if (Widgets.ButtonText(new Rect(area.x, y, 245f, 35f),
                "Copy one-time credential"))
                GUIUtility.systemCopyBuffer = credential ?? "";
            y += 43f;
            if (Widgets.ButtonText(new Rect(area.x, y, 245f, 35f),
                "Stop authoritative probe"))
            {
                SingleMapUnityTickLoop.Stop();
                host = null;
                credential = null;
                port = 0;
            }
            y += 43f;
        }

        if (error.Length != 0)
            Widgets.Label(new Rect(area.x, y, width, 65f), "Start failed: " + error);
    }

    private void StartProbe()
    {
        if (host != null || SingleMapUnityTickLoop.IsActive)
            throw new InvalidOperationException("An authoritative loop is already active");
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Multiplayer"))
            throw new InvalidOperationException(
                "Disable the original Multiplayer mod in this isolated test profile");
        if (Current.ProgramState != ProgramState.Playing || Current.Game == null ||
            Find.TickManager == null || Find.Maps.Count != 1)
            throw new InvalidOperationException("Load a game with exactly one map first");
        if (Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
            throw new InvalidOperationException("Pause vanilla time before starting the probe");

        var faction = Faction.OfPlayer;
        if (faction == null || faction.loadID <= 0)
            throw new InvalidOperationException("A valid player faction is required");

        var domainId = MapDomainIds.FromMapUniqueId(Find.Maps[0].uniqueID);
        var sessionPolicy = new LocalProbeSessionPolicy(faction.loadID, domainId);
        var loaded = LoadedSingleMapHost.Attach(Find.Maps[0], faction.loadID, sessionPolicy);

        SingleMapUnityTickLoop.Start(loaded);
        try
        {
            loaded.Host.Domains.SetSpeed(domainId, 1);
            var localPort = loaded.StartLocalTransport(sessionPolicy);
            host = loaded;
            credential = sessionPolicy.Credential;
            port = localPort;
            Log.Message("[Authoritative Probe] local-only server running on 127.0.0.1:" + port +
                ". Never forward this port. Credential is not logged.");
        }
        catch
        {
            SingleMapUnityTickLoop.Stop();
            throw;
        }
    }
}

// This policy is only for a one-client local proof. A 256-bit token is generated
// per activation and never written to disk. No external authentication exists.
// The session name is PUBLIC; possession of the secret token is the credential.
internal sealed class LocalProbeSessionPolicy : ICommandAuthorization, ILoopbackAuthenticator
{
    public const string SessionName = "local-probe";
    private readonly int factionId;
    private readonly int domainId;

    public string Credential { get; }

    public LocalProbeSessionPolicy(int factionId, int domainId)
    {
        if (factionId <= 0 || domainId <= 0)
            throw new ArgumentOutOfRangeException(nameof(factionId));
        this.factionId = factionId;
        this.domainId = domainId;
        var key = new byte[32];
        using (var random = RandomNumberGenerator.Create())
            random.GetBytes(key);
        Credential = Convert.ToBase64String(key);
        Array.Clear(key, 0, key.Length);
    }

    public bool TryAuthenticate(string candidate, out string authenticatedSessionId)
    {
        authenticatedSessionId = "";
        if (!ConstantTimeEquals(candidate, Credential)) return false;
        authenticatedSessionId = SessionName;
        return true;
    }

    public bool TryGetFaction(string authenticatedSessionId, out int result)
    {
        result = authenticatedSessionId == SessionName ? factionId : 0;
        return result > 0;
    }

    public bool OwnsDomain(int faction, int domain) =>
        faction == factionId && domain == domainId;

    private static bool ConstantTimeEquals(string? a, string b)
    {
        if (a is null || a.Length != b.Length) return false;
        var difference = 0;
        for (var i = 0; i < b.Length; i++)
            difference |= a[i] ^ b[i];
        return difference == 0;
    }
}
