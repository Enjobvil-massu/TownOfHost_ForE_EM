using AmongUs.Data;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using InnerNet;
using TownOfHostForE.Modules;
using System;
using System.Collections.Generic;
using TownOfHostForE.Roles.AddOns.Common;
using TownOfHostForE.Roles.Animals;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Crewmate;
using TownOfHostForE.Roles.Neutral;

namespace TownOfHostForE
{
    class ExileControllerWrapUpPatch
    {
        public static NetworkedPlayerInfo AntiBlackout_LastExiled;

        internal static class VanillaActualExileAtomicDeadGhostAbV34
        {
            private const string Marker = "VANILLA_ACTUAL_EXILE_ATOMIC_DEAD_GHOST_AB_V3_4";
            private const int MaxAtomicPacketBytes = 1000;
            private const string RequiredLedgerPath = "PREPARED_REAL_TARGETED";

            private static readonly System.Reflection.FieldInfo? VanillaIntroRoleMapField =
                AccessTools.Field(typeof(SelectRolesPatch), "VanillaIntroRoleMap");
            private static readonly System.Reflection.FieldInfo? RestoreSnapshotField =
                AccessTools.Field(typeof(AntiBlackout), "ExileDisplayRestoreSnapshotPermanent");
            private static readonly System.Reflection.FieldInfo? SafetyAppliedField =
                AccessTools.Field(typeof(AntiBlackout), "ExileDisplaySafetyAppliedPermanent");

            private static uint meetingGeneration;
            private static uint pendingMeetingGeneration;
            private static byte pendingPlayerId = byte.MaxValue;
            private static int pendingClientId = -1;
            private static RoleTypes pendingRole;
            private static RoleTypes removedRoleView;
            private static RoleTypes removedRestoreSnapshot;
            private static long pendingStartTicks;
            private static bool provisionalFirstHold;
            private static bool firstSelfGhostHeld;
            private static bool restoreMetadataRemoved;
            private static bool restoreCallObserved;
            private static bool secondSelfGhostSuppressed;

            private static Dictionary<(byte SeerId, byte TargetId), RoleTypes>? GetRoleViewMap()
            {
                return VanillaIntroRoleMapField?.GetValue(null)
                    as Dictionary<(byte SeerId, byte TargetId), RoleTypes>;
            }

            private static Dictionary<(byte SeerId, byte TargetId), RoleTypes>? GetRestoreSnapshot()
            {
                return RestoreSnapshotField?.GetValue(null)
                    as Dictionary<(byte SeerId, byte TargetId), RoleTypes>;
            }

            private static bool IsSafetyApplied()
            {
                return SafetyAppliedField?.GetValue(null) is bool applied && applied;
            }

            private static bool IsSupportedGhost(RoleTypes role)
            {
                return role is RoleTypes.CrewmateGhost or RoleTypes.ImpostorGhost;
            }

            private static double ElapsedMilliseconds()
            {
                return pendingStartTicks == 0
                    ? 0d
                    : TimeSpan.FromTicks(DateTime.UtcNow.Ticks - pendingStartTicks).TotalMilliseconds;
            }

            private static void LogStage(
                string stage,
                bool hostDataIsDead,
                bool playerStateIsDead,
                int packetBytes = 0,
                int submessageCount = 0,
                int dataSubmessageIndex = -1,
                int ghostSubmessageIndex = -1)
            {
                Logger.Info(
                    $"Stage={stage} | MeetingGeneration={pendingMeetingGeneration} | PlayerId={pendingPlayerId} | ClientId={pendingClientId} | MappingMode=MIXED_GHOST_MAPPING_ONLY | HeldRoleType={pendingRole} | CanOverride=False | HostDataIsDead={hostDataIsDead} | PlayerStateIsDead={playerStateIsDead} | AntiBlackoutIsCached={AntiBlackout.IsCached} | PacketBytes={packetBytes} | SubmessageCount={submessageCount} | DataSubmessageIndex={dataSubmessageIndex} | GhostSubmessageIndex={ghostSubmessageIndex} | ElapsedMs={ElapsedMilliseconds():F3}",
                    Marker);
            }

            private static void ClearPending()
            {
                pendingMeetingGeneration = 0;
                pendingPlayerId = byte.MaxValue;
                pendingClientId = -1;
                pendingRole = default;
                removedRoleView = default;
                removedRestoreSnapshot = default;
                pendingStartTicks = 0;
                provisionalFirstHold = false;
                firstSelfGhostHeld = false;
                restoreMetadataRemoved = false;
                restoreCallObserved = false;
                secondSelfGhostSuppressed = false;
            }

            private static void RestoreHeldMetadata(bool restoreSnapshot)
            {
                if (!restoreMetadataRemoved)
                    return;

                var key = (pendingPlayerId, pendingPlayerId);
                var roleViewMap = GetRoleViewMap();
                if (roleViewMap != null)
                    roleViewMap[key] = removedRoleView;

                if (restoreSnapshot)
                {
                    var restoreMap = GetRestoreSnapshot();
                    if (restoreMap != null)
                        restoreMap[key] = removedRestoreSnapshot;
                }

                restoreMetadataRemoved = false;
            }

            internal static void ResetForMeeting(uint generation)
            {
                RestoreHeldMetadata(restoreSnapshot: true);
                ClearPending();
                meetingGeneration = generation;
            }

            internal static void ResetForLifecycle(string reason)
            {
                if (firstSelfGhostHeld || restoreMetadataRemoved)
                {
                    Logger.Info($"Stage=QUEUE_CLEARED | Reason={reason}", Marker);
                }
                RestoreHeldMetadata(restoreSnapshot: true);
                ClearPending();
            }

            internal static void ResetForDisconnectedPlayer(byte playerId)
            {
                if ((firstSelfGhostHeld || provisionalFirstHold) &&
                    pendingPlayerId == playerId)
                {
                    ResetForLifecycle("TARGET_DISCONNECTED");
                }
            }

            internal static bool TryPrepareFirstSelfGhostHold(
                PlayerControl target,
                PlayerControl seer,
                RoleTypes role)
            {
                if (!AmongUsClient.Instance.AmHost ||
                    !AntiBlackout.OverrideExiledPlayer ||
                    target == null ||
                    seer == null ||
                    target.Data == null ||
                    target.Data.Disconnected ||
                    target.PlayerId != seer.PlayerId ||
                    seer.AmOwner ||
                    seer.IsModClient() ||
                    !IsSupportedGhost(role) ||
                    firstSelfGhostHeld ||
                    provisionalFirstHold ||
                    !IsSafetyApplied() ||
                    !MeetingVoteManager.H4TryGetActualExileLedger(out var ledger) ||
                    ledger.MeetingGeneration != meetingGeneration ||
                    ledger.ActualExiledPlayerId != target.PlayerId ||
                    ledger.TargetClientId != target.GetClientId() ||
                    !ledger.RealVotingCompleteDelivered ||
                    ledger.IsTie ||
                    ledger.Path != RequiredLedgerPath)
                {
                    return false;
                }

                var key = (target.PlayerId, target.PlayerId);
                var roleViewMap = GetRoleViewMap();
                var restoreMap = GetRestoreSnapshot();
                if (roleViewMap == null ||
                    restoreMap == null ||
                    !roleViewMap.TryGetValue(key, out removedRoleView) ||
                    !restoreMap.TryGetValue(key, out removedRestoreSnapshot))
                {
                    return false;
                }

                pendingMeetingGeneration = ledger.MeetingGeneration;
                pendingPlayerId = target.PlayerId;
                pendingClientId = target.GetClientId();
                pendingRole = role;
                pendingStartTicks = DateTime.UtcNow.Ticks;
                provisionalFirstHold = true;
                return true;
            }

            internal static bool TryCommitFirstSelfGhostHold()
            {
                if (!provisionalFirstHold)
                    return false;

                var key = (pendingPlayerId, pendingPlayerId);
                var roleViewMap = GetRoleViewMap();
                var restoreMap = GetRestoreSnapshot();
                if (roleViewMap == null ||
                    restoreMap == null ||
                    !IsSafetyApplied() ||
                    !roleViewMap.TryGetValue(key, out var currentRole) ||
                    currentRole != pendingRole ||
                    !restoreMap.ContainsKey(key))
                {
                    ClearPending();
                    return false;
                }

                try
                {
                    if (!roleViewMap.Remove(key))
                    {
                        ClearPending();
                        return false;
                    }

                    if (!restoreMap.Remove(key))
                    {
                        roleViewMap[key] = currentRole;
                        ClearPending();
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    roleViewMap[key] = currentRole;
                    restoreMap[key] = removedRestoreSnapshot;
                    Logger.Exception(ex, Marker);
                    ClearPending();
                    return false;
                }

                removedRoleView = currentRole;
                provisionalFirstHold = false;
                firstSelfGhostHeld = true;
                restoreMetadataRemoved = true;

                var playerStateIsDead = PlayerState.GetByPlayerId(pendingPlayerId).IsDead;
                LogStage(
                    "FIRST_SELF_GHOST_HELD",
                    hostDataIsDead: Utils.GetPlayerById(pendingPlayerId)?.Data?.IsDead == true,
                    playerStateIsDead: playerStateIsDead);
                VanillaActualExileCriticalLeaseAbV14A.TryApplyAfterSetIsDeadData();
                return true;
            }

            internal static bool BeginRestoreCall()
            {
                restoreCallObserved = firstSelfGhostHeld && restoreMetadataRemoved;
                return restoreCallObserved;
            }

            internal static void FinishRestoreCall(bool wasArmed, bool completedNormally)
            {
                if (!wasArmed)
                    return;

                RestoreHeldMetadata(restoreSnapshot: !completedNormally);
                if (!completedNormally)
                {
                    ClearPending();
                    return;
                }

                secondSelfGhostSuppressed = true;
                var playerStateIsDead = PlayerState.GetByPlayerId(pendingPlayerId).IsDead;
                LogStage(
                    "SECOND_SELF_GHOST_SUPPRESSED",
                    hostDataIsDead: Utils.GetPlayerById(pendingPlayerId)?.Data?.IsDead == true,
                    playerStateIsDead: playerStateIsDead);
            }
            private static bool TrySendAtomicPacketCore(
                NetworkedPlayerInfo? exiled,
                bool preservePendingOnFailure,
                string stagePrefix)
            {
                if (!firstSelfGhostHeld || !secondSelfGhostSuppressed)
                    return false;

                var target = exiled?.Object;
                var playerStateIsDead = exiled != null &&
                    PlayerState.GetByPlayerId(exiled.PlayerId).IsDead;

                if (!AmongUsClient.Instance.AmHost ||
                    exiled == null ||
                    target == null ||
                    exiled.PlayerId != pendingPlayerId ||
                    target.GetClientId() != pendingClientId ||
                    target.AmOwner ||
                    target.IsModClient() ||
                    exiled.Disconnected ||
                    !exiled.IsDead ||
                    !playerStateIsDead ||
                    AntiBlackout.IsCached ||
                    !IsSupportedGhost(pendingRole) ||
                    !MeetingVoteManager.H4TryGetActualExileLedger(out var ledger) ||
                    ledger.MeetingGeneration != pendingMeetingGeneration ||
                    ledger.ActualExiledPlayerId != pendingPlayerId ||
                    ledger.TargetClientId != pendingClientId ||
                    !ledger.RealVotingCompleteDelivered ||
                    ledger.IsTie ||
                    ledger.Path != RequiredLedgerPath)
                {
                    LogStage(
                        stagePrefix + "_PREFLIGHT_NOT_READY",
                        hostDataIsDead: exiled?.IsDead == true,
                        playerStateIsDead: playerStateIsDead);

                    if (!preservePendingOnFailure)
                        ClearPending();

                    return false;
                }

                CustomRpcSender? sender = null;

                try
                {
                    LogStage(
                        stagePrefix + "_PACKET_BUILD",
                        hostDataIsDead: true,
                        playerStateIsDead: true);

                    sender = CustomRpcSender.Create(
                        Marker,
                        SendOption.Reliable);

                    sender.StartMessage(pendingClientId);

                    sender.stream.StartMessage(1);
                    sender.stream.WritePacked(exiled.NetId);
                    exiled.Serialize(sender.stream, true);
                    sender.stream.EndMessage();

                    LogStage(
                        stagePrefix + "_DATA_WRITTEN",
                        hostDataIsDead: true,
                        playerStateIsDead: true,
                        packetBytes: sender.stream.Length,
                        submessageCount: 1,
                        dataSubmessageIndex: 0);

                    sender.StartRpc(
                            target.NetId,
                            RpcCalls.SetRole)
                        .Write((ushort)pendingRole)
                        .Write(false)
                        .EndRpc();

                    LogStage(
                        stagePrefix + "_GHOST_WRITTEN",
                        hostDataIsDead: true,
                        playerStateIsDead: true,
                        packetBytes: sender.stream.Length,
                        submessageCount: 2,
                        dataSubmessageIndex: 0,
                        ghostSubmessageIndex: 1);

                    sender.EndMessage();

                    var packetBytes =
                        sender.stream.Length;

                    if (packetBytes > MaxAtomicPacketBytes)
                    {
                        Logger.Warn(
                            $"Atomic packet canceled by size gate | PacketBytes={packetBytes} | Max={MaxAtomicPacketBytes}",
                            Marker);

                        sender.stream.Recycle();

                        if (!preservePendingOnFailure)
                            ClearPending();

                        return false;
                    }

                    sender.SendMessage();

                    LogStage(
                        stagePrefix + "_PACKET_SENT",
                        hostDataIsDead: true,
                        playerStateIsDead: true,
                        packetBytes: packetBytes,
                        submessageCount: 2,
                        dataSubmessageIndex: 0,
                        ghostSubmessageIndex: 1);

                    ClearPending();
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Exception(
                        ex,
                        Marker);

                    if (sender?.CurrentState is not CustomRpcSender.State.Finished)
                        sender?.stream.Recycle();

                    if (!preservePendingOnFailure)
                        ClearPending();

                    return false;
                }
            }

            // SYNC_SELF_GHOST_FAIL_OPEN_AB_V1:
            // Remote Vanilla actual exile self only.
            // Successful synchronous Data->Ghost owns the self transition.
            // Any failure returns false without suppressing the existing V27 path.
            internal static bool CompleteWrapUpSynchronous(
                NetworkedPlayerInfo? exiled)
            {
                if (!firstSelfGhostHeld)
                    return false;

                if (!restoreCallObserved ||
                    !secondSelfGhostSuppressed ||
                    exiled == null ||
                    exiled.PlayerId != pendingPlayerId)
                {
                    RestoreHeldMetadata(
                        restoreSnapshot: true);
                    ClearPending();
                    return false;
                }

                return TrySendAtomicPacketCore(
                    exiled,
                    preservePendingOnFailure: true,
                    stagePrefix: "SYNC_ATOMIC");
            }
            internal static void TrySendAtomicPacket(
                NetworkedPlayerInfo? exiled)
            {
                _ = TrySendAtomicPacketCore(
                    exiled,
                    preservePendingOnFailure: false,
                    stagePrefix: "ATOMIC");
            }
        }

