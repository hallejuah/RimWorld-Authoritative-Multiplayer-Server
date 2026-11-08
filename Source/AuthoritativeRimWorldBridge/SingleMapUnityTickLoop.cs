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
    internal static bool IsRunningHost(LoadedSingleMapHost host) =>
        ReferenceEquals(active, host);

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

        host.BeginStateFeed();
        active = host;
        Log.Message("[Authoritative] Single-map simulation loop activated on the Unity thread");
    }

    public static void Stop()
    {
        if (active == null) return;
        var previous = active;
        active = null;
        previous.StopLocalTransport();
        previous.Host.RejectPendingCommands("Authoritative host stopped");
        Log.Message("[Authoritative] Single-map simulation loop stopped; game remains paused");
    }

    internal static void RunFrame()
    {
        var session = active;
        if (session == null) return;

        try
        {
            // Menu windows can force an EFFECTIVE pause without selecting the
            // vanilla Paused speed. After closing one, RimWorld can restore its
            // previous CurTimeSpeed. While the explicit authoritative host is
            // active, the Harmony TickManagerUpdate prefix suppresses all
            // vanilla ticks; the authoritative scheduler is their sole owner.
            // Reassert the selected vanilla pause on the Unity thread before
            // the driver's strict readiness checks and DoSingleTick.
            var manager = Find.TickManager;
            if (manager == null)
                throw new InvalidOperationException("RimWorld tick manager was unloaded");
            if (manager.CurTimeSpeed != TimeSpeed.Paused)
            {
                manager.CurTimeSpeed = TimeSpeed.Paused;
                Log.Warning("[Authoritative] Restored vanilla Paused speed after a menu or speed change while the authoritative host is active");
            }

            var elapsed = Time.deltaTime;
            if (float.IsNaN(elapsed) || float.IsInfinity(elapsed) ||
                elapsed < 0f || elapsed > 5f)
                throw new InvalidOperationException("Invalid Unity delta time");

            // Real Verse callbacks are dispatched by the game's own Update
            // loop, never by the LiteNetLib network worker.
            session.Host.Pump(TimeSpan.FromSeconds(elapsed));
            session.PublishState();
        }
        catch (Exception e)
        {
            // Fail closed: an unexpected partial mutation must not be hidden.
            // Vanilla TickManagerUpdate is no longer suppressed after active
            // is cleared; guarantee a paused selected speed before that switch.
            // If the game is being unloaded this attempt may fail: report both
            // faults instead of swallowing the original host exception.
            try
            {
                var manager = Find.TickManager;
                if (manager != null)
                    manager.CurTimeSpeed = TimeSpeed.Paused;
            }
            catch (Exception pauseException)
            {
                Log.Error("[Authoritative] Could not pause vanilla ticks during emergency shutdown: " + pauseException);
            }
            active = null;
            session.StopLocalTransport();
            session.Host.RejectPendingCommands("Authoritative host failed");
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
