using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AmongUs.GameOptions;
using Hazel;

using TownOfHostForE.Attributes;
using TownOfHostForE.Modules;
using TownOfHostForE.Roles.Animals;
using TownOfHostForE.Roles.Neutral;

namespace TownOfHostForE
{
    public static class AntiBlackout
    {
        ///<summary>
        ///追放処理を上書きするかどうか
        ///</summary>
        public static bool OverrideExiledPlayer => Options.NoGameEnd.GetBool() || Jackal.RoleInfo.IsEnable || AnimalsIsEnable();
        public static bool IsCached { get; private set; } = false;
        private static Dictionary<byte, (bool isDead, bool Disconnected)> isDeadCache = new();
        private readonly static LogHandler logger = Logger.Handler("AntiBlackout");

        // VIEWER_NATIVE_TOPOLOGY_LIFECYCLE_AB_V27:
        // Remote Vanilla viewerごとのNative Role topologyを、
        // meeting/task lifecycleで安全域へ保つA/B層。
        // Host canonical Role / CustomRole / VanillaIntroRoleMapは変更しない。
        private const string ViewerNativeTopologyLifecycleAbV27Marker =
            "VIEWER_NATIVE_TOPOLOGY_LIFECYCLE_AB_V27";

        private sealed class ViewerNativeTopologyLeaseAbV27
        {
            internal byte ViewerId;
            internal int ViewerClientId;
            internal uint MeetingGeneration;
            internal bool MeetingSelfSentinelArmed;
            internal byte MeetingSentinelId = byte.MaxValue;
            internal bool EndMeetingBarrierApplied;
            // DEAD_REMOTE_VANILLA_SKIP_BARRIER_GUARD_AB_V1:
            // Capture the viewer's authoritative death state before AntiBlackout.SetIsDead()
            // temporarily rewrites GameData.IsDead=false for every player.
            internal bool WasDeadBeforeEndMeetingBarrier;
            internal byte PersistentSentinelId = byte.MaxValue;
            internal bool PersistentSentinelWireOverride;
        }

        private static readonly Dictionary<byte, ViewerNativeTopologyLeaseAbV27>
            ViewerNativeTopologyLeasesAbV27 = new();

        private static bool IsNativeImpostorFamilyAbV27(RoleTypes role)
        {
            // VIEWER_NATIVE_TOPOLOGY_OUTCOME_BARRIER_AB_V28:
            // Current interop does not expose RoleTypes.Viper as a compile-time symbol.
            // Existing TOH4E safety paths therefore identify it by enum ToString().
            return role is RoleTypes.Impostor
                or RoleTypes.Shapeshifter
                or RoleTypes.Phantom
                || role.ToString() == "Viper";
        }

        private static bool IsRemoteVanillaViewerAbV27(PlayerControl viewer)
        {
            return viewer != null &&
                   viewer.Data != null &&
                   !viewer.Data.Disconnected &&
                   !viewer.AmOwner &&
                   !viewer.IsModClient();
        }

        private static ViewerNativeTopologyLeaseAbV27
            GetOrCreateViewerNativeTopologyLeaseAbV27(PlayerControl viewer)
        {
            var viewerClientId = viewer.GetClientId();

            if (!ViewerNativeTopologyLeasesAbV27.TryGetValue(
                    viewer.PlayerId,
                    out var lease) ||
                lease.ViewerClientId != viewerClientId)
            {
                lease = new ViewerNativeTopologyLeaseAbV27
                {
                    ViewerId = viewer.PlayerId,
                    ViewerClientId = viewerClientId,
                };

                ViewerNativeTopologyLeasesAbV27[viewer.PlayerId] = lease;
            }

            return lease;
        }

        private static bool TryGetViewerNativeRoleEntriesAbV27(
            PlayerControl viewer,
            List<(PlayerControl Target, RoleTypes Role)> entries)
        {
            entries.Clear();

            foreach (var target in Main.AllPlayerControls)
            {
                if (target == null ||
                    target.Data == null ||
                    target.Data.Disconnected)
                {
                    continue;
                }

                if (!SelectRolesPatch.TryGetExileDisplayRoleViewPermanent(
                        viewer.PlayerId,
                        target.PlayerId,
                        out var role))
                {
                    Logger.Info(
                        $"Stage=MAP_INCOMPLETE | ViewerId={viewer.PlayerId} | " +
                        $"ViewerClientId={viewer.GetClientId()} | TargetId={target.PlayerId}",
                        ViewerNativeTopologyLifecycleAbV27Marker);
                    entries.Clear();
                    return false;
                }

                entries.Add((target, role));
            }

            if (entries.Count < 3 || entries.Count > 15)
            {
                Logger.Info(
                    $"Stage=CONNECTED_COUNT_UNSUPPORTED | ViewerId={viewer.PlayerId} | " +
                    $"ViewerClientId={viewer.GetClientId()} | Connected={entries.Count}",
                    ViewerNativeTopologyLifecycleAbV27Marker);
                entries.Clear();
                return false;
            }

            return true;
        }

        // VIPER_VANILLA_DISSOLVE_BRIDGE_AB_V1:
        // A successful native Viper kill needs the killer to be Viper on the
        // receiving Vanilla client only while that client builds its local corpse.
        // Return the exact viewer-effective role so the temporary wire role can
        // be restored without changing the persistent blackout topology.
        internal static bool TryGetViewerNativeRoleForViperDissolveBridgeAbV1(
            PlayerControl viewer,
            PlayerControl target,
            out RoleTypes role)
        {
            role = RoleTypes.Crewmate;

            if (!AmongUsClient.Instance.AmHost ||
                !GameStates.IsOnlineGame ||
                !GameStates.IsInTask ||
                !IsRemoteVanillaViewerAbV27(viewer) ||
                target == null ||
                target.Data == null ||
                target.Data.Disconnected)
            {
                return false;
            }

            if (!SelectRolesPatch.TryGetExileDisplayRoleViewPermanent(
                    viewer.PlayerId,
                    target.PlayerId,
                    out role))
            {
                return false;
            }

            var lease = GetOrCreateViewerNativeTopologyLeaseAbV27(viewer);

            if (lease.MeetingSelfSentinelArmed &&
                viewer.PlayerId == target.PlayerId)
            {
                role = RoleTypes.Impostor;
            }

            if (lease.PersistentSentinelWireOverride &&
                lease.PersistentSentinelId == target.PlayerId)
            {
                role = RoleTypes.Impostor;
            }

            return true;
        }

        private static List<byte> GetEffectiveAliveNativeImpostorIdsAbV27(
            List<(PlayerControl Target, RoleTypes Role)> entries,
            ViewerNativeTopologyLeaseAbV27 lease)
        {
            var ids = new List<byte>();

            foreach (var entry in entries)
            {
                if (!entry.Target.IsAlive())
                    continue;

                if (IsNativeImpostorFamilyAbV27(entry.Role) &&
                    !ids.Contains(entry.Target.PlayerId))
                {
                    ids.Add(entry.Target.PlayerId);
                }
            }

            if (lease.PersistentSentinelWireOverride &&
                lease.PersistentSentinelId != byte.MaxValue &&
                !ids.Contains(lease.PersistentSentinelId))
            {
                foreach (var entry in entries)
                {
                    if (entry.Target.PlayerId == lease.PersistentSentinelId &&
                        entry.Target.IsAlive())
                    {
                        ids.Add(entry.Target.PlayerId);
                        break;
                    }
                }
            }

            return ids;
        }