        // VANILLA_ACTUAL_EXILE_CRITICAL_LEASE_AB_V14A:
        // 4-15P PREPARED_REAL_TARGETED + MIXED_GHOST_MAPPING_ONLY diagnostic A/B.
        // SetIsDead GameData直後から既存+2s finalizerまで、remote Vanilla viewerごとに
        // alive dummy 1名のImpostor viewだけをleaseする。host role/player stateは変更しない。
        internal static class VanillaActualExileCriticalLeaseAbV14A
        {
            private const string Marker = "V14A_CRITICAL_LEASE";
            private const string RequiredLedgerPath = "PREPARED_REAL_TARGETED";
            private const string MappingMode = "MIXED_GHOST_MAPPING_ONLY";

            private static readonly System.Reflection.FieldInfo? VanillaIntroRoleMapField =
                AccessTools.Field(typeof(SelectRolesPatch), "VanillaIntroRoleMap");
            private static readonly System.Reflection.FieldInfo? RestoreSnapshotField =
                AccessTools.Field(typeof(AntiBlackout), "ExileDisplayRestoreSnapshotPermanent");
            private static readonly System.Reflection.FieldInfo? SafeTargetIdsField =
                AccessTools.Field(typeof(AntiBlackout), "ExileDisplaySafeTargetIdsPermanent");
            private static readonly System.Reflection.FieldInfo? SafetyAppliedField =
                AccessTools.Field(typeof(AntiBlackout), "ExileDisplaySafetyAppliedPermanent");
            // HOST_EXILE_PRE_VOTING_DUMMY_AB_V4:
            // real VotingComplete送信前のPrepared段階を判定するためPendingも参照する。
            private static readonly System.Reflection.FieldInfo? SafetyPendingField =
                AccessTools.Field(typeof(AntiBlackout), "ExileDisplaySafetyPendingPermanent");
            private static readonly System.Reflection.FieldInfo? SafetyDummyIdField =
                AccessTools.Field(typeof(AntiBlackout), "ExileDisplayDummyImpostorIdPermanent");

            private static readonly Dictionary<byte, CriticalLease> Leases = new();

            [ThreadStatic]
            private static IntPtr capturedWriterPointer;
            [ThreadStatic]
            private static List<CapturedUInt16>? capturedUInt16Writes;

            private sealed class CriticalLease
            {
                internal CriticalLease(
                    uint meetingGeneration,
                    byte viewerId,
                    int viewerClientId,
                    byte dummyId,
                    RoleTypes dummyActualRole,
                    byte exiledId)
                {
                    MeetingGeneration = meetingGeneration;
                    ViewerId = viewerId;
                    ViewerClientId = viewerClientId;
                    DummyId = dummyId;
                    DummyActualRole = dummyActualRole;
                    ExiledId = exiledId;
                }

                internal uint MeetingGeneration { get; }
                internal byte ViewerId { get; }
                internal int ViewerClientId { get; }
                internal byte DummyId { get; }
                internal RoleTypes DummyActualRole { get; set; }
                internal byte ExiledId { get; }
                internal bool RestoreHeldLogged { get; set; }
            }

            private readonly struct CapturedUInt16
            {
                internal CapturedUInt16(int position)
                {
                    Position = position;
                }

                internal int Position { get; }
            }

            private sealed class PreparedPacket
            {
                internal PreparedPacket(CriticalLease lease, CustomRpcSender sender)
                {
                    Lease = lease;
                    Sender = sender;
                }

                internal CriticalLease Lease { get; }
                internal CustomRpcSender Sender { get; }
            }

            private static Dictionary<(byte SeerId, byte TargetId), RoleTypes>? GetRoleViewMap()
            {
                return VanillaIntroRoleMapField?.GetValue(null)
                    as Dictionary<(byte SeerId, byte TargetId), RoleTypes>;
            }

            private static Dictionary<(byte SeerId, byte TargetId), RoleTypes>? GetRestoreSnapshot()
            {
                return RestoreSnapshotField?.GetValue(null)
                    as Dictionary<(byte SeerId, byte TargetId), RoleTypes>;
            }

            private static List<byte>? GetSafeTargetIds()
            {
                return SafeTargetIdsField?.GetValue(null) as List<byte>;
            }

            private static bool IsSafetyApplied()
            {
                return SafetyAppliedField?.GetValue(null) is bool applied && applied;
            }

            private static bool IsSafetyPending()
            {
                return SafetyPendingField?.GetValue(null) is bool pending && pending;
            }
            private static byte GetSafetyDummyId()
            {
                return SafetyDummyIdField?.GetValue(null) is byte dummyId
                    ? dummyId
                    : byte.MaxValue;
            }

            private static void LogStage(string stage, CriticalLease lease)
            {
                var viewer = Utils.GetPlayerById(lease.ViewerId);
                var dummy = Utils.GetPlayerById(lease.DummyId);
                Logger.Info(
                    $"Stage={stage} | MeetingGeneration={lease.MeetingGeneration} | ViewerId={lease.ViewerId} | ViewerClientId={lease.ViewerClientId} | DummyId={lease.DummyId} | DummyIsDead={dummy?.Data?.IsDead == true} | DummyConnected={dummy?.Data != null && !dummy.Data.Disconnected} | DummyActualRole={lease.DummyActualRole} | ViewerIsModded={viewer?.IsModClient() == true} | ExiledId={lease.ExiledId} | MappingMode={MappingMode}",
                    Marker);
            }

            private static void ClearLease(CriticalLease lease)
            {
                Leases.Remove(lease.ViewerId);
                LogStage("LEASE_CLEARED", lease);
            }

            private static void ClearAllLeases()
            {
                if (Leases.Count == 0)
                    return;

                var active = new List<CriticalLease>(Leases.Values);
                foreach (var lease in active)
                    ClearLease(lease);
            }

            internal static void ResetForLifecycle()
            {
                ClearAllLeases();
                EndUInt16Capture();
            }

            internal static void ResetForDisconnectedPlayer(byte playerId)
            {
                if (Leases.Count == 0)
                    return;

                var active = new List<CriticalLease>(Leases.Values);
                foreach (var lease in active)
                {
                    if (lease.ViewerId == playerId || lease.DummyId == playerId)
                        ClearLease(lease);
                }
            }

            private static bool TryGetExactMixedMappingState(
                out MeetingVoteManager.H4ActualExileLedger ledger,
                out List<PlayerControl> connected,
                out Dictionary<(byte SeerId, byte TargetId), RoleTypes> restoreSnapshot)
            {
                connected = new List<PlayerControl>();
                restoreSnapshot = null!;
                var hasCurrentLedger = MeetingVoteManager.H4TryGetActualExileLedger(out ledger);
                if (!GameStates.IsOnlineGame ||
                    !GameStates.IsInGame ||
                    !AmongUsClient.Instance.AmHost ||
                    !AntiBlackout.OverrideExiledPlayer ||
                    !IsSafetyApplied() ||
                    !hasCurrentLedger ||
                    ledger.Path != RequiredLedgerPath ||
                    ledger.IsTie ||
                    !ledger.RealVotingCompleteDelivered ||
                    ledger.ActualExiledPlayerId == byte.MaxValue ||
                    ledger.TargetClientId < 0)
                {
                    return false;
                }

                foreach (var player in Main.AllPlayerControls)
                {
                    if (player?.Data == null || player.Data.Disconnected)
                        continue;

                    connected.Add(player);
                }

                connected.Sort((left, right) => left.PlayerId.CompareTo(right.PlayerId));
                if (connected.Count < 4 || connected.Count > 15)
                    return false;

                var exiled = Utils.GetPlayerById(ledger.ActualExiledPlayerId);
                if (exiled?.Data == null ||
                    exiled.Data.Disconnected ||
                    exiled.AmOwner ||
                    exiled.IsModClient() ||
                    exiled.GetClientId() != ledger.TargetClientId)
                {
                    return false;
                }

                var safeTargetIds = GetSafeTargetIds();
                var roleViewMap = GetRoleViewMap();
                restoreSnapshot = GetRestoreSnapshot()!;
                if (safeTargetIds == null ||
                    roleViewMap == null ||
                    restoreSnapshot == null ||
                    safeTargetIds.Count != connected.Count)
                {
                    return false;
                }

                var connectedIds = new HashSet<byte>();
                foreach (var player in connected)
                    connectedIds.Add(player.PlayerId);

                if (connectedIds.Count != connected.Count)
                    return false;

                foreach (var targetId in safeTargetIds)
                {
                    if (!connectedIds.Remove(targetId))
                        return false;
                }

                if (connectedIds.Count != 0)
                    return false;

                var missingKey = (ledger.ActualExiledPlayerId, ledger.ActualExiledPlayerId);
                var remoteViewerCount = 0;
                foreach (var viewer in connected)
                {
                    if (viewer.AmOwner)
                        continue;

                    remoteViewerCount++;
                    foreach (var target in connected)
                    {
                        var key = (viewer.PlayerId, target.PlayerId);
                        if (key == missingKey)
                        {
                            if (roleViewMap.ContainsKey(key) || restoreSnapshot.ContainsKey(key))
                                return false;
                        }
                        else if (!roleViewMap.ContainsKey(key) || !restoreSnapshot.ContainsKey(key))
                        {
                            return false;
                        }
                    }
                }

                return remoteViewerCount > 0 &&
                    restoreSnapshot.Count == (remoteViewerCount * connected.Count) - 1;
            }

            private static PlayerControl? SelectAliveDummy(
                PlayerControl viewer,
                byte exiledId,
                byte safetyDummyId,
                List<PlayerControl> connected)
            {
                bool IsEligible(PlayerControl player)
                {
                    return player.Data != null &&
                        !player.Data.Disconnected &&
                        player.PlayerId != viewer.PlayerId &&
                        player.PlayerId != exiledId &&
                        player.IsAlive();
                }

                var safetyDummy = Utils.GetPlayerById(safetyDummyId);
                if (safetyDummy != null && IsEligible(safetyDummy))
                    return safetyDummy;

                var host = PlayerControl.LocalPlayer;
                if (host != null && IsEligible(host))
                    return host;

                foreach (var candidate in connected)
                {
                    if (IsEligible(candidate))
                        return candidate;
                }

                return null;
            }

            private static void BeginUInt16Capture(MessageWriter writer)
            {
                capturedWriterPointer = writer.Pointer;
                capturedUInt16Writes = new List<CapturedUInt16>();
            }

            private static List<CapturedUInt16> EndUInt16Capture()
            {
                var result = capturedUInt16Writes ?? new List<CapturedUInt16>();
                capturedUInt16Writes = null;
                capturedWriterPointer = IntPtr.Zero;
                return result;
            }

            internal static void CaptureUInt16Write(MessageWriter writer, ushort value)
            {
                if (capturedUInt16Writes == null ||
                    capturedWriterPointer == IntPtr.Zero ||
                    writer.Pointer != capturedWriterPointer)
                {
                    return;
                }

                capturedUInt16Writes.Add(new CapturedUInt16(writer.Position));
            }

            private static bool TryBuildPreparedPacket(
                CriticalLease lease,
                PlayerControl dummy,
                out PreparedPacket? preparedPacket)
            {
                preparedPacket = null;
                CustomRpcSender? sender = null;
                try
                {
                    // V14A_CLIENTREADY_CANDIDATE_SETROLE_PERMANENT
                    // ClientReady Barrier candidateはviewer上でroleAssigned=falseを維持している。
                    // canOverride=trueのtargeted SetRoleで一時Impostor化し、
                    // 実追放されたviewer自身が死亡してもnative game-over guardを1名残す。
                    sender = CustomRpcSender.Create(Marker, SendOption.Reliable);
                    sender.StartMessage(lease.ViewerClientId);
                    sender.StartRpc(dummy.NetId, RpcCalls.SetRole)
                        .Write((ushort)RoleTypes.Impostor)
                        .Write(true)
                        .EndRpc();
                    sender.EndMessage();

                    preparedPacket = new PreparedPacket(lease, sender);
                    Logger.Info(
                        $"Stage=CLIENTREADY_CANDIDATE_SETROLE_PREPARED | ViewerId={lease.ViewerId} | ViewerClientId={lease.ViewerClientId} | CandidateId={lease.DummyId} | ExiledId={lease.ExiledId} | CanOverride=True",
                        Marker);
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex, Marker);
                    if (sender?.CurrentState is not CustomRpcSender.State.Finished)
                        sender?.stream.Recycle();
                    return false;
                }
            }
            // HOST_EXILE_PRE_VOTING_DUMMY_AB_V4:
            // MODホストが、そのVanilla viewerから見た最後の生存Impostorである場合だけ、
            // real VotingCompleteと同じtargeted GameDataToの先頭に
            // 既存Safety Dummy=Impostorを積むための候補を返す。
            // これにより「最後のImpostor追放」をVanillaが先に確定する時間窓を閉じる。
            internal static bool TryGetHostExilePreVotingSafetyDummyAbV4(
                PlayerControl exiled,
                PlayerControl viewer,
                out PlayerControl? dummy)
            {
                const string preVotingMarker = "HOST_EXILE_PRE_VOTING_DUMMY_AB_V4";
                dummy = null;

                if (false /* HOST_EXILE_PRE_VOTING_DUMMY_LOCAL_TEST_GATE_AB_V10 */ ||
                    !GameStates.IsInGame ||
                    !AmongUsClient.Instance.AmHost ||
                    !AntiBlackout.OverrideExiledPlayer ||
                    !IsSafetyPending() ||
                    IsSafetyApplied() ||
                    exiled == null ||
                    exiled.Data == null ||
                    exiled.Data.Disconnected ||
                    !exiled.AmOwner ||
                    viewer == null ||
                    viewer.Data == null ||
                    viewer.Data.Disconnected ||
                    viewer.AmOwner ||
                    viewer.IsModClient() ||
                    !MeetingVoteManager.H4TryGetActualExileLedger(out var ledger) ||
                    ledger.Path != RequiredLedgerPath ||
                    ledger.IsTie ||
                    ledger.RealVotingCompleteDelivered ||
                    ledger.ActualExiledPlayerId != exiled.PlayerId ||
                    ledger.TargetClientId != exiled.GetClientId())
                {
                    return false;
                }

                var connected = new List<PlayerControl>();
                foreach (var player in Main.AllPlayerControls)
                {
                    if (player?.Data == null || player.Data.Disconnected)
                        continue;

                    connected.Add(player);
                }

                connected.Sort((left, right) => left.PlayerId.CompareTo(right.PlayerId));
                if (connected.Count < 4 || connected.Count > 15)
                    return false;

                var safeTargetIds = GetSafeTargetIds();
                var restoreSnapshot = GetRestoreSnapshot();
                var safetyDummyId = GetSafetyDummyId();

                if (safeTargetIds == null ||
                    restoreSnapshot == null ||
                    safetyDummyId == byte.MaxValue ||
                    safeTargetIds.Count != connected.Count)
                {
                    Logger.Info(
                        $"Stage=PRE_VOTING_ABORT | Reason=SAFETY_STATE_INCOMPLETE | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | Connected={connected.Count} | SafeTargets={safeTargetIds?.Count ?? -1} | RestoreSnapshot={restoreSnapshot?.Count ?? -1} | SafetyDummy={safetyDummyId} | ExiledId={exiled.PlayerId}",
                        preVotingMarker);
                    return false;
                }

                var remainingAliveImpostorCount = 0;
                var remainingAliveNonImpostorCount = 0;

                foreach (var remainingTarget in connected)
                {
                    if (remainingTarget.Data == null ||
                        remainingTarget.Data.Disconnected ||
                        remainingTarget.PlayerId == exiled.PlayerId ||
                        PlayerState.GetByPlayerId(remainingTarget.PlayerId).IsDead)
                    {
                        continue;
                    }

                    if (!restoreSnapshot.TryGetValue(
                            (viewer.PlayerId, remainingTarget.PlayerId),
                            out var remainingRole))
                    {
                        Logger.Info(
                            $"Stage=PRE_VOTING_ABORT | Reason=ROLEVIEW_INCOMPLETE | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | TargetId={remainingTarget.PlayerId} | ExiledId={exiled.PlayerId}",
                            preVotingMarker);
                        return false;
                    }

                    var remainingRoleName = remainingRole.ToString();
                    var remainingIsImpostor =
                        remainingRole == RoleTypes.Impostor ||
                        remainingRole == RoleTypes.Shapeshifter ||
                        remainingRoleName == "Phantom" ||
                        remainingRoleName == "Viper";

                    if (remainingIsImpostor)
                        remainingAliveImpostorCount++;
                    else
                        remainingAliveNonImpostorCount++;
                }

                if (remainingAliveImpostorCount > 0)
                {
                    Logger.Info(
                        $"Stage=PRE_VOTING_NOT_REQUIRED | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | RemainingAliveImpostors={remainingAliveImpostorCount} | RemainingAliveNonImpostors={remainingAliveNonImpostorCount} | ExiledId={exiled.PlayerId}",
                        preVotingMarker);
                    return false;
                }

                if (remainingAliveNonImpostorCount <= 2)
                {
                    Logger.Info(
                        $"Stage=PRE_VOTING_ABORT | Reason=PARITY_RISK | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | RemainingAliveImpostors=0 | RemainingAliveNonImpostors={remainingAliveNonImpostorCount} | ExiledId={exiled.PlayerId}",
                        preVotingMarker);
                    return false;
                }

                var candidate = Utils.GetPlayerById(safetyDummyId);
                if (candidate?.Data == null ||
                    candidate.Data.Disconnected ||
                    candidate.PlayerId == exiled.PlayerId ||
                    candidate.PlayerId == viewer.PlayerId ||
                    !candidate.IsAlive())
                {
                    Logger.Info(
                        $"Stage=PRE_VOTING_ABORT | Reason=SAFETY_DUMMY_INELIGIBLE | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | SafetyDummy={safetyDummyId} | ExiledId={exiled.PlayerId}",
                        preVotingMarker);
                    return false;
                }

                if (!restoreSnapshot.TryGetValue(
                        (viewer.PlayerId, candidate.PlayerId),
                        out var candidateActualRole))
                {
                    Logger.Info(
                        $"Stage=PRE_VOTING_ABORT | Reason=DUMMY_RESTORE_MISSING | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | SafetyDummy={candidate.PlayerId} | ExiledId={exiled.PlayerId}",
                        preVotingMarker);
                    return false;
                }

                dummy = candidate;

                Logger.Info(
                    $"Stage=PRE_VOTING_DUMMY_SELECTED | MeetingGeneration={ledger.MeetingGeneration} | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | DummyId={candidate.PlayerId} | DummyActualRole={candidateActualRole} | ExiledId={exiled.PlayerId}",
                    preVotingMarker);

                return true;
            }

