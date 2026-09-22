#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using AmongUs.GameOptions;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace TownOfHostForE.Modules
{
    // All owned state is diagnostic. Call sites protect both argument getters and formatting with Safe.
    internal static class Priority2to5Diagnostics
    {
        private const int Limit = 64;
        private const string Missing = "MISSING";
        private static bool active;
        private static string session = Missing;
        private static uint generation = uint.MaxValue;
        private static string wrapStage = Missing;
        private static string actualExiled = Missing;
        private static string hostExiled = Missing;
        private static long faults, optionReturns, ownerApplies, survivorLines, petLines;
        private static bool finalQueued;
        private static readonly Dictionary<GameOptionsSender, OptionMeta> options = new();
        private static readonly Dictionary<int, TargetWindow> targets = new();
        private static readonly Dictionary<byte, Viewer> viewers = new();
        private static readonly Dictionary<(string Event, byte Player), long> petEvents = new();
        private static readonly HashSet<byte> petRechecks = new();
        private static readonly HashSet<byte> airshipSpawned = new();

        private sealed class OptionMeta
        {
            internal long DirtyGeneration;
            internal string DirtyCaller = "UNKNOWN_ORIGIN";
            internal string Pass = "UNKNOWN_PASS";
            internal string Mode = "UNKNOWN_MODE";
            internal long LastOwnerLine = long.MinValue / 2;
        }
        private sealed class StreamSample
        {
            internal string Hash = "";
            internal long At;
        }
        private sealed class TargetWindow
        {
            internal long LastLine = long.MinValue / 2;
            internal long SendCount, SameHashResendCount, Within100, Within500;
            internal int DuplicateSamples;
            internal string? PendingDuplicate, LatestFields;
            internal bool FlushQueued;
            internal readonly Dictionary<(uint NetId, byte Index), StreamSample> Streams = new();
        }
        private sealed class Viewer
        {
            internal int Stages;
            internal bool RestorePresent;
            internal string Restore = Missing;
            internal string GlobalBeforeClear = Missing;
            internal string VotingReturned = Missing;
            internal bool PlanObserved, PlanSelfPresent;
            internal string PlanSelf = Missing;
            internal string SelfOmitted = Missing;
        }

        internal static void Safe(Action action)
        {
            try { action(); }
            catch { if (faults < long.MaxValue) faults++; }
        }
        private static long Now => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        private static string Text(object? value) => value?.ToString()?.Replace("\r", "_").Replace("\n", "_").Replace(" ", "_") ?? Missing;
        private static string Read(Func<object?> getter)
        {
            try { return Text(getter()); }
            catch { return Missing; }
        }
        private static void Log(string marker, string fields) => Logger.Info(fields, marker);
        private static bool Host => active && AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        private static void Clear()
        {
            options.Clear(); targets.Clear(); viewers.Clear();
            petEvents.Clear(); petRechecks.Clear(); airshipSpawned.Clear();
            generation = uint.MaxValue; wrapStage = actualExiled = hostExiled = Missing;
            finalQueued = false;
        }
        internal static void Begin()
        {
            active = false;
            Clear();
            session = Guid.NewGuid().ToString("N");
            faults = optionReturns = ownerApplies = survivorLines = petLines = 0;
            active = true;
        }
        internal static void End()
        {
            try
            {
                if (Host)
                    Log("P2_P3_P5_SUMMARY", $"Session={session} OptionSendReturned={optionReturns} OwnerApply={ownerApplies} SurvivorLines={survivorLines} PetLines={petLines} DiagnosticFaults={faults} RemoteAck=False");
            }
            finally { active = false; Clear(); }
        }
        internal static void Meeting()
        {
            if (!active) return;
            var current = MeetingVoteManager.H4MeetingGeneration;
            if (generation == current) return;
            generation = current;
            viewers.Clear(); petEvents.Clear(); petRechecks.Clear(); airshipSpawned.Clear();
            wrapStage = actualExiled = hostExiled = Missing;
            finalQueued = false;
        }
        private static OptionMeta? Meta(GameOptionsSender sender)
        {
            if (!Host) return null;
            if (options.TryGetValue(sender, out var meta)) return meta;
            if (options.Count >= Limit) return null;
            meta = new OptionMeta();
            options.Add(sender, meta);
            return meta;
        }
        internal static void Dirty(GameOptionsSender sender, string caller)
        {
            var meta = Meta(sender);
            if (meta == null) return;
            meta.DirtyGeneration++;
            meta.DirtyCaller = Text(caller);
        }
        internal static void Pass(GameOptionsSender sender, int? pass)
        {
            var meta = Meta(sender);
            if (meta != null) meta.Pass = pass?.ToString() ?? "UNKNOWN_PASS";
        }
        internal static void Mode(GameOptionsSender sender, object mode)
        {
            var meta = Meta(sender);
            if (meta != null) meta.Mode = Text(mode);
        }
        internal static void Options(GameOptionsSender sender, Il2CppStructArray<byte> array, byte index, int target, bool returned)
        {
            var meta = Meta(sender);
            if (meta == null) return;
            if (!targets.TryGetValue(target, out var window))
            {
                if (targets.Count >= Limit) return;
                targets.Add(target, window = new TargetWindow());
            }
            var bytes = new byte[array.Length];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = array[i];
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var netId = GameManager.Instance.NetId;
            var now = Now;
            var key = (netId, index);
            var prior = window.Streams.TryGetValue(key, out var last);
            var same = prior && last!.Hash == hash;
            var delta = prior ? now - last!.At : -1;
            var within100 = same && delta <= 100;
            var within500 = same && delta <= 500;
            if (returned)
            {
                optionReturns++; window.SendCount++;
                if (same) window.SameHashResendCount++;
                if (within100) window.Within100++;
                if (within500) window.Within500++;
                if (prior || window.Streams.Count < Limit)
                    window.Streams[key] = new StreamSample { Hash = hash, At = now };
            }
            var fields = $"Session={session} TargetClientId={target} GameManagerNetId={netId} LogicOptionsIndex={index} Mode={meta.Mode} Length={bytes.Length} SHA256={hash} DirtyGeneration={meta.DirtyGeneration} DirtyReason=UNKNOWN_ORIGIN ObservedDirtyCaller={meta.DirtyCaller} Pass={meta.Pass} SendCount={window.SendCount} SameHashResendCount={window.SameHashResendCount} DeltaMs={(prior ? delta.ToString() : Missing)} DuplicateWithin100ms={within100} DuplicateWithin500ms={within500} SendOrDisconnectReturned={returned} Within100Count={window.Within100} Within500Count={window.Within500} ObservedAtMs={now} ThresholdMeaning=OBSERVATION_ONLY RemoteAck=False";
            if (returned && within500 && window.DuplicateSamples < 3 && window.PendingDuplicate == null)
                window.PendingDuplicate = fields;
            window.LatestFields = fields;
            FlushOptions(target, window);
        }
        private static void FlushOptions(int target, TargetWindow window)
        {
            if (window.LatestFields == null) return;
            // Window and duplicate samples share this single per-target rate limit.
            var now = Now;
            if (now - window.LastLine < 1000)
            {
                if (window.FlushQueued) return;
                window.FlushQueued = true;
                var expectedSession = session;
                _ = new LateTask(() => Safe(() =>
                {
                    if (!Host || session != expectedSession || !targets.TryGetValue(target, out var current) || current != window) return;
                    window.FlushQueued = false;
                    // LateTask processes newly appended tasks in the same update: never reschedule here.
                    if (Now - window.LastLine >= 1000) FlushOptions(target, window);
                }), 1.05f, "");
                return;
            }
            window.LastLine = now;
            if (window.PendingDuplicate != null)
            {
                Log("P2_OPTIONS_DUP_SAMPLE", window.PendingDuplicate);
                window.PendingDuplicate = null;
                window.DuplicateSamples++;
            }
            else Log("P2_OPTIONS_WINDOW", window.LatestFields);
            window.LatestFields = null;
        }
        internal static void OwnerApply(GameOptionsSender sender, object mode)
        {
            var meta = Meta(sender);
            if (meta == null) return;
            ownerApplies++;
            var now = Now;
            if (now - meta.LastOwnerLine < 1000) return;
            meta.LastOwnerLine = now;
            Log("P2_OPTIONS_OWNER_APPLY", $"Session={session} TargetClientId=OWNER_LOCAL GameManagerNetId={Read(() => GameManager.Instance.NetId)} LogicOptionsIndex=LOCAL_COMPONENTS Mode={Text(mode)} Length=MISSING SHA256=MISSING DirtyGeneration={meta.DirtyGeneration} DirtyReason=UNKNOWN_ORIGIN ObservedDirtyCaller={meta.DirtyCaller} Pass={meta.Pass} SendCount=0 SameHashResendCount=0 DeltaMs=MISSING DuplicateWithin100ms=False DuplicateWithin500ms=False SendOrDisconnectReturned=MISSING OwnerApplyCount={ownerApplies} SerializedForDiagnostics=False");
        }
        private static Viewer? State(byte id)
        {
            Meeting();
            if (viewers.TryGetValue(id, out var state)) return state;
            if (viewers.Count >= Limit) return null;
            viewers.Add(id, state = new Viewer());
            return state;
        }
        internal static void CaptureRestore(IReadOnlyDictionary<(byte SeerId, byte TargetId), RoleTypes> snapshot, byte dummy)
        {
            if (!Host) return;
            foreach (var entry in snapshot)
            {
                if (entry.Key.SeerId != entry.Key.TargetId) continue;
                var state = State(entry.Key.SeerId);
                if (state == null) continue;
                state.RestorePresent = true;
                state.Restore = Text(entry.Value);
                state.GlobalBeforeClear = Text(dummy);
            }
        }
        internal static void VotingReturned(byte viewerId)
        {
            if (!Host) return;
            var state = State(viewerId);
            if (state != null) state.VotingReturned = "True";
        }
        internal static void Plan(PlayerControl viewer, List<(PlayerControl Target, RoleTypes Role)> plan, string stage)
        {
            if (!Host || stage != "POST_WRAPUP_RESTORE_HANDOFF") return;
            var state = State(viewer.PlayerId);
            if (state == null) return;
            state.PlanObserved = true;
            state.PlanSelfPresent = false;
            state.PlanSelf = Missing;
            foreach (var entry in plan)
            {
                if (entry.Target == null || entry.Target.PlayerId != viewer.PlayerId) continue;
                state.PlanSelfPresent = true;
                state.PlanSelf = Text(entry.Role);
            }
            state.SelfOmitted = state.PlanSelfPresent ? "NONE" : "ABSENT_FROM_RECORDED_PLAN_REASON_UNKNOWN";
        }
        internal static void WrapStage(string stage)
        {
            if (!Host) return;
            Meeting();
            wrapStage = stage;
        }
        internal static void PreVote(NetworkedPlayerInfo? exiled)
        {
            if (!Host) return;
            Meeting();
            actualExiled = Read(() => exiled?.PlayerId);
            hostExiled = Read(() => exiled?.Object == null ? null : (object)exiled.Object.AmOwner);
            Snapshot(1, "PRE_VOTE");
        }
        internal static void PostWrapUp(NetworkedPlayerInfo? exiled, bool syncSelfGhostReturned)
        {
            if (!Host) return;
            Meeting();
            actualExiled = Read(() => exiled?.PlayerId);
            hostExiled = Read(() => exiled?.Object == null ? null : (object)exiled.Object.AmOwner);
            if (syncSelfGhostReturned && exiled != null)
            {
                var state = State(exiled.PlayerId);
                if (state != null && state.PlanObserved && !state.PlanSelfPresent)
                    state.SelfOmitted = "SYNC_SELF_GHOST_SEND_RETURNED";
            }
            Snapshot(2, "POST_WRAPUP");
            Pets("MEETING_END");
        }
        internal static void ScheduleFinal()
        {
            if (!Host) return;
            if (finalQueued) return;
            finalQueued = true;
            var expectedSession = session;
            var expectedGeneration = generation;
            _ = new LateTask(() => Safe(() =>
            {
                if (!Host || session != expectedSession || MeetingVoteManager.H4MeetingGeneration != expectedGeneration) return;
                Snapshot(4, "FINAL");
            }), 2.05f, "");
        }
        private static string V14Dummy(byte viewerId)
        {
            return Read(() =>
            {
                var type = typeof(ExileControllerWrapUpPatch.VanillaActualExileCriticalLeaseAbV14A);
                var leases = type.GetField("Leases", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as IDictionary;
                var lease = leases != null && leases.Contains(viewerId) ? leases[viewerId] : null;
                if (lease == null) return null;
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var leaseGeneration = lease.GetType().GetProperty("MeetingGeneration", flags)?.GetValue(lease);
                if (leaseGeneration is not uint gen || gen != generation) return null;
                return lease.GetType().GetProperty("DummyId", flags)?.GetValue(lease);
            });
        }
        private static void Snapshot(int bit, string markerStage)
        {
            if (!Host) return;
            AntiBlackout.Priority2to5CaptureRestore();
            foreach (var viewer in Main.AllPlayerControls)
            {
                Safe(() =>
                {
                    if (viewer == null || viewer.Data == null || viewer.AmOwner || viewer.IsModClient()) return;
                    var state = State(viewer.PlayerId);
                    if (state == null || (state.Stages & bit) != 0) return;
                    var rolePresent = SelectRolesPatch.TryGetExileDisplayRoleViewPermanent(viewer.PlayerId, viewer.PlayerId, out var role);
                    PlayerState.AllPlayerStates.TryGetValue(viewer.PlayerId, out var playerState);
                    var stage = markerStage == "PRE_VOTE" ? "BEFORE_VOTING_COMPLETE" : wrapStage;
                    if (markerStage == "FINAL" && wrapStage == "BEFORE_NATIVE_WRAPUP_AND_SPAWN")
                        stage = airshipSpawned.Contains(viewer.PlayerId) ? "AIRSHIP_SPAWN_COMPLETED" : "AIRSHIP_SPAWN_NOT_OBSERVED";
                    var fields = $"Session={session} MeetingGeneration={generation} Stage={stage} ViewerId={viewer.PlayerId} ViewerClientId={viewer.GetClientId()} Owner={viewer.AmOwner} Mod={viewer.IsModClient()} Vanilla=True ActualExiledId={actualExiled} HostExiled={hostExiled} RoleViewSelfPresent={rolePresent} RoleViewSelf={(rolePresent ? Text(role) : Missing)} RestoreSelfPresent={state.RestorePresent} RestoreSelf={state.Restore} DataIsDead={viewer.Data.IsDead} PlayerStateIsDead={Read(() => playerState?.IsDead)} Disconnected={viewer.Data.Disconnected} {AntiBlackout.Priority2to5TopologyFields(viewer.PlayerId)} GlobalSafetyDummyBeforeClear={state.GlobalBeforeClear} V14ADummy={V14Dummy(viewer.PlayerId)} V27PlanSelfRole={state.PlanSelf} SelfOmittedReason={state.SelfOmitted} VotingCompleteSendReturned={state.VotingReturned} HasSpawned={Read(() => playerState?.HasSpawned)} PendingDeath={Main.AfterMeetingDeathPlayers.ContainsKey(viewer.PlayerId)} RecordedWireRole=HOST_SEND_CONTENT RemoteAck=False";
                    state.Stages |= bit;
                    survivorLines++;
                    Log("P3_SURVIVOR_" + markerStage, fields);
                });
            }
        }
        internal static void AirshipSpawn(PlayerControl player)
        {
            if (!Host || Main.isFirstTurn) return;
            Meeting();
            if (generation == 0 || generation == uint.MaxValue) return;
            if (airshipSpawned.Count < Limit) airshipSpawned.Add(player.PlayerId);
            Pet(player, "AIRSHIP_MEETING_SPAWN_COMPLETED", false);
        }
        internal static void Pets(string eventName)
        {
            if (!Host) return;
            Meeting();
            foreach (var player in Main.AllPlayerControls)
                Safe(() => Pet(player, eventName, false));
        }
        private static void Pet(PlayerControl? player, string eventName, bool recheck)
        {
            if (!Host || player == null) return;
            if (!recheck)
            {
                var key = (eventName, player.PlayerId);
                var now = Now;
                if (petEvents.TryGetValue(key, out var last))
                {
                    if (now - last < 1000) return;
                }
                else if (petEvents.Count >= Limit * 4) return;
                petEvents[key] = now;
            }
            var data = player.Data;
            var outfit = player.CurrentOutfit;
            var cosmetics = player.cosmetics;
            // Native field getter: never use CurrentPet, GetPet, or a mutating pet API here.
            var pet = cosmetics == null ? null : cosmetics.currentPet;
            PlayerState.AllPlayerStates.TryGetValue(player.PlayerId, out var state);
            var rendererCount = Read(() =>
            {
                if (pet == null || pet.renderers == null) return null;
                var enabled = 0;
                foreach (var renderer in pet.renderers)
                    if (renderer != null && renderer.enabled) enabled++;
                return enabled;
            });
            var fields = $"Session={session} Event={eventName} MeetingGeneration={generation} PlayerId={player.PlayerId} Owner={player.AmOwner} DefaultPetId={Read(() => data?.DefaultOutfit?.PetId)} CurrentOutfitType={Read(() => player.CurrentOutfitType)} CurrentPetId={Read(() => outfit?.PetId)} PetSequenceId={Read(() => outfit?.PetSequenceId)} DataIsDead={Read(() => data?.IsDead)} PlayerStateIsDead={Read(() => state?.IsDead)} Disconnected={Read(() => data?.Disconnected)} HasSpawned={Read(() => state?.HasSpawned)} PlayerControlInstanceId={Read(() => player.GetInstanceID())} CosmeticsInstanceId={Read(() => cosmetics == null ? null : (object)cosmetics.GetInstanceID())} CurrentPetExists={(cosmetics == null ? Missing : Text(pet != null))} PetInstanceId={Read(() => pet == null ? null : (object)pet.GetInstanceID())} ActiveSelf={Read(() => pet == null ? null : (object)pet.gameObject.activeSelf)} ActiveInHierarchy={Read(() => pet == null ? null : (object)pet.gameObject.activeInHierarchy)} PetVisible={Read(() => pet == null ? null : (object)pet.Visible)} RendererEnabledCount={rendererCount} PlayerVisible={Read(() => player.Visible)} CosmeticsVisible={Read(() => cosmetics == null ? null : (object)cosmetics.Visible)} LockVisible={Read(() => cosmetics == null ? null : (object)cosmetics.lockVisible)} PetHiddenByViper={Read(() => cosmetics == null ? null : (object)cosmetics.petHiddenByViper)} Camouflage={Camouflage.IsCamouflage} Mushroom={Read(() => Utils.IsActive(SystemTypes.MushroomMixupSabotage))} Shapeshift={Read(() => Main.CheckShapeshift.TryGetValue(player.PlayerId, out var shifted) ? (object)shifted : null)}";
            petLines++;
            Log(recheck ? "P5_HOST_PET_RECHECK" : "P5_HOST_PET_SNAPSHOT", fields);
            // One recheck per player/generation, only when an expected nonempty outfit pet has no instance.
            if (recheck || pet != null || outfit == null || string.IsNullOrEmpty(outfit.PetId) ||
                petRechecks.Count >= Limit || !petRechecks.Add(player.PlayerId)) return;
            var expectedSession = session;
            var expectedGeneration = generation;
            var instanceId = player.GetInstanceID();
            _ = new LateTask(() => Safe(() =>
            {
                if (!Host || session != expectedSession || MeetingVoteManager.H4MeetingGeneration != expectedGeneration ||
                    player == null || player.GetInstanceID() != instanceId) return;
                Pet(player, eventName, true);
            }), 0.5f, "");
        }
    }
}