        private static PlayerControl? FindPersistentSentinelCandidateAbV27(
            PlayerControl viewer,
            byte excludedPlayerId,
            byte preferredPlayerId = byte.MaxValue)
        {
            PlayerControl? preferred = null;
            PlayerControl? host = null;
            PlayerControl? fallback = null;

            foreach (var candidate in Main.AllPlayerControls)
            {
                if (candidate == null ||
                    candidate.Data == null ||
                    candidate.Data.Disconnected ||
                    !candidate.IsAlive() ||
                    candidate.PlayerId == viewer.PlayerId ||
                    candidate.PlayerId == excludedPlayerId ||
                    Main.AfterMeetingDeathPlayers.ContainsKey(candidate.PlayerId))
                {
                    continue;
                }

                if (candidate.PlayerId == preferredPlayerId)
                    preferred = candidate;

                if (candidate.AmOwner)
                    host = candidate;

                if (fallback == null ||
                    candidate.PlayerId < fallback.PlayerId)
                {
                    fallback = candidate;
                }
            }

            return preferred ?? host ?? fallback;
        }

        private static bool TrySendViewerNativeRolePlanAbV27(
            PlayerControl viewer,
            List<(PlayerControl Target, RoleTypes Role)> plan,
            string stage)
        {
            if (plan.Count == 0)
                return true;
            if (!viewer.TryGetClientId(out var clientId)) return false;

            CustomRpcSender? sender = null;

            try
            {
                sender = CustomRpcSender.Create(
                    "Viewer Native Topology AB V27 " + stage,
                    Hazel.SendOption.Reliable);

                sender.StartMessage(clientId);

                foreach (var entry in plan)
                {
                    sender.StartRpc(entry.Target.NetId, RpcCalls.SetRole)
                        .Write((ushort)entry.Role)
                        .Write(true)
                        .EndRpc();
                }

                sender.EndMessage();
                sender.SendMessage();
                TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.Plan(viewer, plan, stage));

                Logger.Info(
                    $"Stage={stage} | ViewerId={viewer.PlayerId} | " +
                    $"ViewerClientId={viewer.GetClientId()} | RPCs={plan.Count}",
                    ViewerNativeTopologyLifecycleAbV27Marker);

                return true;
            }
            catch (Exception ex)
            {
                logger.Exception(ex);

                if (sender?.CurrentState is not CustomRpcSender.State.Finished)
                    sender?.stream.Recycle();

                Logger.Info(
                    $"Stage={stage}_FAILED | ViewerId={viewer.PlayerId} | " +
                    $"ViewerClientId={viewer.GetClientId()} | RPCs={plan.Count}",
                    ViewerNativeTopologyLifecycleAbV27Marker);

                return false;
            }
        }


        // VIEWER_NATIVE_TOPOLOGY_OUTCOME_BARRIER_AB_V28:
        // Vote result is frozen first. Then each connected remote Vanilla viewer
        // receives exactly one non-exiled Native Impostor sentinel and the rest Crew.
        // VotingComplete is delayed by MeetingVoteManager for 0.5s so client CoSetRole
        // has time to settle before ExileController.WrapUp evaluates native topology.
        private static PlayerControl? FindMeetingSentinelCandidateAbV28(
            PlayerControl viewer,
            byte exiledPlayerId)
        {
            PlayerControl? hostNonSelf = null;
            PlayerControl? nonSelf = null;
            PlayerControl? self = null;

            foreach (var candidate in Main.AllPlayerControls)
            {
                if (candidate == null ||
                    candidate.Data == null ||
                    candidate.Data.Disconnected ||
                    candidate.PlayerId == exiledPlayerId)
                {
                    continue;
                }

                if (candidate.PlayerId == viewer.PlayerId)
                {
                    self = candidate;
                    continue;
                }

                if (candidate.AmOwner)
                    hostNonSelf = candidate;

                if (nonSelf == null ||
                    candidate.PlayerId < nonSelf.PlayerId)
                {
                    nonSelf = candidate;
                }
            }

            return hostNonSelf ?? nonSelf ?? self;
        }

        private static List<(PlayerControl Target, RoleTypes Role)>
            BuildMeetingSafePlanAbV28(
                List<(PlayerControl Target, RoleTypes Role)> entries,
                byte sentinelId)
        {
            var plan =
                new List<(PlayerControl Target, RoleTypes Role)>();

            foreach (var entry in entries)
            {
                plan.Add((
                    entry.Target,
                    entry.Target.PlayerId == sentinelId
                        ? RoleTypes.Impostor
                        : RoleTypes.Crewmate));
            }

            return plan;
        }

        public static bool TryPrepareViewerNativeEndMeetingBarrierAbV28(
            NetworkedPlayerInfo? exiled,
            bool isTie,
            out int preparedViewerCount)
        {
            preparedViewerCount = 0;

            if (!AmongUsClient.Instance.AmHost ||
                !GameStates.IsOnlineGame ||
                !OverrideExiledPlayer)
            {
                return false;
            }

            var exiledId = exiled?.PlayerId ?? byte.MaxValue;

            foreach (var viewer in Main.AllPlayerControls)
            {
                if (!IsRemoteVanillaViewerAbV27(viewer))
                    continue;

                var lease = GetOrCreateViewerNativeTopologyLeaseAbV27(viewer);
                lease.EndMeetingBarrierApplied = false;
                lease.MeetingSentinelId = byte.MaxValue;
                lease.MeetingGeneration =
                    MeetingVoteManager.H4MeetingGeneration;

                // DEAD_REMOTE_VANILLA_SKIP_BARRIER_GUARD_AB_V1:
                // A viewer who was already dead before this meeting outcome must keep the
                // native Ghost role it already owns. The V28 safe plan rewrites every
                // non-sentinel target to Crewmate; combining that with SetIsDead()'s
                // temporary IsDead=false creates a Ghost -> Crewmate/alive -> Ghost
                // transition on the dead Vanilla client. Preserve the pre-meeting ghost
                // RoleType and let WrapUp restore authoritative GameData normally.
                lease.WasDeadBeforeEndMeetingBarrier =
                    viewer.Data != null &&
                    (viewer.Data.IsDead ||
                     !viewer.IsAlive() ||
                     Main.AfterMeetingDeathPlayers.ContainsKey(viewer.PlayerId));

                if (lease.WasDeadBeforeEndMeetingBarrier)
                {
                    Logger.Info(
                        $"Stage=END_BARRIER_PREDEAD_VIEWER_SKIPPED_AB_V1 | ViewerId={viewer.PlayerId} | " +
                        $"ViewerClientId={viewer.GetClientId()} | ExiledId={exiledId} | Tie={isTie}",
                        ViewerNativeTopologyLifecycleAbV27Marker);
                    continue;
                }

                // 3-player real exile where this Vanilla viewer is the exiled player
                // retains the existing V6 specialized self/fallback path. For the
                // surviving viewers the outcome barrier is still useful.
                var connectedCount = Main.AllPlayerControls.Count(
                    x => x != null &&
                         x.Data != null &&
                         !x.Data.Disconnected);

                if (connectedCount == 3 &&
                    exiled != null &&
                    !isTie &&
                    viewer.PlayerId == exiledId)
                {
                    Logger.Info(
                        $"Stage=END_BARRIER_3P_EXILED_VIEWER_LEGACY | ViewerId={viewer.PlayerId} | " +
                        $"ViewerClientId={viewer.GetClientId()} | ExiledId={exiledId}",
                        ViewerNativeTopologyLifecycleAbV27Marker);
                    continue;
                }

                var entries =
                    new List<(PlayerControl Target, RoleTypes Role)>();

                if (!TryGetViewerNativeRoleEntriesAbV27(viewer, entries))
                    continue;

                var sentinel =
                    FindMeetingSentinelCandidateAbV28(
                        viewer,
                        exiledId);

                if (sentinel == null)
                {
                    Logger.Info(
                        $"Stage=END_BARRIER_NO_SENTINEL | ViewerId={viewer.PlayerId} | " +
                        $"ViewerClientId={viewer.GetClientId()} | ExiledId={exiledId} | " +
                        $"Connected={entries.Count}",
                        ViewerNativeTopologyLifecycleAbV27Marker);
                    continue;
                }

                var plan =
                    BuildMeetingSafePlanAbV28(
                        entries,
                        sentinel.PlayerId);

                if (!TrySendViewerNativeRolePlanAbV27(
                        viewer,
                        plan,
                        "END_BARRIER_TOPOLOGY_SENT"))
                {
                    continue;
                }

                lease.MeetingGeneration =
                    MeetingVoteManager.H4MeetingGeneration;
                lease.MeetingSelfSentinelArmed = false;
                lease.MeetingSentinelId = sentinel.PlayerId;
                lease.EndMeetingBarrierApplied = true;
                lease.PersistentSentinelId = byte.MaxValue;
                lease.PersistentSentinelWireOverride = false;
                preparedViewerCount++;

                Logger.Info(
                    $"Stage=END_BARRIER_PREPARED | ViewerId={viewer.PlayerId} | " +
                    $"ViewerClientId={viewer.GetClientId()} | SentinelId={sentinel.PlayerId} | " +
                    $"ExiledId={exiledId} | Tie={isTie} | Connected={entries.Count} | " +
                    $"BarrierSeconds=0.5",
                    ViewerNativeTopologyLifecycleAbV27Marker);
            }

            return preparedViewerCount > 0;
        }