            // REMOTE_MOD_EXILE_PRE_VOTING_DUMMY_AB_V5:
            // Remote MOD実追放でも、remote Vanilla viewer視点で最後のImpostorが消える場合、
            // real VotingCompleteと同じtargeted messageの先頭へSafety Dummy=Impostorを積む。
            // Host v4は変更せず、このmethodは「非ownerかつMOD target」だけを扱う。
            internal static bool TryGetRemoteModExilePreVotingSafetyDummyAbV5(
                PlayerControl exiled,
                PlayerControl viewer,
                out PlayerControl? dummy)
            {
                const string preVotingMarker = "REMOTE_MOD_EXILE_PRE_VOTING_DUMMY_AB_V5";
                dummy = null;

                if (!GameStates.IsOnlineGame ||
                    !GameStates.IsInGame ||
                    !AmongUsClient.Instance.AmHost ||
                    !AntiBlackout.OverrideExiledPlayer ||
                    !IsSafetyPending() ||
                    IsSafetyApplied() ||
                    exiled == null ||
                    exiled.Data == null ||
                    exiled.Data.Disconnected ||
                    exiled.AmOwner ||
                    !exiled.IsModClient() ||
                    viewer == null ||
                    viewer.Data == null ||
                    viewer.Data.Disconnected ||
                    viewer.AmOwner ||
                    viewer.IsModClient() ||
                    !MeetingVoteManager.H4TryGetActualExileLedger(out var ledger) ||
                    ledger.Path != RequiredLedgerPath ||
                    ledger.IsTie ||
                    // Remote MOD本人へ先にVotingComplete済みでも、
                    // 後続Vanilla viewer送信前の保護は必要なのでDeliveredは除外条件にしない。
                    ledger.ActualExiledPlayerId != exiled.PlayerId ||
                    ledger.TargetClientId != exiled.GetClientId())
                {
                    return false;
                }

                var connected = new List<PlayerControl>();
                foreach (var player in Main.AllPlayerControls)
                {
                    if (player?.Data == null || player.Data.Disconnected)
                        continue;

                    connected.Add(player);
                }

                connected.Sort((left, right) => left.PlayerId.CompareTo(right.PlayerId));
                if (connected.Count < 4 || connected.Count > 15)
                    return false;

                var safeTargetIds = GetSafeTargetIds();
                var restoreSnapshot = GetRestoreSnapshot();
                var safetyDummyId = GetSafetyDummyId();

                if (safeTargetIds == null ||
                    restoreSnapshot == null ||
                    safetyDummyId == byte.MaxValue ||
                    safeTargetIds.Count != connected.Count)
                {
                    Logger.Info(
                        $"Stage=PRE_VOTING_ABORT | Reason=SAFETY_STATE_INCOMPLETE | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | Connected={connected.Count} | SafeTargets={safeTargetIds?.Count ?? -1} | RestoreSnapshot={restoreSnapshot?.Count ?? -1} | SafetyDummy={safetyDummyId} | ExiledId={exiled.PlayerId}",
                        preVotingMarker);
                    return false;
                }

                var remainingAliveImpostorCount = 0;
                var remainingAliveNonImpostorCount = 0;

                foreach (var remainingTarget in connected)
                {
                    if (remainingTarget.Data == null ||
                        remainingTarget.Data.Disconnected ||
                        remainingTarget.PlayerId == exiled.PlayerId ||
                        PlayerState.GetByPlayerId(remainingTarget.PlayerId).IsDead)
                    {
                        continue;
                    }

                    if (!restoreSnapshot.TryGetValue(
                            (viewer.PlayerId, remainingTarget.PlayerId),
                            out var remainingRole))
                    {
                        Logger.Info(
                            $"Stage=PRE_VOTING_ABORT | Reason=ROLEVIEW_INCOMPLETE | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | TargetId={remainingTarget.PlayerId} | ExiledId={exiled.PlayerId}",
                            preVotingMarker);
                        return false;
                    }

                    var remainingRoleName = remainingRole.ToString();
                    var remainingIsImpostor =
                        remainingRole == RoleTypes.Impostor ||
                        remainingRole == RoleTypes.Shapeshifter ||
                        remainingRoleName == "Phantom" ||
                        remainingRoleName == "Viper";

                    if (remainingIsImpostor)
                        remainingAliveImpostorCount++;
                    else
                        remainingAliveNonImpostorCount++;
                }

                if (remainingAliveImpostorCount > 0)
                {
                    Logger.Info(
                        $"Stage=PRE_VOTING_NOT_REQUIRED | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | RemainingAliveImpostors={remainingAliveImpostorCount} | RemainingAliveNonImpostors={remainingAliveNonImpostorCount} | ExiledId={exiled.PlayerId}",
                        preVotingMarker);
                    return false;
                }

                if (remainingAliveNonImpostorCount <= 2)
                {
                    Logger.Info(
                        $"Stage=PRE_VOTING_ABORT | Reason=PARITY_RISK | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | RemainingAliveImpostors=0 | RemainingAliveNonImpostors={remainingAliveNonImpostorCount} | ExiledId={exiled.PlayerId}",
                        preVotingMarker);
                    return false;
                }

                var candidate = Utils.GetPlayerById(safetyDummyId);
                if (candidate?.Data == null ||
                    candidate.Data.Disconnected ||
                    candidate.PlayerId == exiled.PlayerId ||
                    candidate.PlayerId == viewer.PlayerId ||
                    !candidate.IsAlive())
                {
                    Logger.Info(
                        $"Stage=PRE_VOTING_ABORT | Reason=SAFETY_DUMMY_INELIGIBLE | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | SafetyDummy={safetyDummyId} | ExiledId={exiled.PlayerId}",
                        preVotingMarker);
                    return false;
                }

                if (!restoreSnapshot.TryGetValue(
                        (viewer.PlayerId, candidate.PlayerId),
                        out var candidateActualRole))
                {
                    Logger.Info(
                        $"Stage=PRE_VOTING_ABORT | Reason=DUMMY_RESTORE_MISSING | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | SafetyDummy={candidate.PlayerId} | ExiledId={exiled.PlayerId}",
                        preVotingMarker);
                    return false;
                }

                dummy = candidate;

                Logger.Info(
                    $"Stage=PRE_VOTING_DUMMY_SELECTED | MeetingGeneration={ledger.MeetingGeneration} | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | DummyId={candidate.PlayerId} | DummyActualRole={candidateActualRole} | ExiledId={exiled.PlayerId}",
                    preVotingMarker);

                return true;
            }
            // MOD_EXILE_REMOTE_VANILLA_ATOMIC_POSTSTATE_AB_V6:
            // Hostまたはremote MOD導入者の実追放で、v4/v5 pre-voting safetyが
            // 実際に必要だったremote Vanilla viewerだけを記録する。
            // WrapUp finalizerでは、既存actual GameData / lease restore後に
            // exiled dead GameData -> CrewmateGhostを同一targeted messageで再提示する。
            private sealed class ModExileAtomicPoststatePlanAbV6
            {
                internal uint MeetingGeneration;
                internal byte ExiledId;
                internal byte ViewerId;
                internal int ViewerClientId;
                internal bool ExiledWasHost;
                internal bool ExiledWasRemoteMod;
            }

            private static readonly Dictionary<byte, ModExileAtomicPoststatePlanAbV6>
                ModExileAtomicPoststatePlansAbV6 = new();

            private const int ModExileAtomicPoststateMaxPacketBytesAbV6 = 1000;

            private static void RecordModExileAtomicPoststatePlanAbV6(
                PlayerControl exiled,
                PlayerControl viewer)
            {
                if (exiled == null ||
                    exiled.Data == null ||
                    exiled.Data.Disconnected ||
                    viewer == null ||
                    viewer.Data == null ||
                    viewer.Data.Disconnected ||
                    viewer.AmOwner ||
                    viewer.IsModClient() ||
                    !MeetingVoteManager.H4TryGetActualExileLedger(out var ledger) ||
                    ledger.Path != RequiredLedgerPath ||
                    ledger.IsTie ||
                    ledger.ActualExiledPlayerId != exiled.PlayerId)
                {
                    return;
                }

                var exiledWasHost = exiled.AmOwner;
                var exiledWasRemoteMod = !exiled.AmOwner && exiled.IsModClient();
                if (!exiledWasHost && !exiledWasRemoteMod)
                    return;

                if (!viewer.TryGetClientId(out var viewerClientId)) return;
                var staleViewerIds = new List<byte>();
                foreach (var entry in ModExileAtomicPoststatePlansAbV6)
                {
                    if (entry.Value.MeetingGeneration != ledger.MeetingGeneration)
                        staleViewerIds.Add(entry.Key);
                }

                foreach (var staleViewerId in staleViewerIds)
                    ModExileAtomicPoststatePlansAbV6.Remove(staleViewerId);

                ModExileAtomicPoststatePlansAbV6[viewer.PlayerId] =
                    new ModExileAtomicPoststatePlanAbV6
                    {
                        MeetingGeneration = ledger.MeetingGeneration,
                        ExiledId = exiled.PlayerId,
                        ViewerId = viewer.PlayerId,
                        ViewerClientId = viewerClientId,
                        ExiledWasHost = exiledWasHost,
                        ExiledWasRemoteMod = exiledWasRemoteMod
                    };

                Logger.Info(
                    $"Stage=PLAN_RECORDED | MeetingGeneration={ledger.MeetingGeneration} | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | ExiledId={exiled.PlayerId} | ExiledWasHost={exiledWasHost} | ExiledWasRemoteMod={exiledWasRemoteMod}",
                    "MOD_EXILE_REMOTE_VANILLA_ATOMIC_POSTSTATE_AB_V6");
            }

