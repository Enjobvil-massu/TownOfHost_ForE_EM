using System;
using System.Collections.Generic;
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
                sender.StartMessage(seer.GetClientId());

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

            ClearExileDisplaySafetyStatePermanent();
        }

        private static void ClearExileDisplaySafetyStatePermanent()
        {
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
        }
    }
}