        public static bool IsViewerNativeEndMeetingBarrierAppliedAbV28(
            byte viewerId)
        {
            return ViewerNativeTopologyLeasesAbV27.TryGetValue(
                       viewerId,
                       out var lease) &&
                   lease.MeetingGeneration ==
                       MeetingVoteManager.H4MeetingGeneration &&
                   lease.EndMeetingBarrierApplied &&
                   lease.MeetingSentinelId != byte.MaxValue;
        }

        public static void ReassertViewerNativeEndMeetingTopologyAfterSetIsDeadAbV28()
        {
            if (!AmongUsClient.Instance.AmHost ||
                !GameStates.IsOnlineGame ||
                !OverrideExiledPlayer)
            {
                return;
            }

            foreach (var viewer in Main.AllPlayerControls)
            {
                if (!IsRemoteVanillaViewerAbV27(viewer) ||
                    !ViewerNativeTopologyLeasesAbV27.TryGetValue(
                        viewer.PlayerId,
                        out var lease) ||
                    lease.MeetingGeneration !=
                        MeetingVoteManager.H4MeetingGeneration)
                {
                    continue;
                }

                // DEAD_REMOTE_VANILLA_SKIP_BARRIER_GUARD_AB_V1:
                // Do not re-apply the living meeting topology after SetIsDead has
                // temporarily rewritten the viewer's GameData death bit.
                if (lease.WasDeadBeforeEndMeetingBarrier)
                {
                    Logger.Info(
                        $"Stage=POST_SETISDEAD_PREDEAD_VIEWER_SKIPPED_AB_V1 | ViewerId={viewer.PlayerId} | " +
                        $"ViewerClientId={viewer.GetClientId()}",
                        ViewerNativeTopologyLifecycleAbV27Marker);
                    continue;
                }

                if (!lease.EndMeetingBarrierApplied ||
                    lease.MeetingSentinelId == byte.MaxValue)
                {
                    continue;
                }

                var entries =
                    new List<(PlayerControl Target, RoleTypes Role)>();

                if (!TryGetViewerNativeRoleEntriesAbV27(viewer, entries))
                    continue;

                var sentinelFound = entries.Any(
                    x => x.Target.PlayerId == lease.MeetingSentinelId);

                if (!sentinelFound)
                {
                    Logger.Info(
                        $"Stage=POST_SETISDEAD_SENTINEL_MISSING | ViewerId={viewer.PlayerId} | " +
                        $"ViewerClientId={viewer.GetClientId()} | SentinelId={lease.MeetingSentinelId}",
                        ViewerNativeTopologyLifecycleAbV27Marker);
                    continue;
                }

                var plan =
                    BuildMeetingSafePlanAbV28(
                        entries,
                        lease.MeetingSentinelId);

                TrySendViewerNativeRolePlanAbV27(
                    viewer,
                    plan,
                    "POST_SETISDEAD_OUTCOME_REASSERT");
            }
        }

        public static void ReassertViewerNativePersistentSentinelsAfterActualDataAbV28()
        {
            if (!AmongUsClient.Instance.AmHost ||
                !GameStates.IsOnlineGame ||
                !OverrideExiledPlayer)
            {
                return;
            }

            foreach (var viewer in Main.AllPlayerControls)
            {
                if (!IsRemoteVanillaViewerAbV27(viewer) ||
                    !ViewerNativeTopologyLeasesAbV27.TryGetValue(
                        viewer.PlayerId,
                        out var lease) ||
                    !lease.PersistentSentinelWireOverride)
                {
                    continue;
                }

                // DEAD_REMOTE_VANILLA_POST_ACTUALDATA_NO_REASSERT_V1:
                // Canonical dead/ghost state wins after WrapUp.
                // Never reassert a living Native Impostor sentinel for a
                // dead Vanilla viewer after actual GameData is restored.
                if (viewer.Data.IsDead ||
                    !viewer.IsAlive() ||
                    Main.AfterMeetingDeathPlayers.ContainsKey(viewer.PlayerId))
                {
                    Logger.Info(
                        $"Stage=POST_ACTUALDATA_DEAD_VIEWER_RELEASE | ViewerId={viewer.PlayerId} | " +
                        $"ViewerClientId={viewer.GetClientId()} | OldSentinelId={lease.PersistentSentinelId}",
                        ViewerNativeTopologyLifecycleAbV27Marker);

                    lease.PersistentSentinelId = byte.MaxValue;
                    lease.PersistentSentinelWireOverride = false;
                    continue;
                }

                var sentinel =
                    Utils.GetPlayerById(lease.PersistentSentinelId);

                if (sentinel == null ||
                    sentinel.Data == null ||
                    sentinel.Data.Disconnected ||
                    !sentinel.IsAlive() ||
                    Main.AfterMeetingDeathPlayers.ContainsKey(
                        sentinel.PlayerId))
                {
                    sentinel =
                        FindPersistentSentinelCandidateAbV27(
                            viewer,
                            lease.PersistentSentinelId,
                            byte.MaxValue);

                    if (sentinel == null)
                    {
                        Logger.Info(
                            $"Stage=POST_ACTUALDATA_NO_SENTINEL | ViewerId={viewer.PlayerId} | " +
                            $"ViewerClientId={viewer.GetClientId()} | OldSentinelId={lease.PersistentSentinelId}",
                            ViewerNativeTopologyLifecycleAbV27Marker);
                        lease.PersistentSentinelId = byte.MaxValue;
                        lease.PersistentSentinelWireOverride = false;
                        continue;
                    }

                    lease.PersistentSentinelId = sentinel.PlayerId;
                }

                var plan =
                    new List<(PlayerControl Target, RoleTypes Role)>
                    {
                        (sentinel, RoleTypes.Impostor),
                    };

                if (TrySendViewerNativeRolePlanAbV27(
                        viewer,
                        plan,
                        "POST_ACTUALDATA_PERSISTENT_REASSERT"))
                {
                    Logger.Info(
                        $"Stage=POST_ACTUALDATA_PERSISTENT_STATE | ViewerId={viewer.PlayerId} | " +
                        $"ViewerClientId={viewer.GetClientId()} | SentinelId={sentinel.PlayerId}",
                        ViewerNativeTopologyLifecycleAbV27Marker);
                }
            }
        }