            internal static void TrySendModExileAtomicPoststateAbV6(NetworkedPlayerInfo? exiled)
            {
                if (ModExileAtomicPoststatePlansAbV6.Count == 0)
                    return;

                try
                {
                    if (!AmongUsClient.Instance.AmHost ||
                        exiled == null ||
                        exiled.Object == null ||
                        !exiled.IsDead ||
                        !MeetingVoteManager.H4TryGetActualExileLedger(out var ledger) ||
                        ledger.Path != RequiredLedgerPath ||
                        ledger.IsTie ||
                        ledger.ActualExiledPlayerId != exiled.PlayerId)
                    {
                        Logger.Info(
                            $"Stage=ATOMIC_POSTSTATE_ABORT | Reason=FINAL_STATE_MISMATCH | Plans={ModExileAtomicPoststatePlansAbV6.Count} | ExiledId={(exiled == null ? byte.MaxValue : exiled.PlayerId)}",
                            "MOD_EXILE_REMOTE_VANILLA_ATOMIC_POSTSTATE_AB_V6");
                        return;
                    }

                    var activePlans =
                        new List<ModExileAtomicPoststatePlanAbV6>(
                            ModExileAtomicPoststatePlansAbV6.Values);
                    activePlans.Sort(
                        (left, right) => left.ViewerId.CompareTo(right.ViewerId));

                    foreach (var plan in activePlans)
                    {
                        if (plan.MeetingGeneration != ledger.MeetingGeneration ||
                            plan.ExiledId != exiled.PlayerId ||
                            (!plan.ExiledWasHost && !plan.ExiledWasRemoteMod))
                        {
                            continue;
                        }

                        var viewer = Utils.GetPlayerById(plan.ViewerId);
                        if (viewer == null ||
                            viewer.Data == null ||
                            viewer.Data.Disconnected ||
                            viewer.AmOwner ||
                            viewer.IsModClient() ||
                            viewer.GetClientId() != plan.ViewerClientId)
                        {
                            Logger.Info(
                                $"Stage=ATOMIC_POSTSTATE_ABORT | Reason=VIEWER_INVALID | MeetingGeneration={plan.MeetingGeneration} | ViewerId={plan.ViewerId} | ViewerClientId={plan.ViewerClientId} | ExiledId={plan.ExiledId}",
                                "MOD_EXILE_REMOTE_VANILLA_ATOMIC_POSTSTATE_AB_V6");
                            continue;
                        }

                        CustomRpcSender? sender = null;
                        try
                        {
                            Logger.Info(
                                $"Stage=ATOMIC_POSTSTATE_BUILD | MeetingGeneration={plan.MeetingGeneration} | ViewerId={plan.ViewerId} | ViewerClientId={plan.ViewerClientId} | ExiledId={plan.ExiledId} | ExiledWasHost={plan.ExiledWasHost} | ExiledWasRemoteMod={plan.ExiledWasRemoteMod} | HostDataIsDead={exiled.IsDead} | Ghost=CrewmateGhost",
                                "MOD_EXILE_REMOTE_VANILLA_ATOMIC_POSTSTATE_AB_V6");

                            sender = CustomRpcSender.Create(
                                "MOD_EXILE_REMOTE_VANILLA_ATOMIC_POSTSTATE_AB_V6",
                                SendOption.Reliable);
                            sender.StartMessage(plan.ViewerClientId);

                            sender.stream.StartMessage(1);
                            sender.stream.WritePacked(exiled.NetId);
                            exiled.Serialize(sender.stream, true);
                            sender.stream.EndMessage();

                            Logger.Info(
                                $"Stage=ATOMIC_POSTSTATE_DATA_WRITTEN | MeetingGeneration={plan.MeetingGeneration} | ViewerId={plan.ViewerId} | ViewerClientId={plan.ViewerClientId} | ExiledId={plan.ExiledId} | PacketBytes={sender.stream.Length}",
                                "MOD_EXILE_REMOTE_VANILLA_ATOMIC_POSTSTATE_AB_V6");

                            sender.StartRpc(exiled.Object.NetId, RpcCalls.SetRole)
                                .Write((ushort)RoleTypes.CrewmateGhost)
                                .Write(false)
                                .EndRpc();

                            Logger.Info(
                                $"Stage=ATOMIC_POSTSTATE_GHOST_WRITTEN | MeetingGeneration={plan.MeetingGeneration} | ViewerId={plan.ViewerId} | ViewerClientId={plan.ViewerClientId} | ExiledId={plan.ExiledId} | Ghost=CrewmateGhost | CanOverride=False | PacketBytes={sender.stream.Length}",
                                "MOD_EXILE_REMOTE_VANILLA_ATOMIC_POSTSTATE_AB_V6");

                            sender.EndMessage();
                            var packetBytes = sender.stream.Length;
                            if (packetBytes > ModExileAtomicPoststateMaxPacketBytesAbV6)
                            {
                                Logger.Warn(
                                    $"Stage=ATOMIC_POSTSTATE_ABORT | Reason=PACKET_TOO_LARGE | MeetingGeneration={plan.MeetingGeneration} | ViewerId={plan.ViewerId} | ViewerClientId={plan.ViewerClientId} | ExiledId={plan.ExiledId} | PacketBytes={packetBytes} | Max={ModExileAtomicPoststateMaxPacketBytesAbV6}",
                                    "MOD_EXILE_REMOTE_VANILLA_ATOMIC_POSTSTATE_AB_V6");
                                sender.stream.Recycle();
                                continue;
                            }

                            sender.SendMessage();

                            Logger.Info(
                                $"Stage=ATOMIC_POSTSTATE_SENT | MeetingGeneration={plan.MeetingGeneration} | ViewerId={plan.ViewerId} | ViewerClientId={plan.ViewerClientId} | ExiledId={plan.ExiledId} | ExiledWasHost={plan.ExiledWasHost} | ExiledWasRemoteMod={plan.ExiledWasRemoteMod} | HostDataIsDead={exiled.IsDead} | Ghost=CrewmateGhost | PacketBytes={packetBytes} | SubmessageCount=2 | DataSubmessageIndex=0 | GhostSubmessageIndex=1",
                                "MOD_EXILE_REMOTE_VANILLA_ATOMIC_POSTSTATE_AB_V6");
                        }
                        catch (Exception ex)
                        {
                            Logger.Exception(ex, "MOD_EXILE_REMOTE_VANILLA_ATOMIC_POSTSTATE_AB_V6");
                            if (sender?.CurrentState is not CustomRpcSender.State.Finished)
                                sender?.stream.Recycle();
                        }
                    }
                }
                finally
                {
                    ModExileAtomicPoststatePlansAbV6.Clear();
                }
            }

            // MOD_EXILE_PRE_VOTING_DUMMY_AB_V5:
            // Host v4をそのまま優先し、該当しない場合だけRemote MOD v5を評価する。
            // A/B v6では、どちらの経路でもpre-voting safetyが必要だったviewerを記録する。
            internal static bool TryGetModExilePreVotingSafetyDummyAbV5(
                PlayerControl exiled,
                PlayerControl viewer,
                out PlayerControl? dummy)
            {
                if (TryGetHostExilePreVotingSafetyDummyAbV4(
                        exiled,
                        viewer,
                        out dummy))
                {
                    RecordModExileAtomicPoststatePlanAbV6(exiled, viewer);
                    return true;
                }

                if (TryGetRemoteModExilePreVotingSafetyDummyAbV5(
                        exiled,
                        viewer,
                        out dummy))
                {
                    RecordModExileAtomicPoststatePlanAbV6(exiled, viewer);

                    // REMOTE_MOD_EXILE_RECORDED_POST_SETISDEAD_REASSERT_AB_V21:
                    // pre-voting時点で確定したdangerous Vanilla viewer / dummyを
                    // SetIsDead後まで保持し、CriticalLease生成を待たずに再利用する。
                    if (dummy != null)
                    {
                        RecordRemoteModPostSetIsDeadReassertPlanAbV21(
                            exiled,
                            viewer,
                            dummy);
                    }

                    return true;
                }

                dummy = null;
                return false;
            }
            // HOST_EXILE_REMOTE_VANILLA_CRITICAL_LEASE_AB_V3:
            // MODホスト自身が4-15人real-targeted実追放されたときだけ、
            // 既存ExileDisplay安全化で既にImpostorになっているsafety dummyを
            // 必要なremote Vanilla viewerだけRestoreで保持する。
            // 新規SetRoleは送らず、既存V14AのRestore hold / +2s finalizerだけを再利用する。
            internal static bool TryPrepareHostExileSafetyDummyLeaseAbV3(PlayerControl target)
            {
                const string hostMarker = "HOST_EXILE_REMOTE_VANILLA_CRITICAL_LEASE_AB_V3";
                // MOD_EXILE_REMOTE_VANILLA_PERSISTENT_IMPOSTOR_LEASE_AB_V8: remote認識済みMOD追放でもSafety leaseを取得可能にする。

                // PERSISTENT_LEASE_LOCAL_TEST_GATE_AB_V8L: ローカル再現環境でも同じA/B lease経路を検証する。
                if (
                    !GameStates.IsInGame ||
                    !AmongUsClient.Instance.AmHost ||
                    !AntiBlackout.OverrideExiledPlayer ||
                    !IsSafetyApplied() ||
                    target == null ||
                    target.Data == null ||
                    target.Data.Disconnected ||
                    (!target.AmOwner && !target.IsModClient()) ||
                    !MeetingVoteManager.H4TryGetActualExileLedger(out var ledger) ||
                    ledger.Path != RequiredLedgerPath ||
                    ledger.IsTie ||
                    (target.AmOwner && ledger.RealVotingCompleteDelivered) ||
                    ledger.ActualExiledPlayerId != target.PlayerId ||
                    ledger.TargetClientId != target.GetClientId())
                {
                    return false;
                }

                if (Leases.Count != 0)
                {
                    Logger.Info(
                        $"Stage=HOST_LEASE_ABORT | Reason=EXISTING_LEASES | Count={Leases.Count} | ExiledId={target.PlayerId}",
                        hostMarker);
                    return false;
                }

                var connected = new List<PlayerControl>();
                foreach (var player in Main.AllPlayerControls)
                {
                    if (player?.Data == null || player.Data.Disconnected)
                        continue;

                    connected.Add(player);
                }

                connected.Sort((left, right) => left.PlayerId.CompareTo(right.PlayerId));
                if (connected.Count < 4 || connected.Count > 15)
                    return false;

                var safeTargetIds = GetSafeTargetIds();
                var restoreSnapshot = GetRestoreSnapshot();
                var safetyDummyId = GetSafetyDummyId();

                if (safeTargetIds == null ||
                    restoreSnapshot == null ||
                    safetyDummyId == byte.MaxValue ||
                    safeTargetIds.Count != connected.Count)
                {
                    Logger.Info(
                        $"Stage=HOST_LEASE_ABORT | Reason=SAFETY_STATE_INCOMPLETE | Connected={connected.Count} | SafeTargets={safeTargetIds?.Count ?? -1} | RestoreSnapshot={restoreSnapshot?.Count ?? -1} | SafetyDummy={safetyDummyId}",
                        hostMarker);
                    return false;
                }

                var connectedIds = new HashSet<byte>();
                foreach (var player in connected)
                    connectedIds.Add(player.PlayerId);

                if (connectedIds.Count != connected.Count)
                    return false;

                foreach (var targetId in safeTargetIds)
                {
                    if (!connectedIds.Remove(targetId))
                        return false;
                }

                if (connectedIds.Count != 0)
                    return false;

                var remoteViewerCount = 0;
                foreach (var viewer in connected)
                {
                    if (viewer.AmOwner)
                        continue;

                    remoteViewerCount++;
                    foreach (var restoreTarget in connected)
                    {
                        if (!restoreSnapshot.ContainsKey(
                                (viewer.PlayerId, restoreTarget.PlayerId)))
                        {
                            Logger.Info(
                                $"Stage=HOST_LEASE_ABORT | Reason=RESTORE_SNAPSHOT_INCOMPLETE | ViewerId={viewer.PlayerId} | TargetId={restoreTarget.PlayerId}",
                                hostMarker);
                            return false;
                        }
                    }
                }

                if (remoteViewerCount <= 0 ||
                    restoreSnapshot.Count != remoteViewerCount * connected.Count)
                {
                    Logger.Info(
                        $"Stage=HOST_LEASE_ABORT | Reason=RESTORE_SNAPSHOT_COUNT | RemoteViewers={remoteViewerCount} | Connected={connected.Count} | RestoreSnapshot={restoreSnapshot.Count}",
                        hostMarker);
                    return false;
                }

                var safetyDummy = Utils.GetPlayerById(safetyDummyId);
                if (safetyDummy?.Data == null ||
                    safetyDummy.Data.Disconnected ||
                    safetyDummy.PlayerId == target.PlayerId ||
                    !safetyDummy.IsAlive())
                {
                    Logger.Info(
                        $"Stage=HOST_LEASE_ABORT | Reason=SAFETY_DUMMY_INELIGIBLE | SafetyDummy={safetyDummyId} | ExiledId={target.PlayerId}",
                        hostMarker);
                    return false;
                }

                var planned = new List<CriticalLease>();

                foreach (var viewer in connected)
                {
                    if (viewer.AmOwner || viewer.IsModClient())
                        continue;

                    var remainingAliveImpostorCount = 0;
                    var remainingAliveNonImpostorCount = 0;

                    foreach (var remainingTarget in connected)
                    {
                        if (remainingTarget.Data == null ||
                            remainingTarget.Data.Disconnected ||
                            remainingTarget.PlayerId == ledger.ActualExiledPlayerId ||
                            PlayerState.GetByPlayerId(remainingTarget.PlayerId).IsDead)
                        {
                            continue;
                        }

                        if (!restoreSnapshot.TryGetValue(
                                (viewer.PlayerId, remainingTarget.PlayerId),
                                out var remainingRole))
                        {
                            Logger.Info(
                                $"Stage=HOST_LEASE_ABORT | Reason=ROLEVIEW_INCOMPLETE | ViewerId={viewer.PlayerId} | TargetId={remainingTarget.PlayerId} | ExiledId={ledger.ActualExiledPlayerId}",
                                hostMarker);
                            return false;
                        }

                        var remainingRoleName = remainingRole.ToString();
                        var remainingIsImpostor =
                            remainingRole == RoleTypes.Impostor ||
                            remainingRole == RoleTypes.Shapeshifter ||
                            remainingRoleName == "Phantom" ||
                            remainingRoleName == "Viper";

                        if (remainingIsImpostor)
                            remainingAliveImpostorCount++;
                        else
                            remainingAliveNonImpostorCount++;
                    }

                    if (remainingAliveImpostorCount > 0)
                    {
                        Logger.Info(
                            $"Stage=HOST_LEASE_NOT_REQUIRED | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | RemainingAliveImpostors={remainingAliveImpostorCount} | RemainingAliveNonImpostors={remainingAliveNonImpostorCount} | ExiledId={ledger.ActualExiledPlayerId}",
                            hostMarker);
                        continue;
                    }

                    if (remainingAliveNonImpostorCount <= 2)
                    {
                        Logger.Info(
                            $"Stage=HOST_LEASE_ABORT | Reason=PARITY_RISK | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | RemainingAliveImpostors=0 | RemainingAliveNonImpostors={remainingAliveNonImpostorCount} | ExiledId={ledger.ActualExiledPlayerId}",
                            hostMarker);
                        return false;
                    }

                    if (safetyDummy.PlayerId == viewer.PlayerId)
                    {
                        Logger.Info(
                            $"Stage=HOST_LEASE_ABORT | Reason=SAFETY_DUMMY_IS_VIEWER | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | SafetyDummy={safetyDummy.PlayerId} | ExiledId={ledger.ActualExiledPlayerId}",
                            hostMarker);
                        return false;
                    }

                    if (!restoreSnapshot.TryGetValue(
                            (viewer.PlayerId, safetyDummy.PlayerId),
                            out var dummyActualRole))
                    {
                        Logger.Info(
                            $"Stage=HOST_LEASE_ABORT | Reason=DUMMY_RESTORE_MISSING | ViewerId={viewer.PlayerId} | SafetyDummy={safetyDummy.PlayerId} | ExiledId={ledger.ActualExiledPlayerId}",
                            hostMarker);
                        return false;
                    }

                    if (!viewer.TryGetClientId(out var viewerClientId)) return false;
                    planned.Add(new CriticalLease(
                        ledger.MeetingGeneration,
                        viewer.PlayerId,
                        viewerClientId,
                        safetyDummy.PlayerId,
                        dummyActualRole,
                        ledger.ActualExiledPlayerId));
                }

                if (planned.Count == 0)
                    return false;

                foreach (var lease in planned)
                {
                    Leases[lease.ViewerId] = lease;
                    Logger.Info(
                        $"Stage=HOST_SAFETY_DUMMY_LEASE_SELECTED | MeetingGeneration={lease.MeetingGeneration} | ViewerId={lease.ViewerId} | ViewerClientId={lease.ViewerClientId} | DummyId={lease.DummyId} | DummyActualRole={lease.DummyActualRole} | ExiledId={lease.ExiledId} | NewSetRole=False",
                        hostMarker);
                }

                return true;
            }
            internal static void TryApplyAfterSetIsDeadData()
            {
                // REMOTE_MOD_EXILE_RECORDED_POST_SETISDEAD_REASSERT_AB_V21:
                // AntiBlackout.SetIsDead/SendGameData直後に、pre-votingで記録済みの
                // remote MOD exile safety planを先に再assertする。
                TrySendRecordedRemoteModPostSetIsDeadReassertAbV21();

                if (!TryGetExactMixedMappingState(
                        out var ledger,
                        out var connected,
                        out var restoreSnapshot))
                {
                    return;
                }

                ClearAllLeases();
                var planned = new List<CriticalLease>();
                foreach (var viewer in connected)
                {
                    if (viewer.AmOwner || viewer.IsModClient())
                        continue;

                    // V14A_CLIENTREADY_CANDIDATE_SETROLE_REMAINING_IMPOSTOR_GUARD_PERMANENT
                    // 実追放対象を除いたviewer別RoleViewを先に数える。
                    // 既に生存Impostor viewが1名以上あるviewerへ追加Impostorを作ると、
                    // 2 vs 2等のnative parity game-overを逆に発火させるためleaseしない。
                    var remainingAliveImpostorCount = 0;
                    var remainingAliveNonImpostorCount = 0;
                    var remainingRoleViewComplete = true;
                    foreach (var target in connected)
                    {
                        if (target == null ||
                            target.Data == null ||
                            target.Data.Disconnected ||
                            target.PlayerId == ledger.ActualExiledPlayerId ||
                            PlayerState.GetByPlayerId(target.PlayerId).IsDead)
                        {
                            continue;
                        }

                        if (!restoreSnapshot.TryGetValue(
                                (viewer.PlayerId, target.PlayerId),
                                out var remainingRole))
                        {
                            remainingRoleViewComplete = false;
                            break;
                        }

                        var remainingRoleName = remainingRole.ToString();
                        var remainingIsImpostor =
                            remainingRole == RoleTypes.Impostor ||
                            remainingRole == RoleTypes.Shapeshifter ||
                            remainingRoleName == "Phantom" ||
                            remainingRoleName == "Viper";

                        if (remainingIsImpostor)
                            remainingAliveImpostorCount++;
                        else
                            remainingAliveNonImpostorCount++;
                    }

                    if (!remainingRoleViewComplete)
                    {
                        Logger.Info(
                            $"Stage=CLIENTREADY_LEASE_ANALYSIS_ABORT | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | Reason=ROLEVIEW_INCOMPLETE | ExiledId={ledger.ActualExiledPlayerId}",
                            Marker);
                        return;
                    }

                    if (remainingAliveImpostorCount > 0)
                    {
                        Logger.Info(
                            $"Stage=CLIENTREADY_LEASE_NOT_REQUIRED | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | RemainingAliveImpostors={remainingAliveImpostorCount} | RemainingAliveNonImpostors={remainingAliveNonImpostorCount} | ExiledId={ledger.ActualExiledPlayerId}",
                            Marker);
                        continue;
                    }

                    // 0 Impostorを1 Impostorへ変える場合、非Impostorが2名以下だと
                    // candidate変換後に1 >= 1等のparityとなるため安全停止する。
                    if (remainingAliveNonImpostorCount <= 2)
                    {
                        Logger.Info(
                            $"Stage=CLIENTREADY_LEASE_ANALYSIS_ABORT | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | Reason=PARITY_RISK | RemainingAliveImpostors={remainingAliveImpostorCount} | RemainingAliveNonImpostors={remainingAliveNonImpostorCount} | ExiledId={ledger.ActualExiledPlayerId}",
                            Marker);
                        return;
                    }

                    Logger.Info(
                        $"Stage=CLIENTREADY_LEASE_REQUIRED | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | RemainingAliveImpostors=0 | RemainingAliveNonImpostors={remainingAliveNonImpostorCount} | ExiledId={ledger.ActualExiledPlayerId}",
                        Marker);
                    // V14A_CLIENTREADY_CANDIDATE_SETROLE_PERMANENT
                    // V14A独自dummyではなく、ClientReady Barrierで保持した
                    // viewer専用roleAssigned=false candidateだけを使用する。
                    if (!SelectRolesPatch.ClientReadyBarrierCandidateByViewer.TryGetValue(
                            viewer.PlayerId,
                            out var candidateId))
                    {
                        Logger.Info(
                            $"Stage=CLIENTREADY_CANDIDATE_MISSING | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | ExiledId={ledger.ActualExiledPlayerId}",
                            Marker);
                        return;
                    }

                    var dummy = Utils.GetPlayerById(candidateId);
                    if (dummy == null ||
                        dummy.Data == null ||
                        dummy.Data.Disconnected ||
                        dummy.PlayerId == viewer.PlayerId ||
                        dummy.PlayerId == ledger.ActualExiledPlayerId ||
                        !dummy.IsAlive() ||
                        !restoreSnapshot.TryGetValue(
                            (viewer.PlayerId, dummy.PlayerId),
                            out var actualRole))
                    {
                        Logger.Info(
                            $"Stage=CLIENTREADY_CANDIDATE_INELIGIBLE | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | CandidateId={candidateId} | ExiledId={ledger.ActualExiledPlayerId}",
                            Marker);
                        return;
                    }

                    Logger.Info(
                        $"Stage=CLIENTREADY_CANDIDATE_SELECTED | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | CandidateId={dummy.PlayerId} | CandidateActualRole={actualRole} | ExiledId={ledger.ActualExiledPlayerId}",
                        Marker);

                    if (!viewer.TryGetClientId(out var viewerClientId)) return;
                    planned.Add(new CriticalLease(
                        ledger.MeetingGeneration,
                        viewer.PlayerId,
                        viewerClientId,
                        dummy.PlayerId,
                        actualRole,
                        ledger.ActualExiledPlayerId));
                }
                if (planned.Count == 0)
                    return;

                var packets = new List<PreparedPacket>();
                foreach (var lease in planned)
                {
                    var dummy = Utils.GetPlayerById(lease.DummyId);
                    if (dummy == null ||
                        !TryBuildPreparedPacket(lease, dummy, out var packet) ||
                        packet == null)
                    {
                        foreach (var built in packets)
                            built.Sender.stream.Recycle();
                        return;
                    }

                    packets.Add(packet);
                }

                foreach (var packet in packets)
                {
                    var lease = packet.Lease;
                    Leases[lease.ViewerId] = lease;
                    LogStage("LEASE_SELECTED", lease);
                    LogStage("POST_SETISDEAD_DATA_SENT", lease);
                    try
                    {
                        packet.Sender.SendMessage();
                        LogStage("POST_SETISDEAD_DUMMY_APPLIED", lease);

                        // REMOTE_MOD_EXILE_POST_SETISDEAD_FULL_SAFE_REASSERT_AB_V20:
                        // v19のpre-Voting full-safe NPIは、その後のAntiBlackout.SetIsDead
                        // GameDataで上書きされ得る。既存V14Aが危険Viewerを選別し、
                        // Dummy=ImpostorをSetIsDead直後に再適用した直後だけ、
                        // remote MOD実追放の生存Vanilla Viewerへfull-safe NPIを再assertする。
                        // Host canonical Role/RoleWhenAlive/RoleBehaviourは変更しない。
                        TrySendRemoteModPostSetIsDeadFullSafeReassertAbV20(
                            lease);
                    }
                    catch (Exception ex)
                    {
                        Logger.Exception(ex, Marker);
                        if (packet.Sender.CurrentState is not CustomRpcSender.State.Finished)
                            packet.Sender.stream.Recycle();
                    }
                }
            }

