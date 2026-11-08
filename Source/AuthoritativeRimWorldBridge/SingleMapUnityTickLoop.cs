using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Multiplayer.Authoritative.RimWorldBridge;

// Explicit opt-in only; this assembly never automatically activates itself.
// Start must be called from Unity's main thread after a map is loaded and the
// original game speed is paused. No socket or multiplayer handshake exists yet.
public static class SingleMapUnityTickLoop
{
    private static readonly Harmony Harmony = new("rimworld.authoritative.singlemap.runtime");
    private static LoadedSingleMapHost? active;
    private static bool patched;

    public static bool IsActive => active != null;

    public static void Start(LoadedSingleMapHost host)
    {
        if (host == null) throw new ArgumentNullException(nameof(host));
        if (active != null)
            throw new InvalidOperationException("A single-map host is already active");

        // This asserts Unity-thread access and an already loaded, paused game.
        host.Host.CaptureSnapshot();
        if (!patched)
        {
            Harmony.PatchAll(typeof(SingleMapUnityTickLoop).Assembly);
            patched = true;
        }

        active = host;
        Log.Message("[Authoritative] Single-map simulation loop activated on the Unity thread");
    }

    public static void Stop()
    {
        if (active == null) return;
        active = null;
        Log.Message("[Authoritative] Single-map simulation loop stopped; game remains paused");
    }

    internal static void RunFrame()
    {
        var session = active;
        if (session == null) return;

        try
        {
            var elapsed = Time.deltaTime;
            if (float.IsNaN(elapsed) || float.IsInfinity(elapsed) ||
                elapsed < 0f || elapsed > 5f)
                throw new InvalidOperationException("Invalid Unity delta time");

            // Real Verse callbacks are dispatched by the game's own Update
            // loop, never by the LiteNetLib network worker.
            session.Host.Pump(TimeSpan.FromSeconds(elapsed));
        }
        catch (Exception e)
        {
            // Fail closed: an unexpected partial mutation must not be hidden.
            active = null;
            Log.Error("[Authoritative] Stopping simulation after host error: " + e);
        }
    }

    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
    [HarmonyPriority(Priority.First)]
    private static class PauseVanillaGameTicks
    {
        static bool Prefix() => !IsActive;
    }

    [HarmonyPatch(typeof(Root_Play), nameof(Root_Play.Update))]
    private static class PumpFromUnityUpdate
    {
        static void Postfix() => RunFrame();
    }
}