        public static void TryRotateViewerNativeSentinelBeforeDeathAbV28(
            PlayerControl target,
            string reason)
        {
            if (!AmongUsClient.Instance.AmHost ||
                !GameStates.IsOnlineGame ||
                !OverrideExiledPlayer ||
                !GameStates.IsInTask ||
                IsCached ||
                target == null ||
                target.Data == null)
            {
                return;
            }

            foreach (var viewer in Main.AllPlayerControls)
            {
                if (!IsRemoteVanillaViewerAbV27(viewer))
                    continue;

                var lease =
                    GetOrCreateViewerNativeTopologyLeaseAbV27(viewer);

                var targetWasEffectiveImpostor =
                    lease.PersistentSentinelWireOverride &&
                    lease.PersistentSentinelId == target.PlayerId;

                if (SelectRolesPatch.TryGetExileDisplayRoleViewPermanent(
                        viewer.PlayerId,
                        target.PlayerId,
                        out var targetRole) &&
                    IsNativeImpostorFamilyAbV27(targetRole))
                {
                    targetWasEffectiveImpostor = true;
                }

                if (!targetWasEffectiveImpostor)
                    continue;

                var remainingEffectiveImpostors =
                    new HashSet<byte>();

                foreach (var candidate in Main.AllPlayerControls)
                {
                    if (candidate == null ||
                        candidate.Data == null ||
                        candidate.Data.Disconnected ||
                        candidate.PlayerId == target.PlayerId ||
                        !candidate.IsAlive() ||
                        Main.AfterMeetingDeathPlayers.ContainsKey(
                            candidate.PlayerId))
                    {
                        continue;
                    }

                    if (SelectRolesPatch.TryGetExileDisplayRoleViewPermanent(
                            viewer.PlayerId,
                            candidate.PlayerId,
                            out var role) &&
                        IsNativeImpostorFamilyAbV27(role))
                    {
                        remainingEffectiveImpostors.Add(
                            candidate.PlayerId);
                    }
                }

                if (lease.PersistentSentinelWireOverride &&
                    lease.PersistentSentinelId != byte.MaxValue &&
                    lease.PersistentSentinelId != target.PlayerId)
                {
                    var currentWireSentinel =
                        Utils.GetPlayerById(
                            lease.PersistentSentinelId);

                    if (currentWireSentinel != null &&
                        currentWireSentinel.Data != null &&
                        !currentWireSentinel.Data.Disconnected &&
                        currentWireSentinel.IsAlive() &&
                        !Main.AfterMeetingDeathPlayers.ContainsKey(
                            currentWireSentinel.PlayerId))
                    {
                        remainingEffectiveImpostors.Add(
                            currentWireSentinel.PlayerId);
                    }
                }

                if (remainingEffectiveImpostors.Count > 0)
                    continue;

                var replacement =
                    FindPersistentSentinelCandidateAbV27(
                        viewer,
                        target.PlayerId,
                        byte.MaxValue);

                if (replacement == null)
                {
                    Logger.Info(
                        $"Stage=TASK_DEATH_ROTATE_NO_TARGET | ViewerId={viewer.PlayerId} | " +
                        $"ViewerClientId={viewer.GetClientId()} | TargetId={target.PlayerId} | " +
                        $"Reason={reason}",
                        ViewerNativeTopologyLifecycleAbV27Marker);
                    continue;
                }

                var plan =
                    new List<(PlayerControl Target, RoleTypes Role)>
                    {
                        (replacement, RoleTypes.Impostor),
                    };

                if (!TrySendViewerNativeRolePlanAbV27(
                        viewer,
                        plan,
                        "TASK_DEATH_SENTINEL_ROTATED"))
                {
                    continue;
                }

                lease.PersistentSentinelId =
                    replacement.PlayerId;
                lease.PersistentSentinelWireOverride = true;

                Logger.Info(
                    $"Stage=TASK_DEATH_ROTATED | ViewerId={viewer.PlayerId} | " +
                    $"ViewerClientId={viewer.GetClientId()} | OldSentinelId={target.PlayerId} | " +
                    $"NewSentinelId={replacement.PlayerId} | Reason={reason}",
                    ViewerNativeTopologyLifecycleAbV27Marker);
            }
        }


        public static void PrepareViewerNativeTopologyForMeetingAbV27()
        {
            if (!AmongUsClient.Instance.AmHost ||
                !GameStates.IsOnlineGame)
            {
                return;
            }

            // VIEWER_NATIVE_TOPOLOGY_OUTCOME_BARRIER_AB_V28:
            // Meeting start no longer mutates Native roles. Outcome-aware topology
            // is sent only after CountVotes has frozen the actual exile result.
            foreach (var lease in ViewerNativeTopologyLeasesAbV27.Values)
            {
                lease.MeetingGeneration =
                    MeetingVoteManager.H4MeetingGeneration;
                lease.MeetingSelfSentinelArmed = false;
                lease.MeetingSentinelId = byte.MaxValue;
                lease.EndMeetingBarrierApplied = false;
                lease.WasDeadBeforeEndMeetingBarrier = false;
            }

            Logger.Info(
                $"Stage=MEETING_BEGIN_TRACK_ONLY | MeetingGeneration={MeetingVoteManager.H4MeetingGeneration}",
                ViewerNativeTopologyLifecycleAbV27Marker);
        }

        public static void FinalizeViewerNativeTopologyAfterWrapUpAbV27(
            NetworkedPlayerInfo? exiled,
            bool synchronousExiledSelfGhostSent)
        {
            if (!AmongUsClient.Instance.AmHost ||
                !GameStates.IsOnlineGame ||
                !OverrideExiledPlayer)
            {
                return;
            }

            var exiledId =
                exiled?.PlayerId ?? byte.MaxValue;

            foreach (var viewer in Main.AllPlayerControls)
            {
                if (!IsRemoteVanillaViewerAbV27(viewer))
                    continue;

                var lease =
                    GetOrCreateViewerNativeTopologyLeaseAbV27(viewer);

                var entries =
                    new List<(PlayerControl Target, RoleTypes Role)>();

                if (!TryGetViewerNativeRoleEntriesAbV27(
                        viewer,
                        entries))
                {
                    continue;
                }

                var authoritativeAliveImpostors =
                    new List<byte>();

                foreach (var entry in entries)
                {
                    if (!entry.Target.IsAlive() ||
                        Main.AfterMeetingDeathPlayers.ContainsKey(
                            entry.Target.PlayerId))
                    {
                        continue;
                    }

                    if (IsNativeImpostorFamilyAbV27(entry.Role))
                    {
                        authoritativeAliveImpostors.Add(
                            entry.Target.PlayerId);
                    }
                }

                PlayerControl? desiredPersistent = null;
                byte desiredPersistentId = byte.MaxValue;
                var desiredPersistentWire = false;

                // DEAD_REMOTE_VANILLA_PERSISTENT_SENTINEL_GUARD_V1:
                // Dead/Ghost Remote Vanilla viewers must not retain a
                // task-phase Native Impostor sentinel after meeting WrapUp.
                var viewerIsAliveAfterWrapUp =
                    viewer.Data != null &&
                    !viewer.Data.IsDead &&
                    viewer.IsAlive() &&
                    !Main.AfterMeetingDeathPlayers.ContainsKey(viewer.PlayerId);

                if (viewerIsAliveAfterWrapUp &&
                    authoritativeAliveImpostors.Count == 0)
                {
                    desiredPersistent =
                        FindPersistentSentinelCandidateAbV27(
                            viewer,
                            exiledId,
                            lease.PersistentSentinelId);

                    if (desiredPersistent != null)
                    {
                        desiredPersistentId =
                            desiredPersistent.PlayerId;
                        desiredPersistentWire = true;
                    }
                }

                var plan =
                    new List<(PlayerControl Target, RoleTypes Role)>();

                // Promote replacement first, then restore latest authoritative RoleMap.
                if (desiredPersistentWire &&
                    desiredPersistent != null)
                {
                    plan.Add((
                        desiredPersistent,
                        RoleTypes.Impostor));
                }

                foreach (var entry in entries)
                {
                    // SYNC_SELF_GHOST_FAIL_OPEN_AB_V1:
                    // sync Data->Ghostが成功したactual-exiled Remote Vanilla本人の
                    // self RoleだけをV27 post-WrapUpから除外する。
                    // 失敗時はflag=falseなので従来planを完全維持する。
                    if (synchronousExiledSelfGhostSent &&
                        viewer.PlayerId == exiledId &&
                        entry.Target.PlayerId == exiledId)
                    {
                        Logger.Info(
                            $"Stage=POST_WRAPUP_SELF_ROLE_SUPPRESSED_SYNC_OWNED | " +
                            $"ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | " +
                            $"ExiledId={exiledId} | Role={entry.Role}",
                            ViewerNativeTopologyLifecycleAbV27Marker);

                        continue;
                    }

                    if (desiredPersistentWire &&
                        entry.Target.PlayerId ==
                            desiredPersistentId)
                    {
                        continue;
                    }

                    plan.Add((entry.Target, entry.Role));
                }

                if (!TrySendViewerNativeRolePlanAbV27(
                        viewer,
                        plan,
                        "POST_WRAPUP_RESTORE_HANDOFF"))
                {
                    continue;
                }

                lease.MeetingSelfSentinelArmed = false;
                lease.MeetingSentinelId = byte.MaxValue;
                lease.EndMeetingBarrierApplied = false;
                lease.PersistentSentinelId =
                    desiredPersistentId;
                lease.PersistentSentinelWireOverride =
                    desiredPersistentWire;

                var aliveCount =
                    Main.AllPlayerControls.Count(
                        x => x != null &&
                             x.Data != null &&
                             !x.Data.Disconnected &&
                             x.IsAlive() &&
                             !Main.AfterMeetingDeathPlayers.ContainsKey(
                                 x.PlayerId));

                Logger.Info(
                    $"Stage=POST_WRAPUP_STATE | ViewerId={viewer.PlayerId} | " +
                    $"ViewerClientId={viewer.GetClientId()} | ExiledId={exiledId} | " +
                    $"AuthoritativeAliveImp={authoritativeAliveImpostors.Count} | " +
                    $"AliveAfterPendingExclusion={aliveCount} | " +
                    $"PersistentSentinelId={desiredPersistentId} | " +
                    $"PersistentWire={desiredPersistentWire}",
                    ViewerNativeTopologyLifecycleAbV27Marker);
            }
        }