            // REMOTE_MOD_EXILE_POST_SETISDEAD_FULL_SAFE_REASSERT_AB_V20:
            // remote MOD実追放で、既存V14Aが「このVanilla viewerは生存Impostor view=0」
            // と判定した場合だけ、SetIsDeadの全Player GameData送信後にv19と同じ
            // wire-only Role/RoleWhenAlive full-safe NPIをtargeted再送する。
            // 既存POST_SETISDEAD_DUMMY_APPLIEDが先にDummy=Impostor SetRoleを送るため、
            // ここではそのRoleBehaviour viewを壊さず、NPI側のRole/RoleWhenAliveを揃える。
            // REMOTE_MOD_EXILE_RECORDED_POST_SETISDEAD_REASSERT_AB_V21:
            // v20 runtimeではSetIsDead直後にCriticalLeaseがまだ存在せず、
            // v20 packetが1件も送信されなかった。
            // v21はpre-voting safety判定結果を記録し、その記録をSetIsDead直後に使う。
            private sealed class RemoteModPostSetIsDeadReassertPlanAbV21
            {
                internal readonly uint MeetingGeneration;
                internal readonly byte ViewerId;
                internal readonly int ViewerClientId;
                internal readonly byte DummyId;
                internal readonly byte ExiledId;

                internal RemoteModPostSetIsDeadReassertPlanAbV21(
                    uint meetingGeneration,
                    byte viewerId,
                    int viewerClientId,
                    byte dummyId,
                    byte exiledId)
                {
                    MeetingGeneration = meetingGeneration;
                    ViewerId = viewerId;
                    ViewerClientId = viewerClientId;
                    DummyId = dummyId;
                    ExiledId = exiledId;
                }
            }

            private static readonly Dictionary<byte, RemoteModPostSetIsDeadReassertPlanAbV21>
                RemoteModPostSetIsDeadReassertPlansAbV21 = new();

            private static void RecordRemoteModPostSetIsDeadReassertPlanAbV21(
                PlayerControl exiled,
                PlayerControl viewer,
                PlayerControl dummy)
            {
                const string marker =
                    "REMOTE_MOD_EXILE_RECORDED_POST_SETISDEAD_REASSERT_AB_V21";

                if (exiled == null ||
                    viewer == null ||
                    dummy == null ||
                    exiled.Data == null ||
                    viewer.Data == null ||
                    dummy.Data == null ||
                    exiled.Data.Disconnected ||
                    viewer.Data.Disconnected ||
                    dummy.Data.Disconnected ||
                    exiled.AmOwner ||
                    !exiled.IsModClient() ||
                    viewer.AmOwner ||
                    viewer.IsModClient())
                {
                    return;
                }

                if (!viewer.TryGetClientId(out var viewerClientId)) return;
                var stale = false;
                foreach (var existing in RemoteModPostSetIsDeadReassertPlansAbV21.Values)
                {
                    if (existing.MeetingGeneration != MeetingVoteManager.H4MeetingGeneration)
                    {
                        stale = true;
                        break;
                    }
                }

                if (stale)
                    RemoteModPostSetIsDeadReassertPlansAbV21.Clear();

                var plan = new RemoteModPostSetIsDeadReassertPlanAbV21(
                    MeetingVoteManager.H4MeetingGeneration,
                    viewer.PlayerId,
                    viewerClientId,
                    dummy.PlayerId,
                    exiled.PlayerId);

                RemoteModPostSetIsDeadReassertPlansAbV21[viewer.PlayerId] = plan;

                Logger.Info(
                    $"Stage=PLAN_RECORDED | MeetingGeneration={plan.MeetingGeneration} | ViewerId={plan.ViewerId} | ViewerClientId={plan.ViewerClientId} | DummyId={plan.DummyId} | ExiledId={plan.ExiledId}",
                    marker);
            }

