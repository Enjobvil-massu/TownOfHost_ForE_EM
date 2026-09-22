using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;

using TownOfHostForE.Attributes;
using TownOfHostForE.GameMode;
using TownOfHostForE.Modules;
using TownOfHostForE.Patches;
using TownOfHostForE.Roles;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Crewmate;
using TownOfHostForE.Roles.Neutral;
using static TownOfHostForE.Translator;

namespace TownOfHostForE
{
    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.CoStartGame))]
    class ChangeRoleSettings
    {
        public static int ImpostorSetNum = 2;
        public static void Postfix(AmongUsClient __instance)
        {
            TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.Begin());
            CriticalStartFlowPacketDiagnostic.BeginFlow("AmongUsClient.CoStartGame.Postfix");
            StartFlowReadiness.BeginFlow();
            CriticalStartFlowPacketDiagnostic.Mark(
                "CHANGE_ROLE_SETTINGS_ENTER",
                $"AmHost={__instance.AmHost} Players={Main.AllPlayerControls.Count()}");

            //注:この時点では役職は設定されていません。
            Main.NormalOptions.roleOptions.SetRoleRate(RoleTypes.GuardianAngel, 0, 0);
            Main.NormalOptions.roleOptions.SetRoleRate(RoleTypes.Viper, 0, 0);
            Main.NormalOptions.roleOptions.SetRoleRate(RoleTypes.Judge, 0, 0);

            ImpostorSetNum = Main.NormalOptions.NumImpostors;

            Main.AllPlayerKillCooldown = new Dictionary<byte, float>();
            Main.AllPlayerSpeed = new Dictionary<byte, float>();

            Main.SKMadmateNowCount = 0;

            Main.AfterMeetingDeathPlayers = new();
            Main.clientIdList = new();

            Main.CheckShapeshift = new();
            Main.ShapeshiftTarget = new();

            Main.NotCrewAssignCount = new();

            ReportDeadBodyPatch.CanReport = new();
            ReportDeadBodyPatch.CanReportByDeadBody = new();
            MeetingHudPatch.RevengeTargetPlayer = new();
            Options.UsedButtonCount = 0;
            Main.RealOptionsData = new OptionBackupData(GameOptionsManager.Instance.CurrentGameOptions);

            Main.isFirstTurn = false;

            Main.DefaultCrewmateVision = Main.RealOptionsData.GetFloat(FloatOptionNames.CrewLightMod);
            Main.DefaultImpostorVision = Main.RealOptionsData.GetFloat(FloatOptionNames.ImpostorLightMod);

            Main.LastNotifyNames = new();

            Main.PlayerColors = new();
            //名前の記録
            Main.AllPlayerNames = new();

            //キルカウントリセット
            Main.killCount = new();

            //シェイプをボタンにしてる人のIDリセット
            Main.shapeSwitchPlayerIds = new();

            //ラバーズ初期化
            LoversManager.LoversMaster = new();

            //デスゲーム判定をリセット
            DarkGameMaster.IsDeathGameTime = false;

            var invalidColor = Main.AllPlayerControls.Where(p => p.Data.DefaultOutfit.ColorId < 0 || Palette.PlayerColors.Length <= p.Data.DefaultOutfit.ColorId);
            if (invalidColor.Any())
            {
                var msg = Translator.GetString("Error.InvalidColor");
                Logger.SendInGame(msg);
                msg += "\n" + string.Join(",", invalidColor.Select(p => $"{p.name}({p.Data.DefaultOutfit.ColorId})"));
                Utils.SendMessage(msg);
                Logger.Error(msg, "CoStartGame");
            }

            GameModuleInitializerAttribute.InitializeAll();

            foreach (var target in Main.AllPlayerControls)
            {
                foreach (var seer in Main.AllPlayerControls)
                {
                    var pair = (target.PlayerId, seer.PlayerId);
                    Main.LastNotifyNames[pair] = target.name;
                }
            }
            foreach (var pc in Main.AllPlayerControls)
            {
                var colorId = pc.Data.DefaultOutfit.ColorId;
                if (AmongUsClient.Instance.AmHost && Options.GetNameChangeModes() == NameChange.Color)
                {
                    if(pc.Is(CustomRoles.Rainbow)) pc.RpcSetName(GetString("RainbowColor"));
                    else pc.RpcSetName(Palette.GetColorName(colorId));
                }
                PlayerState.Create(pc.PlayerId);
                Main.AllPlayerNames[pc.PlayerId] = pc?.Data?.PlayerName;
                Main.PlayerColors[pc.PlayerId] = Palette.PlayerColors[colorId];
                Main.AllPlayerSpeed[pc.PlayerId] = Main.RealOptionsData.GetFloat(FloatOptionNames.PlayerSpeedMod); //移動速度をデフォルトの移動速度に変更
                ReportDeadBodyPatch.CanReport[pc.PlayerId] = true;
                ReportDeadBodyPatch.CanReportByDeadBody[pc.PlayerId] = true;
                ReportDeadBodyPatch.WaitReport[pc.PlayerId] = new();
                pc.cosmetics.nameText.text = pc.name;

                var outfit = pc.Data.DefaultOutfit;
                Camouflage.PlayerSkins[pc.PlayerId] = new NetworkedPlayerInfo.PlayerOutfit().Set(outfit.PlayerName, outfit.ColorId, outfit.HatId, outfit.SkinId, outfit.VisorId, outfit.PetId);
                Main.clientIdList.Add(pc.GetClientId());
            }
            Main.VisibleTasksCount = true;
            if (__instance.AmHost)
            {
                RPC.SyncCustomSettingsRPC(origin: "CoStartGame.ChangeRoleSettings");
                if (Options.CurrentGameMode == CustomGameMode.HideAndSeek)
                {
                    Options.HideAndSeekKillDelayTimer = Options.KillDelay.GetFloat();
                }
                if (Options.IsStandardHAS)
                {
                    Options.HideAndSeekKillDelayTimer = Options.StandardHASWaitingTime.GetFloat();
                }
            }

            IRandom.SetInstanceById(Options.GetRoleAssigningRandomId());
            MassuOriginalRoleAssignmentABV1.BeginGame();

            MeetingStates.MeetingCalled = false;
            MeetingStates.FirstMeeting = true;
            GameStates.AlreadyDied = false;
        }
    }
    [HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
    class SelectRolesPatch
    {
        private static bool AssignedStrayWolf = false;
        private static readonly Dictionary<(byte SeerId, byte TargetId), RoleTypes> VanillaIntroRoleMap = new();

        // CLIENTREADY_INTRO_BARRIER_AB_PHASE1:
        // remote Vanilla viewerだけ、ClientReady後のRole配布rootを組み替える診断A/B。
        // Host canonical NetworkedPlayerInfo.Disconnectedは変更せず、
        // viewerにだけ1人のother-playerを一時Disconnectedとして見せる。
        private const string ClientReadyBarrierMarker = "CLIENTREADY_INTRO_BARRIER_AB_PHASE1";
        private const int ClientReadyBarrierMaxRootLength = 900;

        private sealed class ClientReadyBarrierCapturedUInt16
        {
            public int Position { get; }

            public ClientReadyBarrierCapturedUInt16(int position)
            {
                Position = position;
            }
        }

        private static readonly Dictionary<byte, List<(PlayerControl Target, RoleTypes Role)>>
            ClientReadyBarrierQueuedRoles = new();

        internal static readonly Dictionary<byte, byte>
            ClientReadyBarrierCandidateByViewer = new();

        // EVILHACKER_ATOMIC_ANCHOR_RESTORE_AB_V4
        // EvilHacker reproducerだけ。permanent map/queued canonical roleは変更しない。
        private static readonly Dictionary<byte, (byte AnchorId, RoleTypes CanonicalRole, byte SoleImpostorId)>
            ClientReadyBarrierAtomicAnchorByViewerABV4 = new();

        private static List<ClientReadyBarrierCapturedUInt16>? ClientReadyBarrierCapturedUInt16Writes;
        private static IntPtr ClientReadyBarrierCapturedWriterPointer = IntPtr.Zero;

        private static bool IsClientReadyBarrierViewer(PlayerControl seer)
        {
            return seer != null &&
                   !seer.AmOwner &&
                   seer.Data != null &&
                   !seer.Data.Disconnected &&
                   !seer.IsModClient();
        }

        private static void BeginClientReadyBarrierUInt16Capture(MessageWriter writer)
        {
            ClientReadyBarrierCapturedUInt16Writes = new();
            ClientReadyBarrierCapturedWriterPointer = writer.Pointer;
        }

        private static List<ClientReadyBarrierCapturedUInt16> EndClientReadyBarrierUInt16Capture()
        {
            var result =
                ClientReadyBarrierCapturedUInt16Writes ??
                new List<ClientReadyBarrierCapturedUInt16>();

            ClientReadyBarrierCapturedUInt16Writes = null;
            ClientReadyBarrierCapturedWriterPointer = IntPtr.Zero;
            return result;
        }

        internal static void CaptureClientReadyBarrierUInt16Write(
            MessageWriter writer,
            ushort value)
        {
            if (ClientReadyBarrierCapturedUInt16Writes == null ||
                ClientReadyBarrierCapturedWriterPointer == IntPtr.Zero ||
                writer.Pointer != ClientReadyBarrierCapturedWriterPointer)
            {
                return;
            }

            ClientReadyBarrierCapturedUInt16Writes.Add(
                new ClientReadyBarrierCapturedUInt16(writer.Position));
        }

        private static bool TryAppendClientReadyBarrierSpoof(
            CustomRpcSender sender,
            NetworkedPlayerInfo source,
            out int flagsPosition,
            out byte beforeFlags,
            out byte spoofFlags)
        {
            flagsPosition = -1;
            beforeFlags = 0;
            spoofFlags = 0;

            if (sender == null ||
                source == null ||
                source.Disconnected ||
                source.IsDead)
            {
                return false;
            }

            sender.stream.StartMessage(1);
            sender.stream.WritePacked(source.NetId);

            List<ClientReadyBarrierCapturedUInt16> captured;
            BeginClientReadyBarrierUInt16Capture(sender.stream);
            try
            {
                source.Serialize(sender.stream, true);
            }
            finally
            {
                captured = EndClientReadyBarrierUInt16Capture();
            }

            sender.stream.EndMessage();

            if (captured.Count < 1)
            {
                Logger.Error(
                    $"{ClientReadyBarrierMarker} no UInt16 role write " +
                    $"PlayerId={source.PlayerId}",
                    "ClientReadyIntroBarrierAB1");
                return false;
            }

            var firstRoleWrite = captured.OrderBy(item => item.Position).First();
            flagsPosition = firstRoleWrite.Position - 1;

            if (flagsPosition < 0 ||
                flagsPosition >= sender.stream.Length)
            {
                Logger.Error(
                    $"{ClientReadyBarrierMarker} flags position invalid " +
                    $"PlayerId={source.PlayerId} Position={flagsPosition} " +
                    $"Length={sender.stream.Length}",
                    "ClientReadyIntroBarrierAB1");
                return false;
            }

            beforeFlags = sender.stream.Buffer[flagsPosition];

            var expectedFlags = (byte)(
                (source.Disconnected ? 0x01 : 0x00) |
                (source.IsDead ? 0x04 : 0x00));

            if ((beforeFlags & 0x05) != expectedFlags)
            {
                Logger.Error(
                    $"{ClientReadyBarrierMarker} flags verification failed " +
                    $"PlayerId={source.PlayerId} Position={flagsPosition} " +
                    $"Actual={beforeFlags} ExpectedMask={expectedFlags}",
                    "ClientReadyIntroBarrierAB1");
                return false;
            }

            spoofFlags = (byte)(beforeFlags | 0x01);
            sender.stream.Buffer[flagsPosition] = spoofFlags;
            return true;
        }

        private static void AppendClientReadyBarrierCanonicalRestore(
            CustomRpcSender sender,
            NetworkedPlayerInfo source)
        {
            sender.stream.StartMessage(1);
            sender.stream.WritePacked(source.NetId);
            source.Serialize(sender.stream, true);
            sender.stream.EndMessage();
        }

        private static void AppendClientReadyBarrierSetRole(
            CustomRpcSender sender,
            PlayerControl target,
            RoleTypes role,
            bool canOverride)
        {
            sender.StartRpc(target.NetId, RpcCalls.SetRole)
                .Write((ushort)role)
                .Write(canOverride)
                .EndRpc();
        }

        private static readonly HashSet<byte> P4SuppressedIntroViewerIds = new();

        private static bool TryBuildClientReadyBarrierFallbackSender(
            PlayerControl viewer,
            List<(PlayerControl Target, RoleTypes Role)> queuedRoles,
            out CustomRpcSender fallback)
        {
            fallback = null!;
            if (!viewer.TryGetClientId(out var clientId)) return false;
            fallback = new CustomRpcSender(
                $"{viewer.name}'s ClientReady Barrier Fallback Sender",
                SendOption.Reliable,
                false);

            fallback.StartMessage(clientId);

            foreach (var queued in queuedRoles)
            {
                fallback.RpcSetRole(
                    queued.Target,
                    queued.Role,
                    clientId);
            }

            fallback.EndMessage();
            return true;
        }

        private static bool TryBuildClientReadyBarrierSender(
            PlayerControl viewer,
            List<(PlayerControl Target, RoleTypes Role)> queuedRoles,
            out CustomRpcSender barrierSender,
            out byte candidatePlayerId)
        {
            barrierSender = null!;
            candidatePlayerId = byte.MaxValue;
            if (!viewer.TryGetClientId(out var clientId)) return false;

            var allTargets = Main.AllPlayerControls
                .Where(target =>
                    target != null &&
                    target.Data != null)
                .OrderBy(target => target.PlayerId)
                .ToList();

            if (allTargets.Count < 2 ||
                !allTargets.Any(target => target.PlayerId == viewer.PlayerId) ||
                queuedRoles == null ||
                queuedRoles.Count == 0)
            {
                return false;
            }

            var queuedTargetIds =
                queuedRoles
                    .Where(item => item.Target != null)
                    .Select(item => item.Target.PlayerId)
                    .ToHashSet();

            if (allTargets.Any(target =>
                    !queuedTargetIds.Contains(target.PlayerId)))
            {
                Logger.Error(
                    $"{ClientReadyBarrierMarker} incomplete role queue " +
                    $"Viewer={viewer.PlayerId} " +
                    $"Targets={allTargets.Count} QueuedTargetIds={queuedTargetIds.Count}",
                    "ClientReadyIntroBarrierAB1");
                return false;
            }

            var hasAtomicAnchor =
                ClientReadyBarrierAtomicAnchorByViewerABV4.TryGetValue(
                    viewer.PlayerId,
                    out var atomicAnchor);

            PlayerControl? candidate;
            if (hasAtomicAnchor)
            {
                // EVILHACKER_ATOMIC_ANCHOR_PREROLE_READY_AB_V5:
                // Atomic anchor is selected from the canonical queued role plan.
                // Release() starts CoSetRole asynchronously and immediately builds
                // the ClientReady barrier in the same call, so Data.Role may still
                // be null here. The atomic root itself writes Impostor/true and then
                // restores CanonicalRole/false; pre-existing RoleBehaviour is not
                // used as the authority for this atomic candidate selection.
                candidate = allTargets.FirstOrDefault(target =>
                    target.PlayerId == atomicAnchor.AnchorId &&
                    target.PlayerId != viewer.PlayerId &&
                    queuedTargetIds.Contains(target.PlayerId) &&
                    target.Data != null &&
                    !target.Data.Disconnected &&
                    !target.Data.IsDead);

                var canonicalMatches = queuedRoles.Count(item =>
                    item.Target != null &&
                    item.Target.PlayerId == atomicAnchor.AnchorId &&
                    item.Role == atomicAnchor.CanonicalRole);

                // EVILHACKER_ATOMIC_ANCHOR_RUNTIME_PREDICATE_DIAGNOSTIC_AB_V1:
                // INVALID_PLAN時にどのcandidate predicateが不成立かを確定する。
                // 診断のみ。candidate選択・Role bundle・wire内容は変更しない。
                var rawAtomicAnchor = Main.AllPlayerControls.FirstOrDefault(target =>
                    target != null &&
                    target.PlayerId == atomicAnchor.AnchorId);

                var filteredAtomicAnchor = allTargets.FirstOrDefault(target =>
                    target.PlayerId == atomicAnchor.AnchorId);

                var rawDataPresent =
                    rawAtomicAnchor != null &&
                    rawAtomicAnchor.Data != null;

                var rawDisconnected =
                    rawAtomicAnchor != null &&
                    rawAtomicAnchor.Data != null &&
                    rawAtomicAnchor.Data.Disconnected;

                var rawIsDead =
                    rawAtomicAnchor != null &&
                    rawAtomicAnchor.Data != null &&
                    rawAtomicAnchor.Data.IsDead;

                var filteredDataPresent =
                    filteredAtomicAnchor != null &&
                    filteredAtomicAnchor.Data != null;

                var filteredDisconnected =
                    filteredAtomicAnchor != null &&
                    filteredAtomicAnchor.Data != null &&
                    filteredAtomicAnchor.Data.Disconnected;

                var filteredIsDead =
                    filteredAtomicAnchor != null &&
                    filteredAtomicAnchor.Data != null &&
                    filteredAtomicAnchor.Data.IsDead;

                Logger.Info(
                    $"EVILHACKER_ATOMIC_ANCHOR_RUNTIME_PREDICATE_DIAGNOSTIC_AB_V1 " +
                    $"Viewer={viewer.PlayerId} Anchor={atomicAnchor.AnchorId} " +
                    $"RawPresent={rawAtomicAnchor != null} " +
                    $"RawDataPresent={rawDataPresent} " +
                    $"RawDisconnected={rawDisconnected} " +
                    $"RawIsDead={rawIsDead} " +
                    $"FilteredPresent={filteredAtomicAnchor != null} " +
                    $"FilteredDataPresent={filteredDataPresent} " +
                    $"FilteredDisconnected={filteredDisconnected} " +
                    $"FilteredIsDead={filteredIsDead} " +
                    $"ViewerMismatch={atomicAnchor.AnchorId != viewer.PlayerId} " +
                    $"QueuedContains={queuedTargetIds.Contains(atomicAnchor.AnchorId)} " +
                    $"CanonicalMatches={canonicalMatches} " +
                    $"CandidateNull={candidate == null} " +
                    $"AllTargets={allTargets.Count} QueuedRoles={queuedRoles.Count}",
                    "ClientReadyIntroBarrierAB1");

                if (candidate == null ||
                    canonicalMatches != 1 ||
                    atomicAnchor.CanonicalRole is
                        RoleTypes.Impostor or
                        RoleTypes.Shapeshifter or
                        RoleTypes.Viper or
                        RoleTypes.Phantom ||
                    atomicAnchor.AnchorId == atomicAnchor.SoleImpostorId)
                {
                    Logger.Error(
                        $"EVILHACKER_ATOMIC_ANCHOR_RESTORE_AB_V4 INVALID_PLAN " +
                        $"Viewer={viewer.PlayerId} Anchor={atomicAnchor.AnchorId} " +
                        $"Canonical={atomicAnchor.CanonicalRole} SoleImpostor={atomicAnchor.SoleImpostorId} " +
                        $"CanonicalMatches={canonicalMatches}",
                        "ClientReadyIntroBarrierAB1");
                    return false;
                }
            }
            else
            {
                candidate = allTargets.FirstOrDefault(target =>
                    target.PlayerId != viewer.PlayerId &&
                    queuedTargetIds.Contains(target.PlayerId) &&
                    target.Data != null &&
                    !target.Data.Disconnected &&
                    !target.Data.IsDead &&
                    target.Data.Role != null);
            }

            if (candidate == null)
            {
                Logger.Error(
                    $"{ClientReadyBarrierMarker} no barrier candidate " +
                    $"Viewer={viewer.PlayerId}",
                    "ClientReadyIntroBarrierAB1");
                return false;
            }

            var sender = new CustomRpcSender(
                $"{viewer.name}'s ClientReady Barrier Sender",
                SendOption.Reliable,
                false);

            try
            {
                sender.StartMessage(clientId);

                if (!TryAppendClientReadyBarrierSpoof(
                        sender,
                        candidate.Data,
                        out var flagsPosition,
                        out var beforeFlags,
                        out var spoofFlags))
                {
                    sender.stream.Recycle();
                    return false;
                }

                RoleTypes? finalSelfRole = null;
                PlayerControl? self = null;

                if (hasAtomicAnchor)
                {
                    // EvilHacker reproducer: intended sole Impostorを先に通常確定、
                    // anchor/candidateだけI,trueで未確定にし、残りを送る。
                    var soleEntryMatches = queuedRoles.Where(item =>
                        item.Target != null &&
                        item.Target.PlayerId == atomicAnchor.SoleImpostorId).ToList();

                    if (soleEntryMatches.Count != 1)
                    {
                        Logger.Error(
                            $"EVILHACKER_ATOMIC_ANCHOR_RESTORE_AB_V4 SOLE_ENTRY_INVALID " +
                            $"Viewer={viewer.PlayerId} Count={soleEntryMatches.Count}",
                            "ClientReadyIntroBarrierAB1");
                        sender.stream.Recycle();
                        return false;
                    }

                    var soleEntry = soleEntryMatches[0];
                    AppendClientReadyBarrierSetRole(
                        sender,
                        soleEntry.Target,
                        soleEntry.Role,
                        canOverride: false);

                    AppendClientReadyBarrierSetRole(
                        sender,
                        candidate,
                        RoleTypes.Impostor,
                        canOverride: true);

                    foreach (var queued in queuedRoles)
                    {
                        if (queued.Target == null) continue;

                        if (queued.Target.PlayerId == viewer.PlayerId)
                        {
                            self = queued.Target;
                            finalSelfRole = queued.Role;
                            continue;
                        }

                        if (queued.Target.PlayerId == atomicAnchor.SoleImpostorId ||
                            queued.Target.PlayerId == atomicAnchor.AnchorId)
                        {
                            continue;
                        }

                        AppendClientReadyBarrierSetRole(
                            sender,
                            queued.Target,
                            queued.Role,
                            canOverride: false);
                    }
                }
                else
                {
                    // 既存非EvilHacker barrier経路は従来通り。
                    foreach (var queued in queuedRoles)
                    {
                        if (queued.Target == null) continue;

                        if (queued.Target.PlayerId == viewer.PlayerId)
                        {
                            self = queued.Target;
                            finalSelfRole = queued.Role;
                            continue;
                        }

                        AppendClientReadyBarrierSetRole(
                            sender,
                            queued.Target,
                            queued.Role,
                            canOverride:
                                queued.Target.PlayerId == candidate.PlayerId);
                    }
                }

                if (self == null || !finalSelfRole.HasValue)
                {
                    Logger.Error(
                        $"{ClientReadyBarrierMarker} self role missing " +
                        $"Viewer={viewer.PlayerId}",
                        "ClientReadyIntroBarrierAB1");
                    sender.stream.Recycle();
                    return false;
                }

                AppendClientReadyBarrierSetRole(
                    sender,
                    self,
                    finalSelfRole.Value,
                    canOverride: false);

                AppendClientReadyBarrierCanonicalRestore(
                    sender,
                    candidate.Data);

                if (hasAtomicAnchor)
                {
                    // 同一reliable root内でcanonical RoleBehaviourまでfalseでfinalizeする。
                    AppendClientReadyBarrierSetRole(
                        sender,
                        candidate,
                        atomicAnchor.CanonicalRole,
                        canOverride: false);

                    Logger.Info(
                        $"EVILHACKER_ATOMIC_ANCHOR_RESTORE_AB_V4 PREPARED " +
                        $"Viewer={viewer.PlayerId} Anchor={candidate.PlayerId} " +
                        $"Initial={RoleTypes.Impostor}/True Final={atomicAnchor.CanonicalRole}/False " +
                        $"SoleImpostor={atomicAnchor.SoleImpostorId}",
                        "ClientReadyIntroBarrierAB1");
                }

                sender.EndMessage();

                if (sender.stream.Length > ClientReadyBarrierMaxRootLength)
                {
                    Logger.Error(
                        $"{ClientReadyBarrierMarker} root too large " +
                        $"Viewer={viewer.PlayerId} Candidate={candidate.PlayerId} " +
                        $"Length={sender.stream.Length} " +
                        $"Limit={ClientReadyBarrierMaxRootLength}",
                        "ClientReadyIntroBarrierAB1");
                    sender.stream.Recycle();
                    return false;
                }

                barrierSender = sender;
                candidatePlayerId = candidate.PlayerId;

                Logger.Info(
                    $"{ClientReadyBarrierMarker} prepared " +
                    $"Viewer={viewer.PlayerId} ClientId={viewer.GetClientId()} " +
                    $"Candidate={candidate.PlayerId} " +
                    $"FlagsPosition={flagsPosition} " +
                    $"Flags={beforeFlags}->{spoofFlags} " +
                    $"RootLength={sender.stream.Length}",
                    "ClientReadyIntroBarrierAB1");

                return true;
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, "ClientReadyIntroBarrierAB1");
                if (sender.CurrentState is not CustomRpcSender.State.Finished)
                    sender.stream.Recycle();
                return false;
            }
        }

        private static void CompleteClientReadyBarrierRoleBundles(
            Dictionary<byte, CustomRpcSender> senders)
        {
            foreach (var viewerId in
                     ClientReadyBarrierQueuedRoles.Keys
                         .OrderBy(id => id)
                         .ToArray())
            {
                var viewer = Utils.GetPlayerById(viewerId);
                if (viewer == null ||
                    viewer.Data == null ||
                    viewer.Data.Disconnected ||
                    !senders.TryGetValue(viewerId, out var oldSender))
                {
                    continue;
                }

                if (P4SuppressedIntroViewerIds.Contains(viewerId)) continue;
                var queuedRoles =
                    ClientReadyBarrierQueuedRoles[viewerId];

                CustomRpcSender replacement;

                if (TryBuildClientReadyBarrierSender(
                        viewer,
                        queuedRoles,
                        out var barrierSender,
                        out var candidatePlayerId))
                {
                    replacement = barrierSender;
                    ClientReadyBarrierCandidateByViewer[viewerId] =
                        candidatePlayerId;

                    Logger.Info(
                        $"{ClientReadyBarrierMarker} ACTIVE " +
                        $"Viewer={viewerId} Candidate={candidatePlayerId}",
                        "ClientReadyIntroBarrierAB1");
                }
                else
                {
                    if (!TryBuildClientReadyBarrierFallbackSender(
                            viewer,
                            queuedRoles,
                            out replacement))
                    {
                        P4SuppressedIntroViewerIds.Add(viewerId);
                        continue;
                    }

                    Logger.Error(
                        $"{ClientReadyBarrierMarker} FALLBACK " +
                        $"Viewer={viewerId} QueuedRoles={queuedRoles.Count}",
                        "ClientReadyIntroBarrierAB1");
                }

                if (oldSender.CurrentState is not CustomRpcSender.State.Finished)
                    oldSender.stream.Recycle();

                senders[viewerId] = replacement;
            }
        }

        private static void QueueOrSendVanillaIntroRole(
            PlayerControl seer,
            PlayerControl target,
            RoleTypes role,
            Dictionary<byte, CustomRpcSender> senders,
            RoleTypes? canonicalRole = null)
        {
            RecordVanillaIntroRole(
                seer.PlayerId,
                target.PlayerId,
                canonicalRole ?? role);

            if (P4SuppressedIntroViewerIds.Contains(seer.PlayerId)) return;
            if (IsClientReadyBarrierViewer(seer))
            {
                if (!ClientReadyBarrierQueuedRoles.TryGetValue(
                        seer.PlayerId,
                        out var queuedRoles))
                {
                    queuedRoles = new();
                    ClientReadyBarrierQueuedRoles[
                        seer.PlayerId] = queuedRoles;
                }

                queuedRoles.Add((target, role));
                return;
            }

            if (!seer.TryGetClientId(out var clientId))
            {
                P4SuppressedIntroViewerIds.Add(seer.PlayerId);
                return;
            }
            senders[seer.PlayerId].RpcSetRole(
                target,
                role,
                clientId);
        }

        [HarmonyPatch]
        private static class ClientReadyBarrierUInt16WritePatch
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
                CaptureClientReadyBarrierUInt16Write(
                    __instance,
                    value);
            }
        }

        private static void RecordVanillaIntroRole(byte seerId, byte targetId, RoleTypes role)
        {
            VanillaIntroRoleMap[(seerId, targetId)] = role;
        }

        // ExileDisplayRealPermanent:
        // v600.67で記録済みのviewer x target RoleTypeを
        // AntiBlackout安全化の正確な復元元として再利用する。
        internal static bool TryGetExileDisplayRoleViewPermanent(
            byte seerId,
            byte targetId,
            out RoleTypes role)
        {
            return VanillaIntroRoleMap.TryGetValue((seerId, targetId), out role);
        }

        // PRE_VOTING_FULL_SAFE_NPI_SERIALIZE_HELPER_AB_V19:
        // Serialize one NetworkedPlayerInfo into an already-targeted sender,
        // capture the two direct UInt16 role writes, and patch only the wire
        // bytes. Host Role/RoleWhenAlive/RoleBehaviour objects are untouched.
        internal static bool TryAppendPreVotingFullSafeNetworkedPlayerInfoAbV19(
            CustomRpcSender sender,
            NetworkedPlayerInfo source,
            RoleTypes wireRole,
            out int capturedRoleWriteCount)
        {
            capturedRoleWriteCount = 0;

            if (sender == null || source == null)
            {
                return false;
            }

            sender.stream.StartMessage(1);
            sender.stream.WritePacked(source.NetId);

            List<ClientReadyBarrierCapturedUInt16> captured;
            BeginClientReadyBarrierUInt16Capture(sender.stream);
            try
            {
                source.Serialize(sender.stream, true);
            }
            finally
            {
                captured = EndClientReadyBarrierUInt16Capture();
            }

            sender.stream.EndMessage();
            capturedRoleWriteCount = captured.Count;

            if (captured.Count != 2)
            {
                return false;
            }

            var wireRoleValue = (ushort)wireRole;
            foreach (var roleWrite in captured)
            {
                if (roleWrite.Position < 0 ||
                    roleWrite.Position + 1 >= sender.stream.Length)
                {
                    return false;
                }

                sender.stream.Buffer[roleWrite.Position] =
                    (byte)(wireRoleValue & 0xff);
                sender.stream.Buffer[roleWrite.Position + 1] =
                    (byte)(wireRoleValue >> 8);
            }

            return true;
        }

        // 通常死亡時のofficial Ghost Role変化だけを追従する。
        // キーが存在しない場合は新規推測値を作らない。
        internal static void UpdateExileDisplayRoleViewPermanent(
            byte seerId,
            byte targetId,
            RoleTypes role)
        {
            var key = (seerId, targetId);
            if (VanillaIntroRoleMap.ContainsKey(key))
                VanillaIntroRoleMap[key] = role;
        }

        public static void Prefix()
        {
            if (!AmongUsClient.Instance.AmHost) return;
            TownOfHostForE.Roles.Impostor.EvilHacker.CaptureSimpleAdminEnabledAtGameStart();
            P4SuppressedIntroViewerIds.Clear();
            VanillaIntroRoleMap.Clear();
            ClientReadyBarrierQueuedRoles.Clear();
            ClientReadyBarrierCandidateByViewer.Clear();
            ClientReadyBarrierAtomicAnchorByViewerABV4.Clear();
            //CustomRpcSenderとRpcSetRoleReplacerの初期化
            Dictionary<byte, CustomRpcSender> senders = new();
            foreach (var pc in Main.AllPlayerControls)
            {
                senders[pc.PlayerId] = new CustomRpcSender($"{pc.name}'s SetRole Sender", SendOption.Reliable, false);
                if (pc.PlayerId != 0)
                {
                    if (pc.TryGetClientId(out var clientId))
                        senders[pc.PlayerId].StartMessage(clientId);
                    else
                        P4SuppressedIntroViewerIds.Add(pc.PlayerId);
                }
            }
            RpcSetRoleReplacer.StartReplace(senders);

            // FACTION_COUNT_SEMANTICS_PERMANENT: Tuna動的陣営を役職抽選前に確定
            TownOfHostForE.Roles.Neutral.Tuna.ApplyFactionSetting();
            RoleAssignManager.SelectAssignRoles();

            if (Options.CurrentGameMode != CustomGameMode.HideAndSeek)
            {
                RoleTypes[] RoleTypesList = { RoleTypes.Scientist, RoleTypes.Judge, RoleTypes.Engineer, RoleTypes.Noisemaker, RoleTypes.Tracker, RoleTypes.Shapeshifter, RoleTypes.Viper, RoleTypes.Phantom };
                foreach (var roleTypes in RoleTypesList)
                {
                    var roleOpt = Main.NormalOptions.roleOptions;
                    int numRoleTypes = GetRoleTypesCount(roleTypes);
                    roleOpt.SetRoleRate(roleTypes, numRoleTypes, numRoleTypes > 0 ? 100 : 0);
                }

                List<PlayerControl> AllPlayers = new();
                foreach (var pc in Main.AllPlayerControls)
                {
                    AllPlayers.Add(pc);
                }

                if (Options.EnableGM.GetBool())
                {
                    AllPlayers.RemoveAll(x => x == PlayerControl.LocalPlayer);
                    PlayerControl.LocalPlayer.RpcSetCustomRole(CustomRoles.GM);
                    PlayerControl.LocalPlayer.RpcSetRole(RoleTypes.Crewmate);
                    PlayerControl.LocalPlayer.Data.IsDead = true;
                }

                // MASSU_ORIGINAL_ROLE_ASSIGN_AB_V1
                MassuOriginalRoleAssignmentABV1.PrepareDesyncPlan(AllPlayers);

                Dictionary<(byte, byte), RoleTypes> rolesMap = new();

                foreach (var (role, info) in CustomRoleManager.AllRolesInfo)
                {
                    if (info.IsDesyncImpostor)
                    {
                        switch (role)
                        {
                            case CustomRoles.StrayWolf:
                                AssignedStrayWolf = AssignDesyncRole(CustomRoles.StrayWolf, AllPlayers, senders, rolesMap, BaseRole: RoleTypes.Impostor, IsImpostorRole: true);
                                continue;
                            case CustomRoles.Opportunist:
                                if (!Opportunist.OptionCanKill.GetBool()) continue;
                                break;
                        }
                        AssignDesyncRole(role, AllPlayers, senders, rolesMap, BaseRole: info.BaseRoleType.Invoke());
                    }
                }

                MakeDesyncSender(senders, rolesMap);
            }
            // ROLEBUGTRACE_STAGE1A_PERMANENT: custom role assignment snapshot
            RoleBugTrace.GameStart();
            //以下、バニラ側の役職割り当てが入る
        }
        internal static bool DeferRoleFinalization = false;

        public static void Postfix()
        {
            if (!AmongUsClient.Instance.AmHost) return;
            if (DeferRoleFinalization) return;

            CompleteRoleSelectionImmediately();
        }

        private static void CompleteRoleSelectionImmediately()
        {
            RpcSetRoleReplacer.SendAllImmediately();
            RpcSetRoleReplacer.Cleanup();
            FinalizeRoleSelection();
        }

        internal static System.Collections.IEnumerator CompleteRoleSelectionDeferred(
            float intervalSeconds = 0.3f)
        {
            CriticalStartFlowPacketDiagnostic.Mark(
                "ROLE_SELECTION_DEFERRED_ENTER",
                $"Interval={intervalSeconds} Players={Main.AllPlayerControls.Count()}");
            yield return RpcSetRoleReplacer.SendAllDeferred(
                intervalSeconds
            );

            CriticalStartFlowPacketDiagnostic.Mark("ROLE_SELECTION_DEFERRED_SEND_RETURNED");
            RpcSetRoleReplacer.Cleanup();
            CriticalStartFlowPacketDiagnostic.Mark("ROLE_SELECTION_DEFERRED_CLEANUP_RETURNED");
            FinalizeRoleSelection();
            CriticalStartFlowPacketDiagnostic.Mark("ROLE_SELECTION_DEFERRED_FINALIZE_RETURNED");
        }

        public static void FinalizeRoleSelection()
        {
            CriticalStartFlowPacketDiagnostic.Mark("FINALIZE_ROLE_SELECTION_ENTER");
            //Utils.ApplySuffix();

            var rand = IRandom.Instance;

            List<PlayerControl> Crewmates = new();
            List<PlayerControl> Impostors = new();
            List<PlayerControl> Scientists = new();
            List<PlayerControl> Engineers = new();
            List<PlayerControl> GuardianAngels = new();
            List<PlayerControl> Shapeshifters = new();
            List<PlayerControl> trackers = new();
            List<PlayerControl> noisemakers = new();
            List<PlayerControl> phantoms = new();

            List<PlayerControl> allPlayersbySub = new();

            foreach (var pc in Main.AllPlayerControls)
            {
                pc.Data.IsDead = false; //プレイヤーの死を解除する

                if (!pc.Is(CustomRoles.GM)) allPlayersbySub.Add(pc);

                var state = PlayerState.GetByPlayerId(pc.PlayerId);
                if (state.MainRole != CustomRoles.NotAssigned) continue; //既にカスタム役職が割り当てられていればスキップ
                var role = CustomRoles.NotAssigned;
                switch (pc.Data.Role.Role)
                {
                    case RoleTypes.Crewmate:
                        Crewmates.Add(pc);
                        role = CustomRoles.Crewmate;
                        break;
                    case RoleTypes.Impostor:
                        Impostors.Add(pc);
                        role = CustomRoles.Impostor;
                        break;
                    case RoleTypes.Scientist:
                        Scientists.Add(pc);
                        role = CustomRoles.Scientist;
                        break;
                    case RoleTypes.Judge:
                        // NATIVE_WRAPPER_SLOT_AB_V1: Judge is already the final native-backed main role.
                        role = CustomRoles.NormalJudge;
                        break;
                    case RoleTypes.Engineer:
                        Engineers.Add(pc);
                        role = CustomRoles.Engineer;
                        break;
                    case RoleTypes.GuardianAngel:
                        GuardianAngels.Add(pc);
                        role = CustomRoles.GuardianAngel;
                        break;
                    case RoleTypes.Shapeshifter:
                        Shapeshifters.Add(pc);
                        role = CustomRoles.Shapeshifter;
                        break;
                    case RoleTypes.Viper:
                        // NATIVE_WRAPPER_SLOT_AB_V1: Viper is already the final native-backed main role.
                        role = CustomRoles.NormalViper;
                        break;
                    case RoleTypes.Tracker:
                        trackers.Add(pc);
                        role = CustomRoles.Tracker;
                        break;
                    case RoleTypes.Noisemaker:
                        noisemakers.Add(pc);
                        role = CustomRoles.Noisemaker;
                        break;
                    case RoleTypes.Phantom:
                        if (pc.AmOwner &&
                            TryGetCommonStandardPlannedRoleABV5(
                                pc.PlayerId,
                                out var plannedPhantomRole) &&
                            plannedPhantomRole == CustomRoles.EvilHacker &&
                            TownOfHostForE.Roles.Impostor.EvilHacker.SimpleAdminEnabledAtGameStart)
                        {
                            // EVILHACKER_INITIAL_OWNER_NATIVE_PHANTOM_AB_V4:
                            // Host ownerだけnative PhantomでもCustomRole poolはImpostorとしてEvilHackerへ戻す。
                            Impostors.Add(pc);
                            role = CustomRoles.Impostor;
                        }
                        else
                        {
                            phantoms.Add(pc);
                            // NATIVE_WRAPPER_SLOT_AB_V1: Phantom is already the final native-backed main role.
                            role = CustomRoles.NormalPhantom;
                        }
                        break;
                    default:
                        Logger.SendInGame(string.Format(GetString("Error.InvalidRoleAssignment"), pc?.Data?.PlayerName));
                        break;
                }
                state.SetMainRole(role);
            }

            if (Options.CurrentGameMode == CustomGameMode.HideAndSeek)
            {
                SetColorPatch.IsAntiGlitchDisabled = true;
                foreach (var pc in Main.AllPlayerControls)
                {
                    if (pc.Is(CustomRoleTypes.Impostor))
                        pc.RpcSetColor(0);
                    else if (pc.Is(CustomRoleTypes.Crewmate))
                        pc.RpcSetColor(1);
                }

                //役職設定処理
                AssignCustomRolesFromList(CustomRoles.HASFox, Crewmates);
                AssignCustomRolesFromList(CustomRoles.HASTroll, Crewmates);
                foreach (var pair in PlayerState.AllPlayerStates)
                {
                    //RPCによる同期
                    ExtendedPlayerControl.RpcSetCustomRole(pair.Key, pair.Value.MainRole);
                }
                //色設定処理
                SetColorPatch.IsAntiGlitchDisabled = true;

                GameEndChecker.SetPredicateToHideAndSeek();
            }
            else if (Options.CurrentGameMode == CustomGameMode.SuperBombParty)
            {
                SetColorPatch.IsAntiGlitchDisabled = true;

                //役職設定処理
                foreach (var pair in PlayerState.AllPlayerStates)
                {
                    //RPCによる同期
                    ExtendedPlayerControl.RpcSetCustomRole(pair.Key, pair.Value.MainRole);
                }
                foreach (var pc in Main.AllPlayerControls)
                {
                    HudManager.Instance.SetHudActive(true);
                    //Main.AllPlayerKillCooldown[pc.PlayerId] = SuperBakuretsuBros.SBPKillCool.GetInt();
                    Main.AllPlayerKillCooldown[pc.PlayerId] = Options.DefaultKillCooldown;
                }
                GameEndChecker.SetPredicateToSuperBombParty();

                SuperBakuretsuBros.Add();
            }
            else
            {
                foreach (var role in CustomRolesHelper.AllStandardRoles)
                {
                    if (role.IsVanilla()) continue;
                    // NATIVE_WRAPPER_SLOT_AB_V1:
                    // Judge/Phantom/Viper are materialized by the native role-selection slot itself.
                    if (role is CustomRoles.NormalJudge or CustomRoles.NormalPhantom or CustomRoles.NormalViper) continue;
                    //if (role is CustomRoles.HASTroll or CustomRoles.HASFox or CustomRoles.BAKURETSUKI) continue;
                    //if (CustomRoleManager.GetRoleInfo(role) is SimpleRoleInfo info && info.RequireResetCam) continue;
                    if (CustomRoleManager.GetRoleInfo(role)?.IsDesyncImpostor == true) continue;
                    if (role == CustomRoles.Opportunist && Opportunist.OptionCanKill.GetBool()) continue;
                    if (role == CustomRoles.StrayWolf && AssignedStrayWolf) continue;
                    if (role is not CustomRoles.Opportunist and not CustomRoles.StrayWolf and not CustomRoles.Egoist &&
                        CustomRoleManager.GetRoleInfo(role)?.IsDesyncImpostor == true) continue;

                    var baseRoleTypes = role.GetRoleTypes() switch
                    {
                        RoleTypes.Impostor => Impostors,
                        RoleTypes.Shapeshifter => Shapeshifters,
                        RoleTypes.Phantom => phantoms,
                        RoleTypes.Scientist => Scientists,
                        RoleTypes.Engineer => Engineers,
                        RoleTypes.Noisemaker => noisemakers,
                        RoleTypes.Tracker => trackers,
                        RoleTypes.GuardianAngel => GuardianAngels,
                        _ => Crewmates,
                    };
                    AssignCustomRolesFromList(role, baseRoleTypes);
                }

                // Random-Addon
                AssignCustomSubRolesFromListfromNotCrew(CustomRoles.Gambler);
                //if (!CustomRoles.PlatonicLover.IsEnable() || !CustomRoles.OtakuPrincess.IsEnable()) AssignLoversRolesFromList(allPlayersbySub);
                AssignLoversRolesFromList(allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.AddWatch, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.Sunglasses, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.AddLight, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.AddSeer, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.Autopsy, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.VIP, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.Clumsy, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.Revenger, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.Management, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.InfoPoor, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.Sending, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.TieBreaker, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.NonReport, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.PlusVote, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.Guarding, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.AddBait, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.Refusing, allPlayersbySub);
                AssignCustomSubRolesFromList(CustomRoles.Chu2Byo, allPlayersbySub);

                foreach (var pair in PlayerState.AllPlayerStates)
                {
                    ExtendedPlayerControl.RpcSetCustomRole(pair.Key, pair.Value.MainRole);

                    foreach (var subRole in pair.Value.SubRoles)
                        ExtendedPlayerControl.RpcSetCustomRole(pair.Key, subRole);
                }

                CustomRoleManager.CreateInstance();

                ImposterChat.Add();

                foreach (var pc in Main.AllPlayerControls)
                {
                    HudManager.Instance.SetHudActive(true);
                    pc.ResetKillCooldown();

                    // DirectAssign-Addon
                    if (pc.GetCustomRole().IsAddAddOn()
                        && (Options.AddOnBuffAssign[pc.GetCustomRole()].GetBool() || Options.AddOnDebuffAssign[pc.GetCustomRole()].GetBool()))
                    {
                        foreach (var Addon in Enum.GetValues(typeof(CustomRoles)).Cast<CustomRoles>().Where(x => x.IsAddOn()))
                        {
                            // GAMBLER_MASTER_OFF_ASSIGNMENT_AB_V1: 0% is an absolute master OFF
                            if (Addon == CustomRoles.Gambler && !TownOfHostForE.Roles.AddOns.NotCrew.Gambler.IsAssignmentEnabled) continue;
                            // GOD_GAMBLER_DIRECT_GUARD_PERMANENT
                            if (pc.Is(CustomRoles.God) && Addon == CustomRoles.Gambler) continue;
                            if (Options.AddOnRoleOptions.TryGetValue((pc.GetCustomRole(), Addon), out var option) && option.GetBool())
                            {
                                pc.RpcSetCustomRole(Addon);
                                CustomRoleManager.subRoleAdd(pc.PlayerId, Addon);
                            }
                        }
                    }

                    //通常モードでかくれんぼをする人用
                    if (Options.IsStandardHAS)
                    {
                        foreach (var seer in Main.AllPlayerControls)
                        {
                            if (seer == pc) continue;
                            if (pc.GetCustomRole().IsImpostor() || pc.IsNeutralKiller() || pc.IsAnimalsKiller()) //変更対象がインポスター陣営orキル可能な第三陣営
                                NameColorManager.Add(seer.PlayerId, pc.PlayerId);
                        }
                    }
                    foreach (var seer in Main.AllPlayerControls)
                    {
                        if (seer == pc) continue;
                        if (pc.Is(CustomRoles.GM)
                            || (pc.Is(CustomRoles.Workaholic) && Workaholic.Seen)
                            || pc.Is(CustomRoles.Rainbow))
                            NameColorManager.Add(seer.PlayerId, pc.PlayerId, pc.GetRoleColorCode());
                    }
                }

                RoleTypes[] RoleTypesList = { RoleTypes.Scientist, RoleTypes.Judge, RoleTypes.Engineer, RoleTypes.Noisemaker, RoleTypes.Tracker, RoleTypes.Shapeshifter, RoleTypes.Viper, RoleTypes.Phantom};
                foreach (var roleTypes in RoleTypesList)
                {
                    var roleOpt = Main.NormalOptions.roleOptions;
                    roleOpt.SetRoleRate(roleTypes, 0, 0);
                }
                GameEndChecker.SetPredicateToNormal();

                GameOptionsSender.AllSenders.Clear();
                foreach (var pc in Main.AllPlayerControls)
                {
                    GameOptionsSender.AllSenders.Add(
                        new PlayerGameOptionsSender(pc)
                    );
                }
            }

            /*
            //インポスターのゴーストロールがクルーになるバグ対策
            foreach (var pc in PlayerControl.AllPlayerControls)
            {
                if (pc.Data.Role.IsImpostor || Main.ResetCamPlayerList.Contains(pc.PlayerId))
                {
                    pc.Data.Role.DefaultGhostRole = RoleTypes.ImpostorGhost;
                }
            }
            */

            // MASSU_ORIGINAL_ROLE_ASSIGN_AB_V1
            CriticalStartFlowPacketDiagnostic.MarkAllPlayers("ROLE_STATE_BEFORE_COMMIT");
            MassuOriginalRoleAssignmentABV1.CommitCurrentRoles();
            CriticalStartFlowPacketDiagnostic.MarkRoleCommit();
            StartFlowReadiness.MarkRoleCommitted();
            CriticalStartFlowPacketDiagnostic.MarkAllPlayers("ROLE_STATE_AFTER_COMMIT");

            Utils.CountAlivePlayers(true);
            CriticalStartFlowPacketDiagnostic.Mark("SYNC_ALL_SETTINGS_CALL");
            Utils.SyncAllSettings();
            CriticalStartFlowPacketDiagnostic.Mark("SYNC_ALL_SETTINGS_RETURNED");
            SetColorPatch.IsAntiGlitchDisabled = false;
            CriticalStartFlowPacketDiagnostic.Mark("FINALIZE_ROLE_SELECTION_RETURN");
        }

        private static bool AssignDesyncRole(CustomRoles role, List<PlayerControl> AllPlayers, Dictionary<byte, CustomRpcSender> senders, Dictionary<(byte, byte), RoleTypes> rolesMap, RoleTypes BaseRole, RoleTypes hostBaseRole = RoleTypes.Crewmate,bool IsImpostorRole = false)
        {
            if (!role.IsPresent()) return false;

            var hostId = PlayerControl.LocalPlayer.PlayerId;
            var rand = IRandom.Instance;
            var realAssigned = 0;

            if (IsImpostorRole)
            {
                var impostorNum = Main.NormalOptions.GetInt(Int32OptionNames.NumImpostors);
                if (impostorNum == role.GetRealCount()) return false;
                if (Main.tempImpostorNum == 0)
                    Main.tempImpostorNum = impostorNum;
            }

            for (var i = 0; i < role.GetRealCount(); i++)
            {
                if (AllPlayers.Count <= 0) break;
                var player =
                    MassuOriginalRoleAssignmentABV1.TakeDesyncPlannedPlayer(role, AllPlayers) ??
                    AllPlayers[rand.Next(0, AllPlayers.Count)];
                if (role.IsImpostor() ||
                    role.IsNeutral() ||
                    role.IsAnimals())
                {
                    Logger.Info("非クルーカウント:" + player?.Data?.PlayerName + " = " + role.ToString(), "AssignRoles");
                    Main.NotCrewAssignCount.Add(player);
                }

                PetSettings.PetRoleCheck(role);
                AllPlayers.Remove(player);
                PlayerState.GetByPlayerId(player.PlayerId).SetMainRole(role);

                var selfRole = player.PlayerId == hostId && BaseRole != RoleTypes.Shapeshifter ? hostBaseRole : BaseRole;

                var othersRole = player.PlayerId == hostId ? RoleTypes.Crewmate : RoleTypes.Scientist;

                //Desync役職視点
                foreach (var target in Main.AllPlayerControls)
                {
                    if (player.PlayerId != target.PlayerId)
                    {
                        rolesMap[(player.PlayerId, target.PlayerId)] = othersRole;
                    }
                    else
                    {
                        rolesMap[(player.PlayerId, target.PlayerId)] = selfRole;
                    }
                }

                //他者視点
                foreach (var seer in Main.AllPlayerControls)
                {
                    if (player.PlayerId != seer.PlayerId)
                    {
                        rolesMap[(seer.PlayerId, player.PlayerId)] = othersRole;
                    }
                }
                RpcSetRoleReplacer.DesyncImpostorList.Add(player.PlayerId);
                //ホスト視点はロール決定
                if (player.PlayerId == hostId)
                {
                    player.StartCoroutine(player.CoSetRole(selfRole, false));
                }
                else
                {
                    player.StartCoroutine(player.CoSetRole(othersRole, false));
                }
                player.Data.IsDead = true;
                realAssigned++;

                Logger.Info("役職設定(desync):" + player?.Data?.PlayerName + " = " + role.ToString(), "AssignRoles");
            }

            if (IsImpostorRole) Main.NormalOptions.NumImpostors -= realAssigned;

            return realAssigned > 0;
        }
        public static void MakeDesyncSender(Dictionary<byte, CustomRpcSender> senders, Dictionary<(byte, byte), RoleTypes> rolesMap)
        {
            var hostId = PlayerControl.LocalPlayer.PlayerId;
            foreach (var seer in Main.AllPlayerControls)
            {
                var sender = senders[seer.PlayerId];
                foreach (var target in Main.AllPlayerControls)
                {
                    if (rolesMap.TryGetValue((seer.PlayerId, target.PlayerId), out var role))
                    {
                        QueueOrSendVanillaIntroRole(
                            seer,
                            target,
                            role,
                            senders);
                    }
                }
            }
        }

        // EVILHACKER_COMMON_STANDARD_PREPLAN_AB_V5
        // Defaultでは従来FinalizeRoleSelectionが消費していたmain-role RNGを
        // Release直前に同じrole順/pool順で1回だけ消費し、Finalizeではplanをcommitする。
        private static readonly Dictionary<byte, CustomRoles>
            DefaultStandardMainPlanABV5 = new();

        private static readonly Dictionary<CustomRoles, Queue<byte>>
            DefaultStandardOrderedPlayerPlanABV5 = new();

        private enum DefaultStandardPlanStateABV5
        {
            Unavailable,
            Building,
            Ready,
            Fatal,
        }

        private static DefaultStandardPlanStateABV5
            DefaultStandardPlanState = DefaultStandardPlanStateABV5.Unavailable;

        private const string CommonStandardPlanMarkerABV5 =
            "EVILHACKER_COMMON_STANDARD_PREPLAN_AB_V5";

        private static bool TryGetCommonStandardPlannedRoleABV5(
            byte playerId,
            out CustomRoles role)
        {
            if (MassuOriginalRoleAssignmentABV1.TryGetStandardPlannedRole(
                    playerId,
                    out role))
            {
                return true;
            }

            role = CustomRoles.NotAssigned;
            return DefaultStandardPlanState == DefaultStandardPlanStateABV5.Ready &&
                   DefaultStandardMainPlanABV5.TryGetValue(playerId, out role);
        }

        private static InvalidOperationException DefaultStandardPlanViolationABV5(
            string reason)
        {
            DefaultStandardPlanState = DefaultStandardPlanStateABV5.Fatal;
            Logger.Error(reason, CommonStandardPlanMarkerABV5);
            return new InvalidOperationException(
                $"{CommonStandardPlanMarkerABV5}: {reason}");
        }

        private static PlayerControl? TakeCommonStandardPlannedPlayerABV5(
            CustomRoles role,
            List<PlayerControl> candidates)
        {
            var massuPlayer =
                MassuOriginalRoleAssignmentABV1.TakeStandardPlannedPlayer(
                    role,
                    candidates);

            if (massuPlayer != null)
            {
                return massuPlayer;
            }

            // Unavailableだけが、従来random fallbackへ戻ってよい状態。
            // Building/Ready/Fatalは一度common plan経路へ入った状態なのでfail-closedする。
            if (DefaultStandardPlanState == DefaultStandardPlanStateABV5.Unavailable)
            {
                return null;
            }

            if (DefaultStandardPlanState != DefaultStandardPlanStateABV5.Ready ||
                candidates == null ||
                candidates.Count == 0)
            {
                throw DefaultStandardPlanViolationABV5(
                    $"PLAN_NOT_SELECTABLE State={DefaultStandardPlanState} " +
                    $"Role={role} Candidates={candidates?.Count ?? 0}");
            }

            if (!DefaultStandardOrderedPlayerPlanABV5.TryGetValue(
                    role,
                    out var orderedPlayerIds) ||
                orderedPlayerIds.Count == 0)
            {
                throw DefaultStandardPlanViolationABV5(
                    $"PLAN_SELECTOR_QUEUE_MISS Role={role} Candidates={candidates.Count}");
            }

            var plannedPlayerId = orderedPlayerIds.Dequeue();
            var plannedPlayer = candidates.FirstOrDefault(candidate =>
                candidate != null && candidate.PlayerId == plannedPlayerId);

            if (plannedPlayer == null ||
                !DefaultStandardMainPlanABV5.TryGetValue(
                    plannedPlayerId,
                    out var plannedRole) ||
                plannedRole != role)
            {
                throw DefaultStandardPlanViolationABV5(
                    $"PLAN_SELECTOR_MISS Role={role} PlannedPlayerId={plannedPlayerId} " +
                    $"Candidates={candidates.Count}");
            }

            return plannedPlayer;
        }

        private static void PlanDefaultStandardRoleFromListABV5(
            CustomRoles role,
            List<PlayerControl> players,
            IRandom random)
        {
            if (players == null || players.Count == 0) return;

            var count =
                Math.Clamp(
                    role.GetRealCount(),
                    0,
                    players.Count);

            if (count <= 0)
            {
                return;
            }

            if (!DefaultStandardOrderedPlayerPlanABV5.TryGetValue(
                    role,
                    out var orderedPlayerIds))
            {
                orderedPlayerIds = new Queue<byte>();
                DefaultStandardOrderedPlayerPlanABV5[role] = orderedPlayerIds;
            }

            for (var i = 0; i < count; i++)
            {
                if (players.Count <= 0)
                {
                    throw DefaultStandardPlanViolationABV5(
                        $"PLAN_POOL_EXHAUSTED Role={role} Index={i} Count={count}");
                }

                var player =
                    players[random.Next(0, players.Count)];

                if (player == null ||
                    DefaultStandardMainPlanABV5.ContainsKey(player.PlayerId))
                {
                    throw DefaultStandardPlanViolationABV5(
                        $"PLAN_DUPLICATE_SELECTION Role={role} Index={i}");
                }

                DefaultStandardMainPlanABV5[player.PlayerId] = role;
                orderedPlayerIds.Enqueue(player.PlayerId);
                players.Remove(player);
            }
        }

        private static void PrepareDefaultStandardPlanABV5(
            List<(PlayerControl, RoleTypes)> storagedData)
        {
            DefaultStandardMainPlanABV5.Clear();
            DefaultStandardOrderedPlayerPlanABV5.Clear();
            DefaultStandardPlanState = DefaultStandardPlanStateABV5.Unavailable;

            if (Options.CurrentGameMode != CustomGameMode.Standard ||
                Options.IsMassuOriginalRoleAssigning ||
                storagedData == null ||
                storagedData.Count == 0)
            {
                return;
            }

            var expectedPlayers =
                Main.AllPlayerControls
                    .Where(player =>
                    {
                        if (player == null || player.Data == null)
                        {
                            return false;
                        }

                        var state =
                            PlayerState.GetByPlayerId(player.PlayerId);

                        return state != null &&
                               state.MainRole == CustomRoles.NotAssigned;
                    })
                    .ToList();

            if (expectedPlayers.Count == 0)
            {
                return;
            }

            if (expectedPlayers
                    .GroupBy(player => player.PlayerId)
                    .Any(group => group.Count() != 1))
            {
                Logger.Error(
                    $"DUPLICATE_EXPECTED_PLAYER Players={expectedPlayers.Count}",
                    CommonStandardPlanMarkerABV5);
                return;
            }

            var nativeRoleByPlayer =
                new Dictionary<byte, RoleTypes>();

            var expectedIds =
                expectedPlayers
                    .Select(player => player.PlayerId)
                    .ToHashSet();

            foreach (var (player, nativeRole) in storagedData)
            {
                if (player == null ||
                    !expectedIds.Contains(player.PlayerId))
                {
                    continue;
                }

                if (nativeRoleByPlayer.ContainsKey(player.PlayerId))
                {
                    Logger.Error(
                        $"DUPLICATE_NATIVE PlayerId={player.PlayerId}",
                        CommonStandardPlanMarkerABV5);
                    return;
                }

                nativeRoleByPlayer[player.PlayerId] = nativeRole;
            }

            if (nativeRoleByPlayer.Count != expectedPlayers.Count)
            {
                Logger.Error(
                    $"NATIVE_COUNT_MISMATCH Expected={expectedPlayers.Count} Actual={nativeRoleByPlayer.Count}",
                    CommonStandardPlanMarkerABV5);
                return;
            }

            List<PlayerControl> Crewmates = new();
            List<PlayerControl> Impostors = new();
            List<PlayerControl> Scientists = new();
            List<PlayerControl> Engineers = new();
            List<PlayerControl> GuardianAngels = new();
            List<PlayerControl> Shapeshifters = new();
            List<PlayerControl> trackers = new();
            List<PlayerControl> noisemakers = new();
            List<PlayerControl> phantoms = new();

            foreach (var player in expectedPlayers)
            {
                if (!nativeRoleByPlayer.TryGetValue(
                        player.PlayerId,
                        out var nativeRole))
                {
                    return;
                }

                switch (nativeRole)
                {
                    case RoleTypes.Crewmate:
                        Crewmates.Add(player);
                        break;

                    case RoleTypes.Impostor:
                        Impostors.Add(player);
                        break;

                    case RoleTypes.Scientist:
                        Scientists.Add(player);
                        break;

                    case RoleTypes.Judge:
                        // FinalizeRoleSelectionではNormalJudgeとして確定し、poolへ戻さない。
                        break;

                    case RoleTypes.Engineer:
                        Engineers.Add(player);
                        break;

                    case RoleTypes.GuardianAngel:
                        GuardianAngels.Add(player);
                        break;

                    case RoleTypes.Shapeshifter:
                        Shapeshifters.Add(player);
                        break;

                    case RoleTypes.Viper:
                        // FinalizeRoleSelectionではNormalViperとして確定し、poolへ戻さない。
                        break;

                    case RoleTypes.Tracker:
                        trackers.Add(player);
                        break;

                    case RoleTypes.Noisemaker:
                        noisemakers.Add(player);
                        break;

                    case RoleTypes.Phantom:
                        phantoms.Add(player);
                        break;

                    default:
                        Logger.Error(
                            $"UNSUPPORTED_NATIVE PlayerId={player.PlayerId} Role={nativeRole}",
                            CommonStandardPlanMarkerABV5);
                        return;
                }
            }

            var poolPlayerIds = new[]
                {
                    Crewmates,
                    Impostors,
                    Scientists,
                    Engineers,
                    GuardianAngels,
                    Shapeshifters,
                    trackers,
                    noisemakers,
                    phantoms,
                }
                .SelectMany(pool => pool)
                .Select(player => player.PlayerId)
                .ToArray();

            if (poolPlayerIds.Length != poolPlayerIds.Distinct().Count())
            {
                Logger.Error(
                    $"DUPLICATE_POOL_PLAYER Entries={poolPlayerIds.Length}",
                    CommonStandardPlanMarkerABV5);
                return;
            }

            var random = IRandom.Instance;
            if (random == null)
            {
                Logger.Error("RANDOM_INSTANCE_NULL", CommonStandardPlanMarkerABV5);
                return;
            }

            // ここから先はmain-role RNGを消費し得るため、異常時は従来randomへ戻さない。
            DefaultStandardPlanState = DefaultStandardPlanStateABV5.Building;

            foreach (var role in CustomRolesHelper.AllStandardRoles)
            {
                if (role.IsVanilla()) continue;

                // NATIVE_WRAPPER_SLOT_AB_V1 と同じ除外。
                if (role is CustomRoles.NormalJudge or
                    CustomRoles.NormalPhantom or
                    CustomRoles.NormalViper)
                {
                    continue;
                }

                if (CustomRoleManager.GetRoleInfo(role)?.IsDesyncImpostor == true)
                    continue;

                if (role == CustomRoles.Opportunist &&
                    Opportunist.OptionCanKill.GetBool())
                    continue;

                if (role == CustomRoles.StrayWolf && AssignedStrayWolf)
                    continue;

                if (role is not CustomRoles.Opportunist and
                    not CustomRoles.StrayWolf and
                    not CustomRoles.Egoist &&
                    CustomRoleManager.GetRoleInfo(role)?.IsDesyncImpostor == true)
                    continue;

                var baseRoleTypes =
                    role.GetRoleTypes() switch
                    {
                        RoleTypes.Impostor => Impostors,
                        RoleTypes.Shapeshifter => Shapeshifters,
                        RoleTypes.Phantom => phantoms,
                        RoleTypes.Scientist => Scientists,
                        RoleTypes.Engineer => Engineers,
                        RoleTypes.Noisemaker => noisemakers,
                        RoleTypes.Tracker => trackers,
                        RoleTypes.GuardianAngel => GuardianAngels,
                        _ => Crewmates,
                    };

                PlanDefaultStandardRoleFromListABV5(
                    role,
                    baseRoleTypes,
                    random);
            }

            DefaultStandardPlanState = DefaultStandardPlanStateABV5.Ready;

            Logger.Info(
                $"PLAN_READY Algorithm=Default Players={expectedPlayers.Count} CustomAssignments={DefaultStandardMainPlanABV5.Count} RNG=EXACT_ONCE",
                CommonStandardPlanMarkerABV5);
        }

        private static List<PlayerControl> AssignCustomRolesFromList(CustomRoles role, List<PlayerControl> players, int RawCount = -1)
        {
            if (players == null || players.Count <= 0) return null;
            var rand = IRandom.Instance;
            var count = Math.Clamp(RawCount, 0, players.Count);
            if (RawCount == -1) count = Math.Clamp(role.GetRealCount(), 0, players.Count);
            if (count <= 0) return null;
            List<PlayerControl> AssignedPlayers = new();
            SetColorPatch.IsAntiGlitchDisabled = true;
            for (var i = 0; i < count; i++)
            {
                var player = TakeCommonStandardPlannedPlayerABV5(role, players);
                if (player == null)
                {
                    if (DefaultStandardPlanState != DefaultStandardPlanStateABV5.Unavailable)
                    {
                        throw DefaultStandardPlanViolationABV5(
                            $"PLAN_RANDOM_FALLBACK_BLOCKED Role={role} Index={i}");
                    }

                    player = players[rand.Next(0, players.Count)];
                }
                AssignedPlayers.Add(player);
                if (role.IsImpostor() ||
                    role.IsNeutral() ||
                    role.IsAnimals())
                {
                    Logger.Info("非クルーカウント:" + player?.Data?.PlayerName + " = " + role.ToString(), "AssignRoles");
                    Main.NotCrewAssignCount.Add(player);
                }
                PetSettings.PetRoleCheck(role);
                players.Remove(player);
                PlayerState.GetByPlayerId(player.PlayerId).SetMainRole(role);
                Logger.Info("役職設定:" + player?.Data?.PlayerName + " = " + role.ToString(), "AssignRoles");

                if (Options.CurrentGameMode == CustomGameMode.HideAndSeek)
                {
                    if (player.Is(CustomRoles.HASTroll))
                        player.RpcSetColor(2);
                    else if (player.Is(CustomRoles.HASFox))
                        player.RpcSetColor(3);
                }
            }
            SetColorPatch.IsAntiGlitchDisabled = false;
            return AssignedPlayers;
        }

        private static List<PlayerControl> AssignCustomSubRolesFromList(CustomRoles role, List<PlayerControl> allPlayersbySub, int RawCount = -1)
        {
            if (allPlayersbySub == null || allPlayersbySub.Count <= 0) return null;
            var rand = IRandom.Instance;
            var count = Math.Clamp(RawCount, 0, allPlayersbySub.Count);
            if (RawCount == -1) count = Math.Clamp(role.GetCount(), 0, allPlayersbySub.Count);
            if (count <= 0) return null;
            List<PlayerControl> AssignedPlayers = new();

            Dictionary<byte, List<byte>> loversDic = new();
            List<byte> Lovers = new();

            for (var i = 0; i < count; i++)
            {
                var player = allPlayersbySub[rand.Next(0, allPlayersbySub.Count)];
                AssignedPlayers.Add(player);
                if (role == CustomRoles.Lovers)
                {
                    Lovers.Add(player.PlayerId);
                    //奇数のプレイヤーを親とする(後選出が親)
                    if (i % 2 != 0)
                    {
                        loversDic.Add(player.PlayerId,Lovers);
                        //格納したので初期化
                        Lovers = new();
                    }
                }
                allPlayersbySub.Remove(player);
                PlayerState.GetByPlayerId(player.PlayerId).SetSubRole(role);
                Logger.Info("属性設定:" + player?.Data?.PlayerName + " = " + player.GetCustomRole().ToString() + " + " + role.ToString(), "AssignSubRoles");
            }
            if (role == CustomRoles.Lovers)
            {
                foreach (var team in loversDic)
                {
                    LoversManager.LoversMaster.Add(team.Key, new LoversTeam
                    {
                        LeaderId = team.Key,
                        Teams = team.Value,
                        IsDeath = false
                    });
                }
                RPC.SyncLoversPlayers();
            }

            return AssignedPlayers;
        }
        // GAMBLER_FACTION_ASSIGN_AB_V1:
        private static int GetGamblerFactionIndex(CustomRoles mainRole)
        {
            // 特殊陣営を先に判定して分類の重複を避ける。
            if (mainRole.IsAnimals()) return 4;
            if (mainRole.IsMadmate()) return 1;
            if (mainRole.IsImpostor()) return 0;
            if (mainRole.IsNeutral()) return 2;
            if (mainRole.IsCrewmate()) return 3;
            return -1;
        }

        private static int GetGamblerFactionMaximum(int factionIndex)
        {
            return factionIndex switch
            {
                0 => TownOfHostForE.Roles.AddOns.NotCrew.Gambler.ImpostorMaximum.GetInt(),
                1 => TownOfHostForE.Roles.AddOns.NotCrew.Gambler.MadmateMaximum.GetInt(),
                2 => TownOfHostForE.Roles.AddOns.NotCrew.Gambler.NeutralMaximum.GetInt(),
                3 => TownOfHostForE.Roles.AddOns.NotCrew.Gambler.CrewmateMaximum.GetInt(),
                4 => TownOfHostForE.Roles.AddOns.NotCrew.Gambler.AnimalsMaximum.GetInt(),
                _ => 0,
            };
        }

        // GAMBLER_FACTION_EXACT_B_PERMANENT:
        // B仕様: 5陣営の設定人数をそれぞれ独立した配役目標として扱う。
        // 既存Maximum(role.GetCount)はGamblerの共通Option UI互換のため残すが、
        // 実際のGambler付与人数を制限しない。候補不足時のみ設定人数未満になる。
        private static List<PlayerControl> AssignGamblerByFaction(int RawCount = -1)
        {
            _ = RawCount;

            // GAMBLER_MASTER_OFF_ASSIGNMENT_AB_V1: hidden retained faction values must never assign at 0%
            if (!TownOfHostForE.Roles.AddOns.NotCrew.Gambler.IsAssignmentEnabled) return null!;

            var candidates = Main.AllPlayerControls
                .Where(pc =>
                    pc != null &&
                    pc.Data != null &&
                    !pc.Data.Disconnected &&
                    !pc.Is(CustomRoles.God) &&
                    !pc.Is(CustomRoles.Gambler) &&
                    GetGamblerFactionIndex(pc.GetCustomRole()) >= 0)
                .Select(pc => pc!)
                .ToList();

            if (candidates.Count == 0) return null!;

            var rand = IRandom.Instance;
            List<PlayerControl> assignedPlayers = new();

            for (var factionIndex = 0; factionIndex < 5; factionIndex++)
            {
                var requestedCount = Math.Max(GetGamblerFactionMaximum(factionIndex), 0);
                if (requestedCount <= 0) continue;

                var factionCandidates = candidates
                    .Where(pc => GetGamblerFactionIndex(pc.GetCustomRole()) == factionIndex)
                    .ToList();

                var assignCount = Math.Min(requestedCount, factionCandidates.Count);

                for (var i = 0; i < assignCount; i++)
                {
                    var player = factionCandidates[rand.Next(0, factionCandidates.Count)];
                    factionCandidates.Remove(player);
                    candidates.Remove(player);
                    assignedPlayers.Add(player);

                    // 旧NotCrew経路の対象だった陣営について従来の除外side-effectも維持する。
                    Main.NotCrewAssignCount?.Remove(player);

                    PlayerState.GetByPlayerId(player.PlayerId).SetSubRole(CustomRoles.Gambler);
                    Logger.Info(
                        "ギャンブラー陣営別指定配役:" +
                        player.Data.PlayerName +
                        " = " +
                        player.GetCustomRole().ToString() +
                        " + Gambler / Faction=" +
                        factionIndex +
                        " / Requested=" +
                        requestedCount +
                        " / AssignedIndex=" +
                        (i + 1),
                        "AssignSubRoles");
                }
            }

            return assignedPlayers.Count > 0 ? assignedPlayers : null!;
        }

        private static List<PlayerControl> AssignCustomSubRolesFromListfromNotCrew(CustomRoles role, int RawCount = -1)
        {
            if (role == CustomRoles.Gambler)
                return AssignGamblerByFaction(RawCount);

            Logger.Info("カウント数:" + Main.NotCrewAssignCount.Count, "AssignRoles");
            if (Main.NotCrewAssignCount == null || Main.NotCrewAssignCount.Count <= 0) return null;
            var rand = IRandom.Instance;
            var count = Math.Clamp(RawCount, 0, Main.NotCrewAssignCount.Count);
            if (RawCount == -1) count = Math.Clamp(role.GetCount(), 0, Main.NotCrewAssignCount.Count);
            if (count <= 0) return null;
            List<PlayerControl> AssignedPlayers = new();

            for (var i = 0; i < count; i++)
            {
                // GOD_GAMBLER_LEGACY_GUARD_PERMANENT
                var eligiblePlayers = role == CustomRoles.Gambler
                    ? Main.NotCrewAssignCount.Where(pc => !pc.Is(CustomRoles.God)).ToList()
                    : Main.NotCrewAssignCount;
                if (eligiblePlayers.Count == 0) break;
                var player = eligiblePlayers[rand.Next(0, eligiblePlayers.Count)];
                AssignedPlayers.Add(player);
                Main.NotCrewAssignCount.Remove(player);
                PlayerState.GetByPlayerId(player.PlayerId).SetSubRole(role);
                Logger.Info("非クルー向け属性設定:" + player?.Data?.PlayerName + " = " + player.GetCustomRole().ToString() + " + " + role.ToString(), "AssignSubRoles");
            }
            if (role == CustomRoles.Lovers) RPC.SyncLoversPlayers();

            return AssignedPlayers;
        }

        private static List<PlayerControl> AssignLoversRolesFromList(List<PlayerControl> allPlayersbySub)
        {
            if (!CustomRoles.Lovers.IsEnable()) return null;
                ////Loversを初期化
                //Main.LoversPlayersV2.Clear();
                //Main.isLoversDeadV2.Clear();
                //ランダムに2人選出
                //AssignLoversRoles(2);
                return AssignCustomSubRolesFromList(CustomRoles.Lovers, allPlayersbySub, 2 * LoversManager.OptionMakeLoversPair.GetInt());
        }

        public static int GetRoleTypesCount(RoleTypes roleTypes)
        {
            int count = 0;
            foreach (var role in CustomRolesHelper.AllRoles)
            {
                if (role == CustomRoles.Opportunist && Opportunist.OptionCanKill.GetBool()) continue;
	            if (role is not CustomRoles.Opportunist &&
                    CustomRoleManager.GetRoleInfo(role)?.IsDesyncImpostor == true) continue;
                if (role == CustomRoles.Egoist && Main.NormalOptions.GetInt(Int32OptionNames.NumImpostors) <= 1) continue;
	            if (role.GetRoleTypes() == roleTypes)
	                count += role.GetRealCount();
            }
            return count;
        }


        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
        class RpcSetRoleReplacer
        {
            public static bool doReplace = false;
            public static Dictionary<byte, CustomRpcSender> senders;
            public static List<(PlayerControl, RoleTypes)> StoragedData = new();
            public static List<byte> DesyncImpostorList;
            public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] RoleTypes roleType)
            {
                if (doReplace && senders != null)
                {
                    StoragedData.Add((__instance, roleType));
                    return false;
                }
                else return true;
            }
            private const string CrewNativeInitialImpostorAnchorAbV11Marker =
                "CREW_NATIVE_INITIAL_IMPOSTOR_ANCHOR_AB_V11_ONLINE";

            private static bool IsImpostorFamilyAbV11(RoleTypes role)
            {
                return role is RoleTypes.Impostor or RoleTypes.Shapeshifter or RoleTypes.Viper or RoleTypes.Phantom;
            }

            private static void ApplyCrewNativeInitialImpostorAnchorAbV11()
            {
                // Online A/B only. Local behavior is intentionally unchanged.
                if (!GameStates.IsOnlineGame) return;

                foreach (var viewerId in
                         ClientReadyBarrierQueuedRoles.Keys
                             .OrderBy(id => id)
                             .ToArray())
                {
                    var viewer = Utils.GetPlayerById(viewerId);
                    if (viewer == null || !IsClientReadyBarrierViewer(viewer))
                        continue;

                    if (!ClientReadyBarrierQueuedRoles.TryGetValue(
                            viewerId,
                            out var queuedRoles) ||
                        queuedRoles == null ||
                        queuedRoles.Count == 0)
                    {
                        continue;
                    }

                    var impostorEntries =
                        Main.AllPlayerControls
                            .Where(target =>
                                target != null &&
                                VanillaIntroRoleMap.TryGetValue(
                                    (viewerId, target.PlayerId),
                                    out var mappedRole) &&
                                IsImpostorFamilyAbV11(mappedRole))
                            .OrderBy(target => target.PlayerId)
                            .ToList();

                    // A/B v11 is deliberately narrow:
                    // exactly one native Impostor-family target exists,
                    // and it is someone other than the Vanilla viewer.
                    // Impostor-replacement viewers whose sole Impostor is self are untouched.
                    if (impostorEntries.Count != 1 ||
                        impostorEntries[0].PlayerId == viewerId)
                    {
                        Logger.Info(
                            $"Stage=ANCHOR_NOT_REQUIRED | ViewerId={viewerId} | " +
                            $"ClientId={viewer.GetClientId()} | ImpostorEntries={impostorEntries.Count} | " +
                            $"SoleImpostorId={(impostorEntries.Count == 1 ? impostorEntries[0].PlayerId : byte.MaxValue)}",
                            CrewNativeInitialImpostorAnchorAbV11Marker);
                        continue;
                    }

                    var soleImpostor = impostorEntries[0];

                    // HOST_OWNER_SOLE_IMPOSTOR_INITIAL_ANCHOR_PERMANENT:
                    // Keep each Vanilla viewer's native topology stable when the sole
                    // native Impostor-family target is a MOD client, including the host owner.
                    // Vanilla-sole-Impostor cases remain untouched.
                    if (!soleImpostor.IsModClient())
                    {
                        Logger.Info(
                            $"Stage=REMOTE_MOD_SOLE_IMPOSTOR_NOT_REQUIRED | ViewerId={viewerId} | " +
                            $"ClientId={viewer.GetClientId()} | SoleImpostorId={soleImpostor.PlayerId} | " +
                            $"SoleImpostorIsOwner={soleImpostor.AmOwner} | SoleImpostorIsMod={soleImpostor.IsModClient()}",
                            "ZERO_NATIVE_IMPOSTOR_REMOTE_MOD_INITIAL_ANCHOR_AB_V24");
                        continue;
                    }

                    var anchorIndex = -1;
                    var fallbackIndex = -1;

                    for (var i = 0; i < queuedRoles.Count; i++)
                    {
                        var queued = queuedRoles[i];
                        var target = queued.Target;
                        if (target == null ||
                            target.Data == null ||
                            target.Data.Disconnected ||
                            target.PlayerId == viewerId ||
                            target.PlayerId == soleImpostor.PlayerId ||
                            IsImpostorFamilyAbV11(queued.Role))
                        {
                            continue;
                        }

                        if (fallbackIndex < 0)
                            fallbackIndex = i;

                        if (DesyncImpostorList != null &&
                            DesyncImpostorList.Contains(target.PlayerId))
                        {
                            anchorIndex = i;
                            break;
                        }
                    }

                    if (anchorIndex < 0)
                        anchorIndex = fallbackIndex;

                    if (anchorIndex < 0)
                    {
                        Logger.Info(
                            $"Stage=ANCHOR_NO_TARGET | ViewerId={viewerId} | " +
                            $"ClientId={viewer.GetClientId()} | SoleImpostorId={soleImpostor.PlayerId}",
                            CrewNativeInitialImpostorAnchorAbV11Marker);
                        continue;
                    }

                    var anchorEntry = queuedRoles[anchorIndex];
                    var anchor = anchorEntry.Target;
                    var previousRole = anchorEntry.Role;

                    Logger.Info(
                        $"Stage=REMOTE_MOD_SOLE_IMPOSTOR_ANCHOR_ARMED | ViewerId={viewerId} | " +
                        $"ClientId={viewer.GetClientId()} | SoleImpostorId={soleImpostor.PlayerId} | " +
                        $"AnchorId={anchor.PlayerId} | PreviousRole={previousRole} | AnchorRole={RoleTypes.Impostor}",
                        "ZERO_NATIVE_IMPOSTOR_REMOTE_MOD_INITIAL_ANCHOR_AB_V24");

                    var isPlannedEvilHackerAtomic =
                        TryGetCommonStandardPlannedRoleABV5(
                            soleImpostor.PlayerId,
                            out var plannedSoleRole) &&
                        plannedSoleRole == CustomRoles.EvilHacker &&
                        TownOfHostForE.Roles.Impostor.EvilHacker.SimpleAdminEnabledAtGameStart;

                    if (isPlannedEvilHackerAtomic)
                    {
                        // EVILHACKER_ATOMIC_ANCHOR_RESTORE_AB_V4:
                        // queued roleとpermanent mapはcanonicalのまま保持。
                        // fake I,true / canonical,false はbarrier builder内だけで同一rootに構築する。
                        ClientReadyBarrierAtomicAnchorByViewerABV4[viewerId] =
                            (anchor.PlayerId, previousRole, soleImpostor.PlayerId);

                        Logger.Info(
                            $"EVILHACKER_ATOMIC_ANCHOR_RESTORE_AB_V4 PLAN " +
                            $"Viewer={viewerId} ClientId={viewer.GetClientId()} " +
                            $"SoleImpostor={soleImpostor.PlayerId} Anchor={anchor.PlayerId} " +
                            $"Canonical={previousRole}",
                            CrewNativeInitialImpostorAnchorAbV11Marker);
                    }
                    else
                    {
                        // EVILHACKER_COMMON_PREPLAN_AB_V5:
                        // 非EvilHackerもv3C同様、Fake anchorはqueue/wire-only。
                        // VanillaIntroRoleMapはcanonical previousRoleを維持する。
                        queuedRoles[anchorIndex] =
                            (anchor, RoleTypes.Impostor);

                        Logger.Info(
                            $"Stage=INITIAL_ANCHOR_SET | ViewerId={viewerId} | " +
                            $"ClientId={viewer.GetClientId()} | SoleImpostorId={soleImpostor.PlayerId} | " +
                            $"AnchorId={anchor.PlayerId} | PreviousRole={previousRole} | " +
                            $"AnchorRole={RoleTypes.Impostor} | QueuedRoles={queuedRoles.Count}",
                            CrewNativeInitialImpostorAnchorAbV11Marker);
                    }
                }
            }
            public static void Release()
            {
                CriticalStartFlowPacketDiagnostic.Mark("ROLE_BUNDLE_RELEASE_ENTER");
                // MASSU_ORIGINAL_ROLE_ASSIGN_AB_V1
                MassuOriginalRoleAssignmentABV1.PrepareStandardPlan(StoragedData);
                PrepareDefaultStandardPlanABV5(StoragedData);

                foreach (var (player, role) in StoragedData)
                {
                    if (player == null) continue;

                    var isPlannedEvilHacker =
                        TryGetCommonStandardPlannedRoleABV5(
                            player.PlayerId,
                            out var plannedRole) &&
                        plannedRole == CustomRoles.EvilHacker;
                    var initialOwnerNativePhantom =
                        isPlannedEvilHacker &&
                        TownOfHostForE.Roles.Impostor.EvilHacker.SimpleAdminEnabledAtGameStart;
                    var canonicalRole = isPlannedEvilHacker
                        ? RoleTypes.Impostor
                        : role;

                    // Host owner自身は最初のlocal latchそのものをPhantom,falseにする。
                    var localRole =
                        initialOwnerNativePhantom && player.AmOwner
                            ? RoleTypes.Phantom
                            : role;
                    player.StartCoroutine(player.CoSetRole(localRole, false));

                    if (isPlannedEvilHacker && PlayerControl.LocalPlayer != null)
                    {
                        Logger.Info(
                            $"EVILHACKER_NATIVE_ROLE_PLAN PlayerId={player.PlayerId} " +
                            $"ClientId={player.GetClientId()} OwnerMod={player.IsModClient()} " +
                            $"SimpleAdminEnabled={TownOfHostForE.Roles.Impostor.EvilHacker.SimpleAdminEnabledAtGameStart} " +
                            $"ViewerId={PlayerControl.LocalPlayer.PlayerId} " +
                            $"ViewerClientId={PlayerControl.LocalPlayer.GetClientId()} " +
                            $"IsOwnerViewer={PlayerControl.LocalPlayer.PlayerId == player.PlayerId} " +
                            $"NativeRole={localRole} CanonicalRole={canonicalRole}",
                            "EvilHacker");
                    }

                    var impostorRole = role is RoleTypes.Impostor or RoleTypes.Shapeshifter or RoleTypes.Viper or RoleTypes.Phantom;
                    if (impostorRole && DesyncImpostorList.Count != 0)
                    {
                        foreach (var seer in Main.AllPlayerControls)
                        {
                            if (seer.PlayerId == 0) continue;
                            var assignRole = DesyncImpostorList.Contains(seer.PlayerId)
                                ? RoleTypes.Scientist
                                : role;

                            if (initialOwnerNativePhantom &&
                                seer.PlayerId == player.PlayerId)
                            {
                                assignRole = RoleTypes.Phantom;
                            }

                            if (isPlannedEvilHacker)
                            {
                                Logger.Info(
                                    $"EVILHACKER_NATIVE_ROLE_PLAN PlayerId={player.PlayerId} " +
                                    $"ClientId={player.GetClientId()} OwnerMod={player.IsModClient()} " +
                                    $"SimpleAdminEnabled={TownOfHostForE.Roles.Impostor.EvilHacker.SimpleAdminEnabledAtGameStart} " +
                                    $"ViewerId={seer.PlayerId} ViewerClientId={seer.GetClientId()} " +
                                    $"IsOwnerViewer={seer.PlayerId == player.PlayerId} " +
                                    $"NativeRole={assignRole} CanonicalRole={canonicalRole}",
                                    "EvilHacker");
                            }

                            QueueOrSendVanillaIntroRole(
                                seer,
                                player,
                                assignRole,
                                senders,
                                initialOwnerNativePhantom && seer.PlayerId == player.PlayerId
                                    ? canonicalRole
                                    : null);
                        }
                    }
                    else
                    {
                        // ROLE_DISPATCH_TARGETED_PERMANENT
                        foreach (var seer in Main.AllPlayerControls)
                        {
                            if (seer.AmOwner) continue;

                            var roleForViewer = role;
                            if (initialOwnerNativePhantom &&
                                seer.PlayerId == player.PlayerId)
                            {
                                roleForViewer = RoleTypes.Phantom;
                            }

                            if (isPlannedEvilHacker)
                            {
                                Logger.Info(
                                    $"EVILHACKER_NATIVE_ROLE_PLAN PlayerId={player.PlayerId} " +
                                    $"ClientId={player.GetClientId()} OwnerMod={player.IsModClient()} " +
                                    $"SimpleAdminEnabled={TownOfHostForE.Roles.Impostor.EvilHacker.SimpleAdminEnabledAtGameStart} " +
                                    $"ViewerId={seer.PlayerId} ViewerClientId={seer.GetClientId()} " +
                                    $"IsOwnerViewer={seer.PlayerId == player.PlayerId} " +
                                    $"NativeRole={roleForViewer} CanonicalRole={canonicalRole}",
                                    "EvilHacker");
                            }

                            QueueOrSendVanillaIntroRole(
                                seer,
                                player,
                                roleForViewer,
                                senders,
                                initialOwnerNativePhantom && seer.PlayerId == player.PlayerId
                                    ? canonicalRole
                                    : null);
                        }
                    }
                }

                ApplyCrewNativeInitialImpostorAnchorAbV11();
                CompleteClientReadyBarrierRoleBundles(senders);
                doReplace = false;
                CriticalStartFlowPacketDiagnostic.Mark("ROLE_BUNDLE_RELEASE_RETURN");
            }

            private static bool TrySendIntroBundle(byte viewerId, CustomRpcSender sender)
            {
                if (P4SuppressedIntroViewerIds.Contains(viewerId) ||
                    !Utils.GetPlayerById(viewerId).TryGetClientId(out _))
                {
                    P4SuppressedIntroViewerIds.Add(viewerId);
                    if (sender.CurrentState is not CustomRpcSender.State.Finished)
                        sender.stream.Recycle();
                    return false;
                }
                sender.SendMessage();
                return true;
            }

            public static void SendAllImmediately()
            {
                Release();

                if (senders == null) return;

                foreach (var kvp in senders)
                    TrySendIntroBundle(kvp.Key, kvp.Value);
            }
            public static System.Collections.IEnumerator SendAllDeferred(
                float intervalSeconds = 0.3f)
            {
                CriticalStartFlowPacketDiagnostic.Mark(
                    "ROLE_DISPATCH_DEFERRED_ENTER",
                    $"Interval={intervalSeconds}");
                Release();

                if (senders == null) yield break;

                var senderIndex = 0;
                foreach (var kvp in senders)
                {
                    Logger.Info(
                        $"BEFORE Index={senderIndex}, SenderPlayerId={kvp.Key}, Interval={intervalSeconds}",
                        "ROLE_DISPATCH_SEND_PERMANENT"
                    );
                    CriticalStartFlowPacketDiagnostic.Mark(
                        "ROLE_DISPATCH_SENDER_BEFORE",
                        $"Index={senderIndex} SenderPlayerId={kvp.Key} Interval={intervalSeconds}");

                    if (TrySendIntroBundle(kvp.Key, kvp.Value))
                    {
                        Logger.Info(
                            $"AFTER Index={senderIndex}, SenderPlayerId={kvp.Key}",
                            "ROLE_DISPATCH_SEND_PERMANENT"
                        );
                        CriticalStartFlowPacketDiagnostic.Mark(
                            "ROLE_DISPATCH_SENDER_AFTER",
                            $"Index={senderIndex} SenderPlayerId={kvp.Key}");
                    }

                    senderIndex++;

                    yield return new UnityEngine.WaitForSecondsRealtime(
                        intervalSeconds
                    );
                }
                CriticalStartFlowPacketDiagnostic.Mark(
                    "ROLE_DISPATCH_DEFERRED_RETURN",
                    $"SenderCount={senderIndex} Interval={intervalSeconds}");
            }
            public static void Cleanup()
            {
                P4SuppressedIntroViewerIds.Clear();
                senders = null;
                DesyncImpostorList = null;
                StoragedData = null;
                ClientReadyBarrierQueuedRoles.Clear();
                ClientReadyBarrierAtomicAnchorByViewerABV4.Clear();
            }
            public static void StartReplace(Dictionary<byte, CustomRpcSender> senders)
            {
                RpcSetRoleReplacer.senders = senders;
                StoragedData = new();
                DesyncImpostorList = new();
                doReplace = true;
            }
        }
    }
}