        public static void TryRotateViewerNativeSentinelBeforeMurderAbV27(
            PlayerControl target)
        {
            TryRotateViewerNativeSentinelBeforeDeathAbV28(
                target,
                "RPC_MURDER_PREFIX");
        }

        public static void TryRotateViewerNativeSentinelOnDisconnectAbV27(
            NetworkedPlayerInfo player)
        {
            if (!AmongUsClient.Instance.AmHost ||
                !GameStates.IsOnlineGame ||
                !OverrideExiledPlayer ||
                !GameStates.IsInTask ||
                IsCached ||
                player == null ||
                player.Object == null)
            {
                return;
            }

            TryRotateViewerNativeSentinelBeforeDeathAbV28(
                player.Object,
                "DISCONNECT");
        }

        private static void ResetViewerNativeTopologyLifecycleAbV27()
        {
            ViewerNativeTopologyLeasesAbV27.Clear();
        }

        private static bool AnimalsIsEnable()
        {
            return Coyote.RoleInfo.IsEnable ||
                   Braki.RoleInfo.IsEnable  ||
                   Nyaoha.RoleInfo.IsEnable  ||
                   //Vulture.RoleInfo.IsEnable  ||
                   //Badger.RoleInfo.IsEnable  ||
                   Leopard.RoleInfo.IsEnable;
        }

        public static void SetIsDead(bool doSend = true, [CallerMemberName] string callerMethodName = "")
        {
            logger.Info($"SetIsDead is called from {callerMethodName}");
            if (IsCached)
            {
                logger.Info("再度SetIsDeadを実行する前に、RestoreIsDeadを実行してください。");
                return;
            }
            isDeadCache.Clear();
            foreach (var info in GameData.Instance.AllPlayers)
            {
                if (info == null) continue;
                isDeadCache[info.PlayerId] = (info.IsDead, info.Disconnected);
                info.IsDead = false;
                info.Disconnected = false;
            }
            IsCached = true;
            if (doSend) SendGameData();
        }
        public static void RestoreIsDead(bool doSend = true, [CallerMemberName] string callerMethodName = "")
        {
            logger.Info($"RestoreIsDead is called from {callerMethodName}");
            foreach (var info in GameData.Instance.AllPlayers)
            {
                if (info == null) continue;
                if (isDeadCache.TryGetValue(info.PlayerId, out var val))
                {
                    info.IsDead = val.isDead;
                    info.Disconnected = val.Disconnected;
                }
            }
            isDeadCache.Clear();
            IsCached = false;
            if (doSend) SendGameData();
        }

        public static void SendGameData([CallerMemberName] string callerMethodName = "")
        {
            logger.Info($"SendGameData is called from {callerMethodName}");
            foreach (var playerinfo in GameData.Instance.AllPlayers)
            {
                MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
                // 書き込み {}は読みやすさのためです。
                writer.StartMessage(5); //0x05 GameData
                {
                    writer.Write(AmongUsClient.Instance.GameId);
                    writer.StartMessage(1); //0x01 Data
                    {
                        writer.WritePacked(playerinfo.NetId);
                        playerinfo.Serialize(writer, true);

                    }
                    writer.EndMessage();
                }
                writer.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(writer);
                writer.Recycle();
            }
        }
        public static void OnDisconnect(NetworkedPlayerInfo player)
        {
            // VIEWER_NATIVE_TOPOLOGY_LIFECYCLE_AB_V27:
            // task phaseでもsole sentinelをbest-effortで交代する。
            TryRotateViewerNativeSentinelOnDisconnectAbV27(player);

            // 実行条件: クライアントがホストである, IsDeadが上書きされている, playerが切断済み
            if (!AmongUsClient.Instance.AmHost || !IsCached || !player.Disconnected) return;
            isDeadCache[player.PlayerId] = (true, true);
            player.IsDead = player.Disconnected = false;
            SendGameData();
        }


        // ExileDisplayRealPermanent:
        // AntiBlackout中でも4-15人の実追放表示を安全に見せるため、
        // 追放者以外の接続者全員を一時的に 1 Impostor + 残り Crewmate にする。
        // 復元値はCustomRoleから推測せず、SelectRolesPatchが保持する
        // viewer x target の実際のRoleTypeだけを使用する。
        private static readonly List<byte> ExileDisplaySafeTargetIdsPermanent = new();
        private static readonly Dictionary<(byte SeerId, byte TargetId), RoleTypes> ExileDisplayRestoreSnapshotPermanent = new();
        private static byte ExileDisplayDummyImpostorIdPermanent = byte.MaxValue;
        private static bool ExileDisplaySafetyPendingPermanent;
        private static bool ExileDisplaySafetyAppliedPermanent;

        // HOST_EXILE_REMOTE_VANILLA_GHOST_SAFETY_ACCESSOR_AB_V1:
        // 実追放Ghost安全化から現在の4-15人安全化適用状態を読むだけ。
        internal static bool IsRealExileDisplaySafetyAppliedPermanent
            => ExileDisplaySafetyAppliedPermanent;