            internal static void TrySendRecordedRemoteModPostSetIsDeadReassertAbV21()
            {
                const string marker =
                    "REMOTE_MOD_EXILE_RECORDED_POST_SETISDEAD_REASSERT_AB_V21";

                if (RemoteModPostSetIsDeadReassertPlansAbV21.Count == 0)
                    return;

                var plans =
                    new List<RemoteModPostSetIsDeadReassertPlanAbV21>(
                        RemoteModPostSetIsDeadReassertPlansAbV21.Values);
                plans.Sort((left, right) => left.ViewerId.CompareTo(right.ViewerId));

                try
                {
                    foreach (var plan in plans)
                    {
                        if (plan.MeetingGeneration != MeetingVoteManager.H4MeetingGeneration ||
                            !GameStates.IsOnlineGame ||
                            !GameStates.IsInGame ||
                            !AmongUsClient.Instance.AmHost ||
                            !AntiBlackout.OverrideExiledPlayer ||
                            !MeetingVoteManager.H4TryGetActualExileLedger(out var ledger) ||
                            ledger.Path != RequiredLedgerPath ||
                            ledger.IsTie ||
                            ledger.ActualExiledPlayerId != plan.ExiledId)
                        {
                            Logger.Warn(
                                $"Stage=POST_SETISDEAD_ABORT | ViewerId={plan.ViewerId} | ViewerClientId={plan.ViewerClientId} | DummyId={plan.DummyId} | ExiledId={plan.ExiledId} | Reason=GLOBAL_GATE",
                                marker);
                            continue;
                        }

                        var viewer = Utils.GetPlayerById(plan.ViewerId);
                        var dummy = Utils.GetPlayerById(plan.DummyId);
                        var exiledTarget = Utils.GetPlayerById(plan.ExiledId);

                        if (viewer == null ||
                            viewer.Data == null ||
                            viewer.Data.Disconnected ||
                            viewer.AmOwner ||
                            viewer.IsModClient() ||
                            viewer.GetClientId() != plan.ViewerClientId ||
                            dummy == null ||
                            dummy.Data == null ||
                            dummy.Data.Disconnected ||
                            !dummy.IsAlive() ||
                            exiledTarget == null ||
                            exiledTarget.Data == null ||
                            exiledTarget.Data.Disconnected ||
                            exiledTarget.AmOwner ||
                            !exiledTarget.IsModClient())
                        {
                            Logger.Warn(
                                $"Stage=POST_SETISDEAD_ABORT | ViewerId={plan.ViewerId} | ViewerClientId={plan.ViewerClientId} | DummyId={plan.DummyId} | ExiledId={plan.ExiledId} | Reason=PLAYER_GATE",
                                marker);
                            continue;
                        }

                        var prepared = new List<CustomRpcSender>();
                        CustomRpcSender? building = null;
                        var targetCount = 0;
                        var patchedUInt16 = 0;
                        var maxPacketBytes = 0;
                        var buildOk = true;
                        var abortReason = "";

                        foreach (var source in GameData.Instance.AllPlayers)
                        {
                            if (source == null || source.Disconnected)
                                continue;

                            building = new CustomRpcSender(
                                $"RemoteModExile Recorded PostSetIsDead FullSafe AB V21 P{source.PlayerId}",
                                SendOption.Reliable,
                                false);
                            building.StartMessage(plan.ViewerClientId);

                            var wireRole =
                                source.PlayerId == plan.DummyId
                                    ? RoleTypes.Impostor
                                    : RoleTypes.Crewmate;

                            if (!SelectRolesPatch.TryAppendPreVotingFullSafeNetworkedPlayerInfoAbV19(
                                    building,
                                    source,
                                    wireRole,
                                    out var capturedWrites) ||
                                capturedWrites != 2)
                            {
                                abortReason =
                                    $"CAPTURE_COUNT PlayerId={source.PlayerId} Count={capturedWrites}";
                                building.stream.Recycle();
                                building = null;
                                buildOk = false;
                                break;
                            }

                            building.EndMessage();
                            var packetBytes = building.stream.Length;
                            if (packetBytes > 400)
                            {
                                abortReason =
                                    $"PACKET_TOO_LARGE PlayerId={source.PlayerId} Bytes={packetBytes} Max=400";
                                building.stream.Recycle();
                                building = null;
                                buildOk = false;
                                break;
                            }

                            maxPacketBytes = Math.Max(maxPacketBytes, packetBytes);
                            prepared.Add(building);
                            building = null;
                            targetCount++;
                            patchedUInt16 += capturedWrites;
                        }

                        if (!buildOk || targetCount == 0)
                        {
                            foreach (var sender in prepared)
                            {
                                if (sender.CurrentState is not CustomRpcSender.State.Finished)
                                    sender.stream.Recycle();
                            }

                            Logger.Warn(
                                $"Stage=POST_SETISDEAD_ABORT | ViewerId={plan.ViewerId} | ViewerClientId={plan.ViewerClientId} | DummyId={plan.DummyId} | ExiledId={plan.ExiledId} | Reason={(targetCount == 0 && buildOk ? "NO_TARGETS" : abortReason)}",
                                marker);
                            continue;
                        }

                        Logger.Info(
                            $"Stage=POST_SETISDEAD_FULL_SAFE_PREPARED | MeetingGeneration={plan.MeetingGeneration} | ViewerId={plan.ViewerId} | ViewerClientId={plan.ViewerClientId} | DummyId={plan.DummyId} | ExiledId={plan.ExiledId} | Targets={targetCount} | Packets={prepared.Count} | PatchedUInt16={patchedUInt16} | MaxPacketBytes={maxPacketBytes} | RecordedPreVotingPlan=True | AfterSetIsDeadData=True | HostStateMutated=False",
                            marker);

                        CustomRpcSender? setRoleSender = null;
                        try
                        {
                            setRoleSender = CustomRpcSender.Create(
                                "RemoteModExile Recorded PostSetIsDead Safe SetRole AB V21",
                                SendOption.Reliable);
                            setRoleSender.StartMessage(plan.ViewerClientId);
                            setRoleSender
                                .StartRpc(dummy.NetId, RpcCalls.SetRole)
                                .Write((ushort)RoleTypes.Impostor)
                                .Write(true)
                                .EndRpc();
                            setRoleSender.EndMessage();
                            setRoleSender.SendMessage();

                            Logger.Info(
                                $"Stage=POST_SETISDEAD_SAFE_SETROLE_SENT | MeetingGeneration={plan.MeetingGeneration} | ViewerId={plan.ViewerId} | ViewerClientId={plan.ViewerClientId} | DummyId={plan.DummyId} | ExiledId={plan.ExiledId} | SeparateReliableMessage=True | ArtificialDelayMs=0",
                                marker);
                        }
                        catch (Exception ex)
                        {
                            Logger.Exception(ex, marker);
                            if (setRoleSender?.CurrentState is not CustomRpcSender.State.Finished)
                                setRoleSender?.stream.Recycle();

                            foreach (var sender in prepared)
                            {
                                if (sender.CurrentState is not CustomRpcSender.State.Finished)
                                    sender.stream.Recycle();
                            }

                            continue;
                        }

                        var sent = 0;
                        foreach (var sender in prepared)
                        {
                            sender.SendMessage();
                            sent++;
                        }

                        Logger.Info(
                            $"Stage=POST_SETISDEAD_FULL_SAFE_SENT | MeetingGeneration={plan.MeetingGeneration} | ViewerId={plan.ViewerId} | ViewerClientId={plan.ViewerClientId} | DummyId={plan.DummyId} | ExiledId={plan.ExiledId} | Targets={targetCount} | Packets={sent} | PatchedUInt16={patchedUInt16} | MaxPacketBytes={maxPacketBytes} | RecordedPreVotingPlan=True | AfterSetIsDeadData=True | BeforeLocalWrapUp=True | HostStateMutated=False",
                            marker);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex, marker);
                }
                finally
                {
                    RemoteModPostSetIsDeadReassertPlansAbV21.Clear();
                }
            }

            private static bool TrySendRemoteModPostSetIsDeadFullSafeReassertAbV20(
                CriticalLease lease)
            {
                const string marker =
                    "REMOTE_MOD_EXILE_POST_SETISDEAD_FULL_SAFE_REASSERT_AB_V20";

                var viewer = Utils.GetPlayerById(lease.ViewerId);
                var dummy = Utils.GetPlayerById(lease.DummyId);
                var exiledTarget = Utils.GetPlayerById(lease.ExiledId);

                if (!GameStates.IsOnlineGame ||
                    !GameStates.IsInGame ||
                    !AmongUsClient.Instance.AmHost ||
                    !AntiBlackout.OverrideExiledPlayer ||
                    viewer == null ||
                    viewer.Data == null ||
                    viewer.Data.Disconnected ||
                    viewer.Data.IsDead ||
                    viewer.AmOwner ||
                    viewer.IsModClient() ||
                    viewer.GetClientId() != lease.ViewerClientId ||
                    dummy == null ||
                    dummy.Data == null ||
                    dummy.Data.Disconnected ||
                    !dummy.IsAlive() ||
                    dummy.PlayerId != lease.DummyId ||
                    exiledTarget == null ||
                    exiledTarget.Data == null ||
                    exiledTarget.Data.Disconnected ||
                    exiledTarget.AmOwner ||
                    !exiledTarget.IsModClient() ||
                    exiledTarget.PlayerId != lease.ExiledId)
                {
                    return false;
                }

                var preparedSenders = new List<CustomRpcSender>();
                CustomRpcSender? buildingSender = null;
                var preparedTargetCount = 0;
                var patchedUInt16Count = 0;
                var maxPacketBytes = 0;
                var sentPacketCount = 0;
                var abortReason = "";

                try
                {
                    foreach (var source in GameData.Instance.AllPlayers)
                    {
                        if (source == null || source.Disconnected)
                            continue;

                        buildingSender = new CustomRpcSender(
                            $"RemoteModExile PostSetIsDead FullSafe AB V20 P{source.PlayerId}",
                            SendOption.Reliable,
                            false);
                        buildingSender.StartMessage(lease.ViewerClientId);

                        var wireRole =
                            source.PlayerId == lease.DummyId
                                ? RoleTypes.Impostor
                                : RoleTypes.Crewmate;

                        if (!SelectRolesPatch.TryAppendPreVotingFullSafeNetworkedPlayerInfoAbV19(
                                buildingSender,
                                source,
                                wireRole,
                                out var capturedWrites) ||
                            capturedWrites != 2)
                        {
                            abortReason =
                                $"CAPTURE_COUNT PlayerId={source.PlayerId} Count={capturedWrites}";
                            buildingSender.stream.Recycle();
                            buildingSender = null;

                            foreach (var prepared in preparedSenders)
                                prepared.stream.Recycle();

                            Logger.Warn(
                                $"Stage=POST_SETISDEAD_FULL_SAFE_ABORT | ViewerId={lease.ViewerId} | ViewerClientId={lease.ViewerClientId} | DummyId={lease.DummyId} | ExiledId={lease.ExiledId} | Reason={abortReason}",
                                marker);
                            return false;
                        }

                        buildingSender.EndMessage();
                        var packetBytes = buildingSender.stream.Length;
                        if (packetBytes > 400)
                        {
                            abortReason =
                                $"PACKET_TOO_LARGE PlayerId={source.PlayerId} Bytes={packetBytes} Max=400";
                            buildingSender.stream.Recycle();
                            buildingSender = null;

                            foreach (var prepared in preparedSenders)
                                prepared.stream.Recycle();

                            Logger.Warn(
                                $"Stage=POST_SETISDEAD_FULL_SAFE_ABORT | ViewerId={lease.ViewerId} | ViewerClientId={lease.ViewerClientId} | DummyId={lease.DummyId} | ExiledId={lease.ExiledId} | Reason={abortReason}",
                                marker);
                            return false;
                        }

                        maxPacketBytes = Math.Max(maxPacketBytes, packetBytes);
                        preparedSenders.Add(buildingSender);
                        buildingSender = null;
                        preparedTargetCount++;
                        patchedUInt16Count += capturedWrites;
                    }

                    if (preparedTargetCount == 0)
                    {
                        Logger.Warn(
                            $"Stage=POST_SETISDEAD_FULL_SAFE_ABORT | ViewerId={lease.ViewerId} | ViewerClientId={lease.ViewerClientId} | DummyId={lease.DummyId} | ExiledId={lease.ExiledId} | Reason=NO_TARGETS",
                            marker);
                        return false;
                    }

                    Logger.Info(
                        $"Stage=POST_SETISDEAD_FULL_SAFE_PREPARED | MeetingGeneration={lease.MeetingGeneration} | ViewerId={lease.ViewerId} | ViewerClientId={lease.ViewerClientId} | DummyId={lease.DummyId} | ExiledId={lease.ExiledId} | Targets={preparedTargetCount} | Packets={preparedSenders.Count} | PatchedUInt16={patchedUInt16Count} | MaxPacketBytes={maxPacketBytes} | AfterSetIsDeadData=True | HostStateMutated=False",
                        marker);

                    foreach (var prepared in preparedSenders)
                    {
                        prepared.SendMessage();
                        sentPacketCount++;
                    }

                    Logger.Info(
                        $"Stage=POST_SETISDEAD_FULL_SAFE_SENT | MeetingGeneration={lease.MeetingGeneration} | ViewerId={lease.ViewerId} | ViewerClientId={lease.ViewerClientId} | DummyId={lease.DummyId} | ExiledId={lease.ExiledId} | Targets={preparedTargetCount} | Packets={sentPacketCount} | PatchedUInt16={patchedUInt16Count} | MaxPacketBytes={maxPacketBytes} | AfterSetIsDeadData=True | BeforeLocalWrapUp=True | HostStateMutated=False",
                        marker);

                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex, marker);

                    if (buildingSender?.CurrentState is not CustomRpcSender.State.Finished)
                        buildingSender?.stream.Recycle();

                    for (var i = sentPacketCount; i < preparedSenders.Count; i++)
                    {
                        var prepared = preparedSenders[i];
                        if (prepared.CurrentState is not CustomRpcSender.State.Finished)
                            prepared.stream.Recycle();
                    }

                    return false;
                }
            }

            internal static bool TryHoldRestore(
                PlayerControl viewer,
                PlayerControl dummy,
                RoleTypes actualRole)
            {
                if (!Leases.TryGetValue(viewer.PlayerId, out var lease) ||
                    lease.MeetingGeneration != MeetingVoteManager.H4MeetingGeneration ||
                    lease.ViewerClientId != viewer.GetClientId() ||
                    lease.DummyId != dummy.PlayerId ||
                    viewer.Data == null ||
                    viewer.Data.Disconnected ||
                    dummy.Data == null ||
                    dummy.Data.Disconnected)
                {
                    return false;
                }

                lease.DummyActualRole = actualRole;
                if (!lease.RestoreHeldLogged)
                {
                    lease.RestoreHeldLogged = true;
                    LogStage("RESTORE_DUMMY_HELD", lease);
                }
                return true;
            }

            internal static void FinalizeAfterActualData(NetworkedPlayerInfo? exiled)
            {
                if (Leases.Count == 0)
                    return;

                var active = new List<CriticalLease>(Leases.Values);
                active.Sort((left, right) => left.ViewerId.CompareTo(right.ViewerId));
                foreach (var lease in active)
                {
                    LogStage("FINALIZER_ACTUAL_DATA_SENT", lease);
                    var viewer = Utils.GetPlayerById(lease.ViewerId);
                    var dummy = Utils.GetPlayerById(lease.DummyId);
                    var exiledTarget = Utils.GetPlayerById(lease.ExiledId);
                    // MOD_EXILE_REMOTE_VANILLA_PERSISTENT_IMPOSTOR_LEASE_AB_V8:
                    // 生存remote Vanilla viewerが実追放後にviewer-visible alive Impostor=0へ戻る経路では、
                    // dummy Impostor leaseをfinalizer後も保持し、Vanilla native game-over/blackout再判定を防ぐ。
                    // viewer自身の実追放(V14A既存成功経路)は従来どおりrestore/clearする。
                    if (lease.ViewerId != lease.ExiledId &&
                        viewer?.Data != null &&
                        !viewer.Data.Disconnected &&
                        !viewer.Data.IsDead &&
                        !viewer.AmOwner &&
                        !viewer.IsModClient() &&
                        viewer.GetClientId() == lease.ViewerClientId &&
                        dummy?.Data != null &&
                        !dummy.Data.Disconnected &&
                        dummy.IsAlive() &&
                        exiledTarget != null &&
                        (exiledTarget.AmOwner || exiledTarget.IsModClient()))
                    {
                        // REMOTE_MOD_EXILE_FINALIZER_FULL_SAFE_REASSERT_AB_V23:
                        // WrapUpFinalizerのactual SendGameData直後に、persistent lease対象だけへ
                        // Dummy=Impostor SetRoleをtargeted Reliableで再投入し、その直後に既存V20の
                        // wire-only full-safe Role/RoleWhenAlive NPIを再送する。
                        // Host canonical role / broadcast / artificial delayは変更しない。
                        var finalizerSafeSetRoleSent = false;
                        CustomRpcSender? finalizerSafeSetRoleSender = null;
                        try
                        {
                            finalizerSafeSetRoleSender = CustomRpcSender.Create(
                                "RemoteModExile Finalizer Safe SetRole AB V23",
                                SendOption.Reliable);
                            finalizerSafeSetRoleSender.StartMessage(lease.ViewerClientId);
                            finalizerSafeSetRoleSender
                                .StartRpc(dummy.NetId, RpcCalls.SetRole)
                                .Write((ushort)RoleTypes.Impostor)
                                .Write(true)
                                .EndRpc();
                            finalizerSafeSetRoleSender.EndMessage();
                            finalizerSafeSetRoleSender.SendMessage();
                            finalizerSafeSetRoleSent = true;

                            Logger.Info(
                                $"Stage=FINALIZER_SAFE_SETROLE_SENT | MeetingGeneration={lease.MeetingGeneration} | ViewerId={lease.ViewerId} | ViewerClientId={lease.ViewerClientId} | DummyId={lease.DummyId} | ExiledId={lease.ExiledId} | SeparateReliableMessage=True | AfterFinalizerActualData=True | BeforeAtomicPoststate=True | ArtificialDelayMs=0 | HostStateMutated=False",
                                "REMOTE_MOD_EXILE_FINALIZER_FULL_SAFE_REASSERT_AB_V23");
                        }
                        catch (Exception ex)
                        {
                            Logger.Exception(
                                ex,
                                "REMOTE_MOD_EXILE_FINALIZER_FULL_SAFE_REASSERT_AB_V23");

                            if (finalizerSafeSetRoleSender?.CurrentState is not CustomRpcSender.State.Finished)
                                finalizerSafeSetRoleSender?.stream.Recycle();
                        }

                        var finalizerFullSafeSent =
                            finalizerSafeSetRoleSent &&
                            TrySendRemoteModPostSetIsDeadFullSafeReassertAbV20(
                                lease);

                        Logger.Info(
                            $"Stage=FINALIZER_FULL_SAFE_REASSERT_RESULT | MeetingGeneration={lease.MeetingGeneration} | ViewerId={lease.ViewerId} | ViewerClientId={lease.ViewerClientId} | DummyId={lease.DummyId} | ExiledId={lease.ExiledId} | SafeSetRoleSent={finalizerSafeSetRoleSent} | FullSafeSent={finalizerFullSafeSent} | AfterFinalizerActualData=True | BeforeAtomicPoststate=True | HostStateMutated=False",
                            "REMOTE_MOD_EXILE_FINALIZER_FULL_SAFE_REASSERT_AB_V23");

                        Logger.Info(
                            $"Stage=PERSISTENT_LEASE_HELD | MeetingGeneration={lease.MeetingGeneration} | ViewerId={lease.ViewerId} | ViewerClientId={lease.ViewerClientId} | DummyId={lease.DummyId} | DummyActualRole={lease.DummyActualRole} | ExiledId={lease.ExiledId} | ExiledWasHost={exiledTarget.AmOwner} | ExiledWasRemoteMod={!exiledTarget.AmOwner && exiledTarget.IsModClient()}",
                            "MOD_EXILE_REMOTE_VANILLA_PERSISTENT_IMPOSTOR_LEASE_AB_V8");
                        continue;
                    }
                    if (AmongUsClient.Instance.AmHost &&
                        exiled?.PlayerId == lease.ExiledId &&
                        viewer?.Data != null &&
                        !viewer.Data.Disconnected &&
                        viewer.GetClientId() == lease.ViewerClientId &&
                        dummy?.Data != null &&
                        !dummy.Data.Disconnected)
                    {
                        CustomRpcSender? sender = null;
                        try
                        {
                            sender = CustomRpcSender.Create(Marker, SendOption.Reliable);
                            sender.StartMessage(lease.ViewerClientId);
                            sender.StartRpc(dummy.NetId, RpcCalls.SetRole)
                                .Write((ushort)lease.DummyActualRole)
                                .Write(true)
                                .EndRpc();
                            sender.EndMessage();
                            sender.SendMessage();
                            LogStage("FINALIZER_DUMMY_RESTORED", lease);
                        }
                        catch (Exception ex)
                        {
                            Logger.Exception(ex, Marker);
                            if (sender?.CurrentState is not CustomRpcSender.State.Finished)
                                sender?.stream.Recycle();
                        }
                    }

                    ClearLease(lease);
                }
            }
        }

        [HarmonyPatch]
        class VanillaActualExileCriticalLeaseUInt16WritePatch
        {
            private static System.Reflection.MethodBase TargetMethod()
            {
                return AccessTools.Method(
                    typeof(MessageWriter),
                    nameof(MessageWriter.Write),
                    new[] { typeof(ushort) })!;
            }

            public static void Prefix(
                MessageWriter __instance,
                [HarmonyArgument(0)] ushort value)
            {
                VanillaActualExileCriticalLeaseAbV14A.CaptureUInt16Write(__instance, value);
            }
        }

        [HarmonyPatch(
            typeof(AntiBlackout),
            nameof(AntiBlackout.RestoreRealExileDisplayRolesPermanent))]
        class VanillaActualExileAtomicDeadGhostRestorePatch
        {
            public static void Prefix(out bool __state)
            {
                __state = VanillaActualExileAtomicDeadGhostAbV34.BeginRestoreCall();
            }

            public static Exception? Finalizer(Exception? __exception, bool __state)
            {
                VanillaActualExileAtomicDeadGhostAbV34.FinishRestoreCall(
                    __state,
                    completedNormally: __exception == null);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft))]
        class VanillaActualExileAtomicDeadGhostDisconnectPatch
        {
            public static void Prefix([HarmonyArgument(0)] ClientData? data)
            {
                if (data?.Character != null)
                {
                    VanillaActualExileAtomicDeadGhostAbV34.ResetForDisconnectedPlayer(
                        data.Character.PlayerId);
                    VanillaActualExileCriticalLeaseAbV14A.ResetForDisconnectedPlayer(
                        data.Character.PlayerId);
                }
            }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
        class VanillaActualExileAtomicDeadGhostGameEndPatch
        {
            public static void Prefix()
            {
                TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.End());
                VanillaActualExileAtomicDeadGhostAbV34.ResetForLifecycle("GAME_END");
                VanillaActualExileCriticalLeaseAbV14A.ResetForLifecycle();
            }
        }
        [HarmonyPatch(typeof(ExileController), nameof(ExileController.WrapUp))]
        class BaseExileControllerPatch
        {
            public static void Postfix(ExileController __instance)
            {
                TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.WrapStage("AFTER_NATIVE_WRAPUP"));
                try
                {
                    WrapUpPostfix(__instance.initData.networkedPlayer);
                }
                catch (Exception ex)
                {
                    Logger.Info("追放処理例外：" + ex.Message + "/" + ex.StackTrace, "WrapUpAndSpawn");
                }
                finally
                {
                    WrapUpFinalizer(__instance.initData.networkedPlayer);
                }
            }
        }

        [HarmonyPatch(typeof(AirshipExileController), nameof(AirshipExileController.Animate))]
        class AirshipStatusPatch
        {
            public static void Postfix(AirshipExileController __instance, ref Il2CppSystem.Collections.IEnumerator __result)
            {
                //WrapUpAndSpawnに直接パッチが当たらないのでAnimateメソッド中にパッチを当てる
                var pathcer = new CoroutinPatcher(__result);
                //WrapUpAndSpawnはステートマシンとしてクラス化されているためそのクラス実行前にパッチを当てる
                //元々Postfixだが、タイミング的にはPrefixの方が適切なのでPrefixに当てる
                pathcer.AddPrefix(typeof(AirshipExileController._WrapUpAndSpawn_d__11), () =>
                    AirshipExileControllerPatch.Postfix(__instance)
                );
                __result = pathcer.EnumerateWithPatch();
            }
        }
        // Patchが当たらないが念のためコメントアウト
        //[HarmonyPatch(typeof(AirshipExileController), nameof(AirshipExileController.WrapUpAndSpawn))]
        class AirshipExileControllerPatch
        {
            public static void Postfix(AirshipExileController __instance)
            {
                TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.WrapStage("BEFORE_NATIVE_WRAPUP_AND_SPAWN"));
                try
                {
                    WrapUpPostfix(__instance.initData.networkedPlayer);
                }
                catch (Exception ex)
                {
                    Logger.Info("追放処理例外：" + ex.Message + "/" + ex.StackTrace, "WrapUpAndSpawn");
                }
                finally
                {
                    WrapUpFinalizer(__instance.initData.networkedPlayer);
                }
            }
        }
        static void WrapUpPostfix(NetworkedPlayerInfo exiled)
        {
            if (AntiBlackout.OverrideExiledPlayer)
            {
                exiled = AntiBlackout_LastExiled;
            }

            var mapId = Main.NormalOptions.MapId;
            // エアシップではまだ湧かない
            if ((MapNames)mapId != MapNames.Airship)
            {
                foreach (var state in PlayerState.AllPlayerStates.Values)
                {
                    state.HasSpawned = true;
                }
            }

            bool DecidedWinner = false;
            if (!AmongUsClient.Instance.AmHost) return; //ホスト以外はこれ以降の処理を実行しません
            AntiBlackout.RestoreIsDead(doSend: false);

            // ExileDisplayRealPermanent:
            // IsDeadのホスト側復元直後、クライアントへ一時RoleTypeを正確に戻す。
            AntiBlackout.RestoreRealExileDisplayRolesPermanent();
            // ROLEBUGTRACE_STAGE1B_PERMANENT
            RoleBugTrace.Exile(exiled);
            if (exiled != null)
            {
                var role = exiled.GetCustomRole();
                var info = role.GetRoleInfo();
                //霊界用暗転バグ対処
                if (!AntiBlackout.OverrideExiledPlayer && info?.IsDesyncImpostor == true)
                    exiled.Object?.ResetPlayerCam(1f);

                exiled.IsDead = true;
                if (role != CustomRoles.AntiComplete)
                    PlayerState.GetByPlayerId(exiled.PlayerId).DeathReason = CustomDeathReason.Vote;

                foreach (var roleClass in CustomRoleManager.AllActiveRoles.Values)
                {
                    roleClass.OnExileWrapUp(exiled, ref DecidedWinner);
                }
                // ROLEBUGTRACE_EXECUTIONER_EXILE_AFTER_PERMANENT:
                // 全OnExileWrapUp完了後の状態だけを診断ログへ記録する。
                RoleBugTrace.ExileAfterRoleEffects(exiled);
                Sending.OnExileWrapUp(exiled.Object);

                if (CustomWinnerHolder.WinnerTeam != CustomWinner.Terrorist) PlayerState.GetByPlayerId(exiled.PlayerId).SetDead();
            }
            foreach (var pc in Main.AllPlayerControls)
            {
                pc.ResetKillCooldown();
            }
            if (RandomSpawn.IsRandomSpawn())
            {
                RandomSpawn.SpawnMap map;
                switch (mapId)
                {
                    case 0:
                        map = new RandomSpawn.SkeldSpawnMap();
                        Main.AllPlayerControls.Do(map.RandomTeleport);
                        break;
                    case 1:
                        map = new RandomSpawn.MiraHQSpawnMap();
                        Main.AllPlayerControls.Do(map.RandomTeleport);
                        break;
                    case 2:
                        map = new RandomSpawn.PolusSpawnMap();
                        Main.AllPlayerControls.Do(map.RandomTeleport);
                        break;
                    case 5:
                        map = new RandomSpawn.FungleSpawnMap();
                        Main.AllPlayerControls.Do(map.RandomTeleport);
                        break;
                }
            }
            FallFromLadder.Reset();
            Utils.CountAlivePlayers(true);
            Utils.AfterMeetingTasks();
            if (mapId != 4)
            {
                foreach (var pc in Main.AllPlayerControls)
                {
                    pc.GetRoleClass()?.OnSpawn();
                    pc.SyncSettings();
                    pc.RpcResetAbilityCooldown();
                }

            }
            Utils.NotifyRoles();
        }
        static void WrapUpFinalizer(NetworkedPlayerInfo exiled)
        {
            //WrapUpPostfixで例外が発生しても、この部分だけは確実に実行されます。
            if (AmongUsClient.Instance.AmHost)
            {
                var synchronousSelfGhostSent =
                    VanillaActualExileAtomicDeadGhostAbV34
                        .CompleteWrapUpSynchronous(exiled);

                // VIEWER_NATIVE_TOPOLOGY_LIFECYCLE_AB_V27:
                // Original WrapUp/slot13復帰後だけrestore/handoffする。
                // SYNC_SELF_GHOST_FAIL_OPEN_AB_V1:
                // sync成功時だけexiled self entryをV27から除外する。
                AntiBlackout.FinalizeViewerNativeTopologyAfterWrapUpAbV27(
                    exiled,
                    synchronousSelfGhostSent);
                TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.PostWrapUp(exiled, synchronousSelfGhostSent));

                _ = new LateTask(() =>
                {
                    exiled = AntiBlackout_LastExiled;
                    AntiBlackout.SendGameData();
                    VanillaActualExileCriticalLeaseAbV14A.FinalizeAfterActualData(exiled);
                    VanillaActualExileCriticalLeaseAbV14A.TrySendModExileAtomicPoststateAbV6(exiled);
                    VanillaActualExileAtomicDeadGhostAbV34.TrySendAtomicPacket(exiled);
                    if (AntiBlackout.OverrideExiledPlayer && // 追放対象が上書きされる状態 (上書きされない状態なら実行不要)
                        exiled != null && //exiledがnullでない
                        exiled.Object != null) //exiled.Objectがnullでない
                    {
                        exiled.Object.RpcExile();
                    }

                    // VIEWER_NATIVE_TOPOLOGY_OUTCOME_BARRIER_AB_V28:
                    // Canonical actual GameData / atomic poststate / final RpcExile may
                    // overwrite a wire-only task sentinel. Reassert only after all of them.
                    AntiBlackout.ReassertViewerNativePersistentSentinelsAfterActualDataAbV28();
                    TownOfHostForE.Roles.Impostor.EvilHacker.RearmSelfOnlyPhantomAfterFinalCanonicalABV3C();
                }, 2.0f, "Restore IsDead Task");
                _ = new LateTask(() =>
                {
                    Main.AfterMeetingDeathPlayers.Do(x =>
                    {
                        var player = Utils.GetPlayerById(x.Key);
                        var roleClass = CustomRoleManager.GetByPlayerId(x.Key);
                        var requireResetCam = player?.GetCustomRole().GetRoleInfo()?.IsDesyncImpostor == true;
                        var state = PlayerState.GetByPlayerId(x.Key);
                        Logger.Info($"{player.GetNameWithRole()}を{x.Value}で死亡させました", "AfterMeetingDeath");
                        state.DeathReason = x.Value;
                        state.SetDead();
                        player?.RpcExile();
                        if (x.Value == CustomDeathReason.Suicide)
                            player?.SetRealKiller(player, true);
                        if (requireResetCam)
                            player?.ResetPlayerCam(1f);
                        if (roleClass is Executioner executioner && executioner.TargetId == x.Key)
                            Executioner.ChangeRoleByTarget(x.Key);
                    });
                    Main.AfterMeetingDeathPlayers.Clear();
                }, 0.5f, "AfterMeetingDeathPlayers Task");
                TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.ScheduleFinal());
            }

            GameStates.AlreadyDied |= !Utils.IsAllAlive;
            RemoveDisableDevicesPatch.UpdateDisableDevices();
            SoundManager.Instance.ChangeAmbienceVolume(DataManager.Settings.Audio.AmbienceVolume);
            GameStates.InTask = true;

            if (AmongUsClient.Instance.AmHost)
            {
                // DEAD_PET_AFTER_MEETING_CLEANUP_V1:
                // OnSpawn / outfit restore後に全死亡者へ空Petを再assertする。
                TownOfHostForE.Patches.PetSettings.RemoveAllDeadPetVisualsReliable(
                    "MeetingWrapUp.TaskPhase");

                // AfterMeetingDeathPlayersの0.5秒処理より後に1回だけ再確認する。
                _ = new LateTask(() =>
                {
                    if (!AmongUsClient.Instance.AmHost)
                        return;

                    if (!GameStates.InTask)
                        return;

                    TownOfHostForE.Patches.PetSettings.RemoveAllDeadPetVisualsReliable(
                        "MeetingWrapUp.Recheck");
                }, 0.75f, "Dead Pet Cleanup Recheck");
            }

            Logger.Info("タスクフェイズ開始", "Phase");
            // ROLEBUGTRACE_STAGE1B_PERMANENT
            RoleBugTrace.MeetingEnd();
            Badger.MeetingEndCheck();
            Tiikawa.MeetingEndCheck();

            if (AmongUsClient.Instance.AmHost)
            {
                try
                {
                    VanillaActualExilePostStateCriticalExiledBookendAbV5.QueueFromFallbackLedger();
                }
                catch (Exception ex)
                {
                    Logger.Info(
                        $"Stage=QUEUE_EXCEPTION_SUPPRESSED | ExceptionType={ex.GetType().FullName} | Message={ex.Message}",
                        VanillaActualExilePostStateCriticalExiledBookendAbV5.Marker);
                }
            }
        }

        // VANILLA_ACTUAL_EXILE_POSTSTATE_CRITICAL_EXILED_BOOKEND_AB_V5
        // Single-variable follow-up to V4 partial recovery: add one targeted Reliable raw Exiled between critical start and clear.
        // This A/B preserves the V4 target-specific critical-system 128 -> 16/17 bookend.
        // It adds exactly one targeted Reliable payloadless RpcCalls.Exiled and still sends no MurderPlayer, Ghost Role, or GameData.
        internal static class VanillaActualExilePostStateCriticalExiledBookendAbV5
        {
            internal const string Marker = "VANILLA_ACTUAL_EXILE_POSTSTATE_CRITICAL_EXILED_BOOKEND_AB_V5";
            private const string RequiredLedgerPath = "ANTIBLACKOUT_FALLBACK";
            private const float PollInterval = 0.1f;
            private const double MaxLifetimeSeconds = 30d;
            private static readonly Dictionary<byte, uint> QueuedPlayers = new();

            internal static void QueueFromFallbackLedger()
            {
                if (!GameStates.IsOnlineGame ||
                    !AmongUsClient.Instance.AmHost ||
                    !MeetingVoteManager.H4TryGetActualExileLedger(out var ledger) ||
                    ledger.Path != RequiredLedgerPath ||
                    ledger.IsTie ||
                    ledger.RealVotingCompleteDelivered ||
                    ledger.ActualExiledPlayerId == byte.MaxValue ||
                    ledger.TargetClientId < 0)
                {
                    return;
                }

                var target = Utils.GetPlayerById(ledger.ActualExiledPlayerId);
                if (target == null ||
                    target.Data == null ||
                    target.Data.Disconnected ||
                    target.AmOwner ||
                    target.IsModClient() ||
                    target.GetClientId() != ledger.TargetClientId)
                {
                    return;
                }

                byte playerId = target.PlayerId;
                uint generation = ledger.MeetingGeneration;

                if (QueuedPlayers.TryGetValue(playerId, out var queuedGeneration) &&
                    queuedGeneration == generation)
                {
                    return;
                }

                QueuedPlayers[playerId] = generation;
                DateTime queuedAt = DateTime.UtcNow;

                Log("QUEUED", target, playerId, ledger.TargetClientId, generation, queuedAt);
                Log("WAITING", target, playerId, ledger.TargetClientId, generation, queuedAt);
                SchedulePoll(
                    target,
                    playerId,
                    ledger.TargetClientId,
                    generation,
                    queuedAt,
                    null);
            }

            private static void SchedulePoll(
                PlayerControl target,
                byte playerId,
                int clientId,
                uint generation,
                DateTime queuedAt,
                DateTime? readyAt)
            {
                float delay = Math.Max(
                    PollInterval,
                    UnityEngine.Time.deltaTime + 0.001f);

                _ = new LateTask(
                    () => Poll(
                        target,
                        playerId,
                        clientId,
                        generation,
                        queuedAt,
                        readyAt),
                    delay,
                    "");
            }

            private static void Poll(
                PlayerControl target,
                byte playerId,
                int clientId,
                uint generation,
                DateTime queuedAt,
                DateTime? readyAt)
            {
                try
                {
                    if (!IsQueueOwned(playerId, generation))
                        return;

                    DateTime now = DateTime.UtcNow;

                    if ((now - queuedAt).TotalSeconds >= MaxLifetimeSeconds)
                    {
                        Log("CANCELED_TIMEOUT", target, playerId, clientId, generation, queuedAt);
                        RemoveIfOwned(playerId, generation);
                        return;
                    }

                    if (!GameStates.IsInGame || GameStates.IsEnded)
                    {
                        Log("CANCELED_GAME_END", target, playerId, clientId, generation, queuedAt);
                        RemoveIfOwned(playerId, generation);
                        return;
                    }

                    if (target == null ||
                        target.Data == null ||
                        target.Data.Disconnected)
                    {
                        Log("CANCELED_DISCONNECT", target, playerId, clientId, generation, queuedAt);
                        RemoveIfOwned(playerId, generation);
                        return;
                    }

                    if (!GameStates.IsOnlineGame ||
                        target.AmOwner ||
                        target.IsModClient() ||
                        target.GetClientId() != clientId ||
                        !LedgerStillMatches(playerId, clientId, generation))
                    {
                        Log("CANCELED_STATE_INVALID", target, playerId, clientId, generation, queuedAt);
                        RemoveIfOwned(playerId, generation);
                        return;
                    }

                    if (!IsReady(target))
                    {
                        if (readyAt.HasValue)
                            Log("WAITING", target, playerId, clientId, generation, queuedAt);

                        SchedulePoll(
                            target,
                            playerId,
                            clientId,
                            generation,
                            queuedAt,
                            null);
                        return;
                    }

                    if (!readyAt.HasValue)
                    {
                        SchedulePoll(
                            target,
                            playerId,
                            clientId,
                            generation,
                            queuedAt,
                            now);
                        return;
                    }

                    if ((now - readyAt.Value).TotalMilliseconds < 100d)
                    {
                        SchedulePoll(
                            target,
                            playerId,
                            clientId,
                            generation,
                            queuedAt,
                            readyAt);
                        return;
                    }

                    if (!IsReady(target) ||
                        !LedgerStillMatches(playerId, clientId, generation))
                    {
                        Log("WAITING", target, playerId, clientId, generation, queuedAt);
                        SchedulePoll(
                            target,
                            playerId,
                            clientId,
                            generation,
                            queuedAt,
                            null);
                        return;
                    }

                    Log("READY_AFTER_100MS_V8", target, playerId, clientId, generation, queuedAt);
                    Log("FIRED_BEFORE", target, playerId, clientId, generation, queuedAt);
                    ScheduleCriticalExiledBookend(
                        target,
                        playerId,
                        clientId,
                        generation,
                        queuedAt);
                    Log("FIRED_AFTER", target, playerId, clientId, generation, queuedAt);
                    RemoveIfOwned(playerId, generation);
                }
                catch
                {
                    RemoveIfOwned(playerId, generation);
                    throw;
                }
            }

            private static void ScheduleCriticalExiledBookend(
                PlayerControl target,
                byte playerId,
                int clientId,
                uint generation,
                DateTime queuedAt)
            {
                var systemType = Utils.GetCriticalSabotageSystemType();

                _ = new LateTask(
                    () =>
                    {
                        target.RpcDesyncUpdateSystem(systemType, 128);
                        Log(
                            "CRITICAL_START_SENT",
                            target,
                            playerId,
                            clientId,
                            generation,
                            queuedAt);
                    },
                    0f,
                    "Vanilla Exile V5 Critical Start");

                _ = new LateTask(
                    () =>
                    {
                        var writer = AmongUsClient.Instance.StartRpcImmediately(
                            target.NetId,
                            (byte)RpcCalls.Exiled,
                            SendOption.Reliable,
                            clientId);
                        AmongUsClient.Instance.FinishRpcImmediately(writer);

                        Log(
                            "TARGETED_EXILED_SENT",
                            target,
                            playerId,
                            clientId,
                            generation,
                            queuedAt);
                    },
                    0.2f,
                    "Vanilla Exile V5 Targeted Raw Exiled");

                _ = new LateTask(
                    () =>
                    {
                        target.RpcDesyncUpdateSystem(systemType, 16);
                        if (Main.NormalOptions.MapId == 4)
                            target.RpcDesyncUpdateSystem(systemType, 17);

                        Log(
                            "CRITICAL_CLEAR_SENT",
                            target,
                            playerId,
                            clientId,
                            generation,
                            queuedAt);
                    },
                    0.4f,
                    "Vanilla Exile V5 Critical Clear");
            }

            private static bool LedgerStillMatches(
                byte playerId,
                int clientId,
                uint generation)
            {
                return MeetingVoteManager.H4TryGetActualExileLedger(out var ledger) &&
                       ledger.MeetingGeneration == generation &&
                       ledger.ActualExiledPlayerId == playerId &&
                       ledger.TargetClientId == clientId &&
                       !ledger.RealVotingCompleteDelivered &&
                       !ledger.IsTie &&
                       ledger.Path == RequiredLedgerPath;
            }

            private static bool IsReady(PlayerControl target)
            {
                return GameStates.IsOnlineGame &&
                       !GameStates.IsMeeting &&
                       ExileController.Instance == null &&
                       !AntiBlackout.IsCached &&
                       GameStates.IsInTask &&
                       !GameStates.IsEnded &&
                       target != null &&
                       !target.AmOwner &&
                       !target.IsModClient() &&
                       target.Data != null &&
                       !target.Data.Disconnected;
            }

            private static bool IsQueueOwned(byte playerId, uint generation)
            {
                return QueuedPlayers.TryGetValue(playerId, out var queuedGeneration) &&
                       queuedGeneration == generation;
            }

            private static void RemoveIfOwned(byte playerId, uint generation)
            {
                if (IsQueueOwned(playerId, generation))
                    QueuedPlayers.Remove(playerId);
            }

            private static void Log(
                string stage,
                PlayerControl? target,
                byte playerId,
                int clientId,
                uint generation,
                DateTime queuedAt)
            {
                try
                {
                    long elapsedMs = (long)(DateTime.UtcNow - queuedAt).TotalMilliseconds;
                    var data = target == null ? null : target.Data;
                    string dataIsDead = data == null ? "null" : data.IsDead.ToString();
                    string disconnected = data == null ? "null" : data.Disconnected.ToString();
                    var state = PlayerState.GetByPlayerId(playerId);
                    string playerStateIsDead = state == null ? "null" : state.IsDead.ToString();

                    Logger.Info(
                        $"Stage={stage} | MeetingGeneration={generation} | PlayerId={playerId} | ClientId={clientId} | " +
                        $"ElapsedMs={elapsedMs} | IsMeeting={GameStates.IsMeeting} | " +
                        $"ExileControllerExists={ExileController.Instance != null} | " +
                        $"AntiBlackoutIsCached={AntiBlackout.IsCached} | IsInTask={GameStates.IsInTask} | " +
                        $"DataIsDead={dataIsDead} | PlayerStateIsDead={playerStateIsDead} | " +
                        $"Disconnected={disconnected}",
                        Marker);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    [HarmonyPatch(typeof(ExileController), nameof(ExileController.Begin))]
    class ExileControllerBeginDisplayRestorePermanentPatch
    {
        private static bool IsRestoredBegin;

        public static bool Prefix(ExileController __instance, ExileController.InitProperties init)
        {
            if (!AmongUsClient.Instance.AmHost
                || !AntiBlackout.OverrideExiledPlayer)
            {
                return true;
            }

            var actualExiled = ExileControllerWrapUpPatch.AntiBlackout_LastExiled;
            if (actualExiled == null)
            {
                return true;
            }

            var displayOutfit = Camouflage.PlayerSkins.TryGetValue(actualExiled.PlayerId, out var cachedOutfit)
                ? cachedOutfit
                : actualExiled.DefaultOutfit;

            if (IsRestoredBegin)
            {
                // EXILE_DISPLAY_HOST_RESTORE_PERMANENT: ホストの追放表示だけを実際の対象へ復元
                __instance.completeString = string.Format(
                    Translator.GetString(StringNames.ExileTextNonConfirm),
                    displayOutfit.PlayerName);
                return true;
            }

            var restoredInit = init;
            restoredInit.networkedPlayer = actualExiled;
            restoredInit.outfit = displayOutfit;
            restoredInit.voteTie = false;

            IsRestoredBegin = true;
            try
            {
                __instance.Begin(restoredInit);
            }
            finally
            {
                IsRestoredBegin = false;
            }
            return false;
        }

        // SCALES_TIE_EXILE_DISPLAY_PERMANENT:
        // ホストはcarrierを実追放者にせず、表示文だけを天秤専用結果へ上書きする。
        public static void Postfix(ExileController __instance)
        {
            if (!AmongUsClient.Instance.AmHost)
                return;

            if (Scales.TryGetTieExileDisplayPermanent(out var displayText))
            {
                __instance.completeString = displayText;
                Logger.Info(
                    $"Host completeString override | Text={displayText}",
                    "ScalesTieExileDisplayPermanent");
            }
        }
    }
    //static void WrapUpFinalizer(NetworkedPlayerInfo exiled)
    //{
    //    //WrapUpPostfixで例外が発生しても、この部分だけは確実に実行されます。
    //    if (AmongUsClient.Instance.AmHost)
    //    {
    //        _ = new LateTask(() =>
    //        {
    //            exiled = AntiBlackout_LastExiled;
    //            AntiBlackout_LastExiled = null;
    //            AntiBlackout.SendGameData();
    //            if (AntiBlackout.OverrideExiledPlayer && // 追放対象が上書きされる状態 (上書きされない状態なら実行不要)
    //                exiled != null && //exiledがnullでない
    //                exiled.Object != null) //exiled.Objectがnullでない
    //            {
    //                exiled.Object.RpcExileV2();
    //            }
    //        }, 0.5f, "Restore IsDead Task");
    //        _ = new LateTask(() =>
    //        {
    //            Main.AfterMeetingDeathPlayers.Do(x =>
    //            {
    //                REIKAITENSOU(x.Key, x.Value);
    //            });
    //            Main.AfterMeetingDeathPlayers.Clear();
    //        }, 0.5f, "AfterMeetingDeathPlayers Task");
    //    }

    //    GameStates.AlreadyDied |= !Utils.IsAllAlive;
    //    RemoveDisableDevicesPatch.UpdateDisableDevices();
    //    SoundManager.Instance.ChangeAmbienceVolume(DataManager.Settings.Audio.AmbienceVolume);
    //    Logger.Info("タスクフェイズ開始", "Phase");
    //    Badger.MeetingEndCheck();
    //    Tiikawa.MeetingEndCheck();
    //}



    [HarmonyPatch(typeof(PbExileController), nameof(PbExileController.PlayerSpin))]
    class PolusExileHatFixPatch
    {
        public static void Prefix(PbExileController __instance)
        {
            __instance.Player.cosmetics.hat.transform.localPosition = new(-0.2f, 0.6f, 1.1f);
        }
    }
}