        public static bool TryPrepareRealExileDisplayPermanent(NetworkedPlayerInfo exiled)
        {
            if (!AmongUsClient.Instance.AmHost || exiled == null)
                return false;

            if (ExileDisplaySafetyPendingPermanent || ExileDisplaySafetyAppliedPermanent)
            {
                Logger.Warn("Previous exile safety state is still active. Falling back to null/tie.", "ExileDisplayRealPermanent");
                return false;
            }

            var connectedPlayers = new List<PlayerControl>();
            foreach (var pc in Main.AllPlayerControls)
            {
                if (pc == null ||
                    pc.Data == null ||
                    pc.Data.Disconnected)
                    continue;

                connectedPlayers.Add(pc);
            }

            if (connectedPlayers.Count < 4 || connectedPlayers.Count > 15)
                return false;

            ExileDisplaySafeTargetIdsPermanent.Clear();
            ExileDisplayRestoreSnapshotPermanent.Clear();

            foreach (var pc in connectedPlayers)
            {
                // SetIsDead()は実追放者本人も一時生存扱いに戻すため、
                // RoleType安全化も追放者を含む接続者全員へ適用する。
                ExileDisplaySafeTargetIdsPermanent.Add(pc.PlayerId);
            }

            // 4人ロビーなら一時安全構成は 1 Impostor + 3 Crewmate。
            // real VotingComplete側で追放者が外れた後も 1 + 2 が残る。
            if (ExileDisplaySafeTargetIdsPermanent.Count < 4)
            {
                ExileDisplaySafeTargetIdsPermanent.Clear();
                return false;
            }

            PlayerControl? dummyImpostor = null;

            // 既存の実インポスターを優先し、RoleType変更量を少なくする。
            foreach (var pc in connectedPlayers)
            {
                if (pc.PlayerId == exiled.PlayerId)
                    continue;

                if (pc.IsAlive() && pc.GetCustomRole().IsImpostor())
                {
                    dummyImpostor = pc;
                    break;
                }
            }

            // 実インポスターがいなければ生存者を優先する。
            if (dummyImpostor == null)
            {
                foreach (var pc in connectedPlayers)
                {
                    if (pc.PlayerId == exiled.PlayerId)
                        continue;

                    if (pc.IsAlive())
                    {
                        dummyImpostor = pc;
                        break;
                    }
                }
            }

            // 全員死亡扱い等の特殊状態でも、接続者が4人以上なら最低1人を選べる。
            if (dummyImpostor == null)
            {
                foreach (var pc in connectedPlayers)
                {
                    if (pc.PlayerId != exiled.PlayerId)
                    {
                        dummyImpostor = pc;
                        break;
                    }
                }
            }

            if (dummyImpostor == null)
            {
                ExileDisplaySafeTargetIdsPermanent.Clear();
                return false;
            }

            ExileDisplayDummyImpostorIdPermanent = dummyImpostor.PlayerId;

            // real VotingCompleteを送る前に、全非ホストviewer x 全安全化targetの
            // 正確な復元RoleTypeが揃っていることを必須条件にする。
            foreach (var seer in connectedPlayers)
            {
                if (seer.AmOwner)
                    continue;

                foreach (var targetId in ExileDisplaySafeTargetIdsPermanent)
                {
                    if (!SelectRolesPatch.TryGetExileDisplayRoleViewPermanent(
                            seer.PlayerId,
                            targetId,
                            out var role))
                    {
                        Logger.Warn(
                            $"RoleView incomplete | Seer={seer.PlayerId} | Target={targetId} | fallback=null/tie",
                            "ExileDisplayRealPermanent"
                        );
                        ExileDisplaySafeTargetIdsPermanent.Clear();
                        ExileDisplayRestoreSnapshotPermanent.Clear();
                        ExileDisplayDummyImpostorIdPermanent = byte.MaxValue;
                        return false;
                    }

                    ExileDisplayRestoreSnapshotPermanent[(seer.PlayerId, targetId)] = role;
                }
            }

            // ここではまだRoleType/IsDeadを変更しない。
            // MeetingHud終了時の既存SetIsDead直前に安全RoleTypeを送る。
            ExileDisplaySafetyPendingPermanent = true;
            ExileDisplaySafetyAppliedPermanent = false;

            Logger.Info(
                $"Prepared | Connected={connectedPlayers.Count} | Exiled={exiled.PlayerId} | SafeTargets={ExileDisplaySafeTargetIdsPermanent.Count} | RestoreSnapshot={ExileDisplayRestoreSnapshotPermanent.Count}",
                "ExileDisplayRealPermanent"
            );
                        LogExileRoleBundleTraceAbV15("PREPARED", exiled.PlayerId);
return true;
        }

        // VANILLA_ACTUAL_EXILE_3P_FALLBACK_SAFE_ROLE_AB_V6
        // 3人の実追放remote Vanillaだけに、既存4-15人安全化と同じ
        // official RoleType view (1 Impostor + 2 Crewmate) を準備する。
        // 重要: PREPARED_REAL_TARGETED / real VotingComplete は3人へ拡張しない。
        public static bool TryPrepareThreePlayerFallbackExileSafetyAbV6(NetworkedPlayerInfo exiled)
        {
            if (!AmongUsClient.Instance.AmHost || exiled == null)
                return false;

            if (ExileDisplaySafetyPendingPermanent || ExileDisplaySafetyAppliedPermanent)
            {
                Logger.Warn(
                    "V6 3P fallback safety state is already active. Keeping legacy null/tie fallback.",
                    "VANILLA_ACTUAL_EXILE_3P_FALLBACK_SAFE_ROLE_AB_V6");
                return false;
            }

            var connectedPlayers = new List<PlayerControl>();
            var exiledFound = false;

            foreach (var pc in Main.AllPlayerControls)
            {
                if (pc == null ||
                    pc.Data == null ||
                    pc.Data.Disconnected)
                    continue;

                connectedPlayers.Add(pc);
                if (pc.PlayerId == exiled.PlayerId)
                    exiledFound = true;
            }

            if (connectedPlayers.Count != 3 || !exiledFound)
                return false;

            ExileDisplaySafeTargetIdsPermanent.Clear();
            ExileDisplayRestoreSnapshotPermanent.Clear();

            foreach (var pc in connectedPlayers)
            {
                // SetIsDead()が3人全員を一時aliveにするので全員を安全RoleType対象にする。
                ExileDisplaySafeTargetIdsPermanent.Add(pc.PlayerId);
            }

            if (ExileDisplaySafeTargetIdsPermanent.Count != 3)
            {
                ExileDisplaySafeTargetIdsPermanent.Clear();
                return false;
            }

            PlayerControl? dummyImpostor = null;

            // 実追放者以外の実インポスターを最優先。
            foreach (var pc in connectedPlayers)
            {
                if (pc.PlayerId == exiled.PlayerId)
                    continue;

                if (pc.IsAlive() && pc.GetCustomRole().IsImpostor())
                {
                    dummyImpostor = pc;
                    break;
                }
            }

            // いなければ実追放者以外の生存者。
            if (dummyImpostor == null)
            {
                foreach (var pc in connectedPlayers)
                {
                    if (pc.PlayerId == exiled.PlayerId)
                        continue;

                    if (pc.IsAlive())
                    {
                        dummyImpostor = pc;
                        break;
                    }
                }
            }

            // 特殊状態でも実追放者以外の接続者を1人だけdummy Impostorにする。
            if (dummyImpostor == null)
            {
                foreach (var pc in connectedPlayers)
                {
                    if (pc.PlayerId != exiled.PlayerId)
                    {
                        dummyImpostor = pc;
                        break;
                    }
                }
            }

            if (dummyImpostor == null)
            {
                ExileDisplaySafeTargetIdsPermanent.Clear();
                return false;
            }

            ExileDisplayDummyImpostorIdPermanent = dummyImpostor.PlayerId;

            // 復元は推測禁止。既存viewer x target RoleViewが全件揃う場合だけA/Bを有効にする。
            foreach (var seer in connectedPlayers)
            {
                if (seer.AmOwner)
                    continue;

                foreach (var targetId in ExileDisplaySafeTargetIdsPermanent)
                {
                    if (!SelectRolesPatch.TryGetExileDisplayRoleViewPermanent(
                            seer.PlayerId,
                            targetId,
                            out var role))
                    {
                        Logger.Warn(
                            $"V6 RoleView incomplete | Seer={seer.PlayerId} | Target={targetId} | keep legacy fallback",
                            "VANILLA_ACTUAL_EXILE_3P_FALLBACK_SAFE_ROLE_AB_V6");

                        ExileDisplaySafeTargetIdsPermanent.Clear();
                        ExileDisplayRestoreSnapshotPermanent.Clear();
                        ExileDisplayDummyImpostorIdPermanent = byte.MaxValue;
                        return false;
                    }

                    ExileDisplayRestoreSnapshotPermanent[(seer.PlayerId, targetId)] = role;
                }
            }

            // MeetingHud.OnDestroyの既存順序:
            // ApplyPreparedRealExileSafetyPermanent() → SetIsDead()
            // をそのまま再利用する。
            ExileDisplaySafetyPendingPermanent = true;
            ExileDisplaySafetyAppliedPermanent = false;

            Logger.Info(
                $"Stage=PREPARED | Connected=3 | Exiled={exiled.PlayerId} | DummyImp={ExileDisplayDummyImpostorIdPermanent} | " +
                $"SafeTargets={ExileDisplaySafeTargetIdsPermanent.Count} | RestoreSnapshot={ExileDisplayRestoreSnapshotPermanent.Count}",
                "VANILLA_ACTUAL_EXILE_3P_FALLBACK_SAFE_ROLE_AB_V6");

            return true;
        }

        public static void ApplyPreparedRealExileSafetyPermanent()
        {
            if (!AmongUsClient.Instance.AmHost ||
                !ExileDisplaySafetyPendingPermanent ||
                ExileDisplaySafetyAppliedPermanent)
                return;

            // 重要:
            // 既存MeetingHudPatchのSetIsDead()より先にRoleTypeを安全化する。
            // Reliable順序で「1 Impostor + 残りCrew」→「全員alive GameData」とし、
            // 全員aliveなのにofficial RoleTypeだけ危険、という窓を作らない。
            //
            // Send途中の例外でもWrapUp時に復元対象として扱えるよう、
            // 送信前にAppliedを立てる。
            ExileDisplaySafetyAppliedPermanent = true;
            SendExileDisplaySafeRolesPermanent();
            LogExileRoleBundleTraceAbV15("SAFETY_APPLIED", byte.MaxValue);

            Logger.Info(
                $"Safety sent before SetIsDead | Targets={ExileDisplaySafeTargetIdsPermanent.Count} | DummyImp={ExileDisplayDummyImpostorIdPermanent} | RestoreSnapshot={ExileDisplayRestoreSnapshotPermanent.Count}",
                "ExileDisplayRealPermanent"
            );
        }

        private static PlayerControl? FindExileDisplayPlayerPermanent(byte playerId)
        {
            foreach (var pc in Main.AllPlayerControls)
            {
                if (pc != null && pc.PlayerId == playerId)
                    return pc;
            }

            return null;
        }

        private static void SendExileDisplaySafeRolesPermanent()
        {
            // 全remote clientへ同一安全構成をbroadcastするため、
            // 15人でも最大15 SetRole RPCで済む。
            var sender = CustomRpcSender.Create(
                "ExileDisplayRealPermanent Safety",
                Hazel.SendOption.Reliable
            );
            sender.StartMessage();

            var sentCount = 0;
            foreach (var targetId in ExileDisplaySafeTargetIdsPermanent)
            {
                var target = FindExileDisplayPlayerPermanent(targetId);
                if (target == null)
                {
                    Logger.Warn(
                        $"Safety target missing | Target={targetId}",
                        "ExileDisplayRealPermanent"
                    );
                    continue;
                }

                var safeRole = targetId == ExileDisplayDummyImpostorIdPermanent
                    ? RoleTypes.Impostor
                    : RoleTypes.Crewmate;

                sender.StartRpc(target.NetId, RpcCalls.SetRole)
                    .Write((ushort)safeRole)
                    .Write(true)
                    .EndRpc();

                sentCount++;
            }

            sender.EndMessage();

            if (sentCount > 0)
                sender.SendMessage();

            if (sentCount < 3)
            {
                Logger.Warn(
                    $"Safety role count became insufficient | Sent={sentCount}",
                    "ExileDisplayRealPermanent"
                );
            }
        }

        public static void RestoreRealExileDisplayRolesPermanent()
        {
            if (!AmongUsClient.Instance.AmHost)
                return;

            ExileDisplaySafetyPendingPermanent = false;

            if (!ExileDisplaySafetyAppliedPermanent)
            {
                ClearExileDisplaySafetyStatePermanent();
                return;
            }

            var restoredRpcCount = 0;
            var restoredViewerCount = 0;

            foreach (var seer in Main.AllPlayerControls)
            {
                if (seer == null ||
                    seer.Data == null ||
                    seer.Data.Disconnected ||
                    seer.AmOwner)
                    continue;

                if (!seer.TryGetClientId(out var clientId)) continue;
                var restoreEntries = new List<(PlayerControl Target, RoleTypes Role)>();

                foreach (var targetId in ExileDisplaySafeTargetIdsPermanent)
                {
                    var target = FindExileDisplayPlayerPermanent(targetId);
                    if (target == null)
                        continue;

                    RoleTypes restoreRole;

                    // 会議/追放演出中に別死亡が発生した場合は、
                    // Ghost Role追従済みの最新RoleViewを優先する。
                    if (!SelectRolesPatch.TryGetExileDisplayRoleViewPermanent(
                            seer.PlayerId,
                            targetId,
                            out restoreRole))
                    {
                        if (!ExileDisplayRestoreSnapshotPermanent.TryGetValue(
                                (seer.PlayerId, targetId),
                                out restoreRole))
                        {
                            Logger.Warn(
                                $"Restore RoleView missing | Seer={seer.PlayerId} | Target={targetId}",
                                "ExileDisplayRealPermanent"
                            );
                            continue;
                        }
                    }

                    if (ExileControllerWrapUpPatch.VanillaActualExileCriticalLeaseAbV14A
                        .TryHoldRestore(seer, target, restoreRole))
                    {
                        continue;
                    }

                    restoreEntries.Add((target, restoreRole));
                }

                if (restoreEntries.Count == 0)
                    continue;

                var sender = CustomRpcSender.Create(
                    "ExileDisplayRealPermanent Restore",
                    Hazel.SendOption.Reliable
                );
                sender.StartMessage(clientId);

                foreach (var entry in restoreEntries)
                {
                    sender.StartRpc(entry.Target.NetId, RpcCalls.SetRole)
                        .Write((ushort)entry.Role)
                        .Write(true)
                        .EndRpc();

                    restoredRpcCount++;
                }

                sender.EndMessage();
                sender.SendMessage();
                restoredViewerCount++;
            }

            Logger.Info(
                $"Restore | Viewers={restoredViewerCount} | RPCs={restoredRpcCount} | Targets={ExileDisplaySafeTargetIdsPermanent.Count}",
                "ExileDisplayRealPermanent"
            );

                        LogExileRoleBundleTraceAbV15("RESTORE_COMPLETE", byte.MaxValue);
ClearExileDisplaySafetyStatePermanent();
        }

                // MOD_EXILE_VANILLA_ROLE_BUNDLE_TRACE_AB_V15
        // Logger-only A/B. No RoleType / IsDead / RPC behavior is changed.
        private const string ExileRoleBundleTraceAbV15Marker = "MOD_EXILE_VANILLA_ROLE_BUNDLE_TRACE_AB_V15";

        private static bool IsExileRoleBundleImpostorFamilyAbV15(RoleTypes role)
        {
            var roleName = role.ToString();
            return
                role == RoleTypes.Impostor ||
                role == RoleTypes.Shapeshifter ||
                roleName == "Phantom" ||
                roleName == "Viper";
        }

        private static void LogExileRoleBundleTraceAbV15(
            string stage,
            byte exiledId)
        {
            if (!AmongUsClient.Instance.AmHost)
                return;

            if (exiledId == byte.MaxValue &&
                MeetingVoteManager.H4TryGetActualExileLedger(out var ledger) &&
                !ledger.IsTie)
            {
                exiledId = ledger.ActualExiledPlayerId;
            }

            var exiled = FindExileDisplayPlayerPermanent(exiledId);
            var exiledCustomRole = exiled == null ? "Missing" : exiled.GetCustomRole().ToString();
            var exiledIsMod = exiled != null && exiled.IsModClient();

            foreach (var viewer in Main.AllPlayerControls)
            {
                if (viewer == null ||
                    viewer.Data == null ||
                    viewer.Data.Disconnected ||
                    viewer.AmOwner ||
                    viewer.IsModClient())
                {
                    continue;
                }

                var snapshotBundle = new System.Text.StringBuilder();
                var currentBundle = new System.Text.StringBuilder();
                var snapshotAliveImpostors = 0;
                var snapshotAliveNonImpostors = 0;
                var currentAliveImpostors = 0;
                var currentAliveNonImpostors = 0;

                foreach (var targetId in ExileDisplaySafeTargetIdsPermanent)
                {
                    var target = FindExileDisplayPlayerPermanent(targetId);
                    var targetData =
                        target == null ? null : target.Data;
                    var targetUsable =
                        targetData != null &&
                        !targetData.Disconnected;
                    var targetIsDead =
                        targetData != null &&
                        targetData.IsDead;

                    var hasSnapshot =
                        ExileDisplayRestoreSnapshotPermanent.TryGetValue(
                            (viewer.PlayerId, targetId),
                            out var snapshotRole);
                    var hasCurrent =
                        SelectRolesPatch.TryGetExileDisplayRoleViewPermanent(
                            viewer.PlayerId,
                            targetId,
                            out var currentRole);

                    if (snapshotBundle.Length > 0)
                        snapshotBundle.Append(",");
                    snapshotBundle
                        .Append(targetId)
                        .Append(":")
                        .Append(hasSnapshot ? snapshotRole.ToString() : "Missing")
                        .Append("/Dead=")
                        .Append(targetIsDead);

                    if (currentBundle.Length > 0)
                        currentBundle.Append(",");
                    currentBundle
                        .Append(targetId)
                        .Append(":")
                        .Append(hasCurrent ? currentRole.ToString() : "Missing")
                        .Append("/Dead=")
                        .Append(targetIsDead);

                    if (!targetUsable || targetId == exiledId)
                        continue;

                    if (hasSnapshot)
                    {
                        if (IsExileRoleBundleImpostorFamilyAbV15(snapshotRole))
                            snapshotAliveImpostors++;
                        else
                            snapshotAliveNonImpostors++;
                    }

                    if (hasCurrent)
                    {
                        if (IsExileRoleBundleImpostorFamilyAbV15(currentRole))
                            currentAliveImpostors++;
                        else
                            currentAliveNonImpostors++;
                    }
                }

                var hasCandidate =
                    SelectRolesPatch.ClientReadyBarrierCandidateByViewer.TryGetValue(
                        viewer.PlayerId,
                        out var candidateId);
                var candidateSnapshot = "None";
                var candidateCurrent = "None";

                if (hasCandidate)
                {
                    if (ExileDisplayRestoreSnapshotPermanent.TryGetValue(
                            (viewer.PlayerId, candidateId),
                            out var candidateSnapshotRole))
                    {
                        candidateSnapshot = candidateSnapshotRole.ToString();
                    }

                    if (SelectRolesPatch.TryGetExileDisplayRoleViewPermanent(
                            viewer.PlayerId,
                            candidateId,
                            out var candidateCurrentRole))
                    {
                        candidateCurrent = candidateCurrentRole.ToString();
                    }
                }

                var dummySnapshot = "None";
                var dummyCurrent = "None";
                if (ExileDisplayDummyImpostorIdPermanent != byte.MaxValue)
                {
                    if (ExileDisplayRestoreSnapshotPermanent.TryGetValue(
                            (viewer.PlayerId, ExileDisplayDummyImpostorIdPermanent),
                            out var dummySnapshotRole))
                    {
                        dummySnapshot = dummySnapshotRole.ToString();
                    }

                    if (SelectRolesPatch.TryGetExileDisplayRoleViewPermanent(
                            viewer.PlayerId,
                            ExileDisplayDummyImpostorIdPermanent,
                            out var dummyCurrentRole))
                    {
                        dummyCurrent = dummyCurrentRole.ToString();
                    }
                }

                Logger.Info(
                    $"Stage={stage} | ViewerId={viewer.PlayerId} | ViewerClientId={viewer.GetClientId()} | ViewerCustomRole={viewer.GetCustomRole()} | " +
                    $"ExiledId={exiledId} | ExiledCustomRole={exiledCustomRole} | ExiledIsMod={exiledIsMod} | " +
                    $"DummyId={ExileDisplayDummyImpostorIdPermanent} | CandidateId={(hasCandidate ? candidateId : byte.MaxValue)} | " +
                    $"CandidateSnapshot={candidateSnapshot} | CandidateCurrent={candidateCurrent} | " +
                    $"DummySnapshot={dummySnapshot} | DummyCurrent={dummyCurrent} | " +
                    $"SnapshotAliveImp={snapshotAliveImpostors} | SnapshotAliveNonImp={snapshotAliveNonImpostors} | " +
                    $"CurrentAliveImp={currentAliveImpostors} | CurrentAliveNonImp={currentAliveNonImpostors} | " +
                    $"SafetyPending={ExileDisplaySafetyPendingPermanent} | SafetyApplied={ExileDisplaySafetyAppliedPermanent} | " +
                    $"SnapshotBundle=[{snapshotBundle}] | CurrentBundle=[{currentBundle}]",
                    ExileRoleBundleTraceAbV15Marker);
            }
        }

internal static void Priority2to5CaptureRestore()
        {
            TownOfHostForE.Modules.Priority2to5Diagnostics.CaptureRestore(ExileDisplayRestoreSnapshotPermanent, ExileDisplayDummyImpostorIdPermanent);
        }

        internal static string Priority2to5TopologyFields(byte viewerId)
        {
            var found = ViewerNativeTopologyLeasesAbV27.TryGetValue(viewerId, out var lease);
            return $"GlobalSafetyDummy={ExileDisplayDummyImpostorIdPermanent} " +
                $"V28MeetingSentinel={(found ? lease!.MeetingSentinelId.ToString() : "MISSING")} " +
                $"V27PersistentSentinel={(found ? lease!.PersistentSentinelId.ToString() : "MISSING")} " +
                $"V28BarrierApplied={(found ? lease!.EndMeetingBarrierApplied.ToString() : "MISSING")} " +
                $"PersistentActive={(found ? lease!.PersistentSentinelWireOverride.ToString() : "MISSING")} " +
                $"TopologyGeneration={(found ? lease!.MeetingGeneration.ToString() : "MISSING")}";
        }

        private static void ClearExileDisplaySafetyStatePermanent()
        {
            TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => Priority2to5CaptureRestore());
            ExileDisplaySafetyPendingPermanent = false;
            ExileDisplaySafetyAppliedPermanent = false;
            ExileDisplayDummyImpostorIdPermanent = byte.MaxValue;
            ExileDisplaySafeTargetIdsPermanent.Clear();
            ExileDisplayRestoreSnapshotPermanent.Clear();
        }

        ///<summary>
        ///一時的にIsDeadを本来のものに戻した状態でコードを実行します
        ///<param name="action">実行内容</param>
        ///</summary>
        public static void TempRestore(Action action)
        {
            logger.Info("==Temp Restore==");
            //IsDeadが上書きされた状態でTempRestoreが実行されたかどうか
            bool before_IsCached = IsCached;
            try
            {
                if (before_IsCached) RestoreIsDead(doSend: false);
                action();
            }
            catch (Exception ex)
            {
                logger.Warn("AntiBlackout.TempRestore内で例外が発生しました");
                logger.Exception(ex);
            }
            finally
            {
                if (before_IsCached) SetIsDead(doSend: false);
                logger.Info("==/Temp Restore==");
            }
        }

        [GameModuleInitializer]
        public static void Reset()
        {
            logger.Info("==Reset==");
            if (isDeadCache == null) isDeadCache = new();
            isDeadCache.Clear();
            IsCached = false;

            // ExileDisplayRealPermanent: ゲーム初期化時に前ゲームの一時安全化状態を破棄
            ClearExileDisplaySafetyStatePermanent();

            // VIEWER_NATIVE_TOPOLOGY_LIFECYCLE_AB_V27:
            ResetViewerNativeTopologyLifecycleAbV27();
        }
    }
}
