using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using UnityEngine;

using TownOfHostForE.Modules;
using TownOfHostForE.Roles;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Interfaces;
using TownOfHostForE.Roles.Neutral;
using TownOfHostForE.Roles.AddOns.Common;
using TownOfHostForE.Roles.AddOns.Crewmate;
using TownOfHostForE.Roles.Impostor;
using TownOfHostForE.Roles.Crewmate;
using TownOfHostForE.GameMode;

namespace TownOfHostForE
{
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckProtect))]
    class CheckProtectPatch
    {
        public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
        {
            if (!AmongUsClient.Instance.AmHost) return false;

            // RENEGADE_R2G_GLOBAL_PROTECT_LOCKDOWN
            if (Renegade.IsAwakeningActive())
            {
                Logger.Info(
                    $"R2G awakened chase: Protect blocked actor={__instance.PlayerId}",
                    "Renegade");
                return false;
            }

            Logger.Info("CheckProtect発生: " + __instance.GetNameWithRole() + "=>" + target.GetNameWithRole(), "CheckProtect");
            if (__instance.Is(CustomRoles.Sheriff)
                ||__instance.Is(CustomRoles.SillySheriff)
                ||__instance.Is(CustomRoles.Hunter)
                ||__instance.Is(CustomRoles.Metaton)
                ||__instance.Is(CustomRoles.MadSheriff))
            {
                if (__instance.Data.IsDead)
                {
                    Logger.Info("守護をブロックしました。", "CheckProtect");
                    return false;
                }
            }
            return true;
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))]
    class CheckMurderPatch
    {
        public static Dictionary<byte, float> TimeSinceLastKill = new();
        public static void Update()
        {
            for (byte i = 0; i < 15; i++)
            {
                if (TimeSinceLastKill.ContainsKey(i))
                {
                    TimeSinceLastKill[i] += Time.deltaTime;
                    if (15f < TimeSinceLastKill[i]) TimeSinceLastKill.Remove(i);
                }
            }
        }
        public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
        {
            if (!AmongUsClient.Instance.AmHost) return false;
            // ROLEBUGTRACE_STAGE1A_PERMANENT
            RoleBugTrace.KillAttempt(__instance, target);

            // RENEGADE_R2G_GLOBAL_ATTACK_LOCKDOWN
            // 覚醒レネゲイド本人の接触キルだけを通し、それ以外の攻撃入力は失敗扱い。
            if (Renegade.IsAwakeningActive() &&
                !Renegade.IsAwakeningPlayer(__instance))
            {
                Logger.Info(
                    $"R2G awakened chase: CheckMurder blocked killer={__instance.PlayerId} target={target?.PlayerId}",
                    "Renegade");
                __instance.RpcMurderPlayer(target, false);
                return false;
            }

            // 処理は全てCustomRoleManager側で行う
            if (!CustomRoleManager.OnCheckMurder(__instance, target))
            {
                // キル失敗
                __instance.RpcMurderPlayer(target, false);
            }

            return false;
        }

        // 不正キル防止チェック
        public static bool CheckForInvalidMurdering(MurderInfo info)
        {
            (var killer, var target) = info.AttemptTuple;

            // Killerが既に死んでいないかどうか
            if (!killer.IsAlive())
            {
                Logger.Info($"{killer.GetNameWithRole()}は死亡しているためキャンセルされました。", "CheckMurder");
                return false;
            }
            // targetがキル可能な状態か
            if (
                // PlayerDataがnullじゃないか確認
                target.Data == null ||
                // targetの状態をチェック
                target.inVent ||
                target.MyPhysics.Animations.IsPlayingEnterVentAnimation() ||
                target.MyPhysics.Animations.IsPlayingAnyLadderAnimation() ||
                target.inMovingPlat)
            {
                Logger.Info("targetは現在キルできない状態です。", "CheckMurder");
                return false;
            }
            // targetが既に死んでいないか
            if (!target.IsAlive())
            {
                Logger.Info("targetは既に死んでいたため、キルをキャンセルしました。", "CheckMurder");
                return false;
            }
            // 会議中のキルでないか
            if (MeetingHud.Instance != null)
            {
                Logger.Info("会議が始まっていたため、キルをキャンセルしました。", "CheckMurder");
                return false;
            }
            // ゲーム開始直後の強制会議前はキルを禁止
            if (Options.IsFirstTurnMeetingActive)
            {
                Logger.Info($"{killer.GetNameWithRole()}が強制会議前にキルしようとしたためキャンセルしました。", "CheckMurder");
                return false;
            }

            // 連打キルでないか
            float minTime = Mathf.Max(0.02f, AmongUsClient.Instance.Ping / 1000f * 6f); //※AmongUsClient.Instance.Pingの値はミリ秒(ms)なので÷1000
            //TimeSinceLastKillに値が保存されていない || 保存されている時間がminTime以上 => キルを許可
            //↓許可されない場合
            if (TimeSinceLastKill.TryGetValue(killer.PlayerId, out var time) && time < minTime)
            {
                Logger.Info("前回のキルからの時間が早すぎるため、キルをブロックしました。", "CheckMurder");
                return false;
            }
            TimeSinceLastKill[killer.PlayerId] = 0f;

            // HideAndSeek_キルボタンが使用可能か
            if ((Options.CurrentGameMode == CustomGameMode.HideAndSeek || Options.IsStandardHAS) && Options.HideAndSeekKillDelayTimer > 0)
            {
                Logger.Info("HideAndSeekの待機時間中だったため、キルをキャンセルしました。", "CheckMurder");
                return false;
            }
            // キルが可能なプレイヤーか(遠隔は除く)
            // ULTRASTAR_CONTACT_KILL_PERMANENT: キルボタンを持たない接触キルだけを許可
            // RENEGADE_R2D_CONTACT_KILL_GATE: 覚醒中レネゲイドも同じ設計で許可。
            if (!info.IsFakeSuicide
                && !killer.CanUseKillButton()
                && !killer.Is(CustomRoles.UltraStar)
                && !Renegade.CanUseAwakeningContactKill(killer))
            {
                Logger.Info(killer.GetNameWithRole() + "はKillできないので、キルはキャンセルされました。", "CheckMurder");
                return false;
            }
            return true;
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcMurderPlayer))]
    class RpcMurderPlayerPatch
    {
        private static readonly LogHandler logger = Logger.Handler(nameof(PlayerControl.RpcMurderPlayer));

        private static bool SendViperViewerRoleAbV1(
            PlayerControl viewer,
            PlayerControl killer,
            RoleTypes role,
            string stage)
        {
            try
            {
                if (!viewer.TryGetClientId(out var clientId)) return false;
                var sender = CustomRpcSender.Create(
                    "Viper Vanilla Dissolve Bridge AB V1 " + stage,
                    Hazel.SendOption.Reliable);

                sender.StartMessage(clientId);
                sender.StartRpc(killer.NetId, RpcCalls.SetRole)
                    .Write((ushort)role)
                    .Write(true)
                    .EndRpc();
                sender.EndMessage();
                sender.SendMessage();

                Logger.Info(
                    $"Stage={stage} | ViewerId={viewer.PlayerId} | " +
                    $"ViewerClientId={viewer.GetClientId()} | KillerId={killer.PlayerId} | " +
                    $"Role={role}",
                    "VIPER_VANILLA_DISSOLVE_BRIDGE_AB_V1");

                return true;
            }
            catch (System.Exception ex)
            {
                Logger.Exception(ex, "VIPER_VANILLA_DISSOLVE_BRIDGE_AB_V1");
                return false;
            }
        }

        private static void ArmViperVanillaDissolveBridgeAbV1(
            PlayerControl killer)
        {
            if (killer == null ||
                killer.Data == null ||
                killer.Data.Disconnected ||
                !killer.Is(CustomRoles.NormalViper))
            {
                return;
            }

            var restorePlan =
                new List<(PlayerControl Viewer, RoleTypes RestoreRole)>();

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

                if (!AntiBlackout.TryGetViewerNativeRoleForViperDissolveBridgeAbV1(
                        viewer,
                        killer,
                        out var restoreRole))
                {
                    continue;
                }

                if (restoreRole == RoleTypes.Viper)
                {
                    Logger.Info(
                        $"Stage=ALREADY_VIPER | ViewerId={viewer.PlayerId} | " +
                        $"ViewerClientId={viewer.GetClientId()} | KillerId={killer.PlayerId}",
                        "VIPER_VANILLA_DISSOLVE_BRIDGE_AB_V1");
                    continue;
                }

                if (!SendViperViewerRoleAbV1(
                        viewer,
                        killer,
                        RoleTypes.Viper,
                        "ARM"))
                {
                    continue;
                }

                restorePlan.Add((viewer, restoreRole));
            }

            if (restorePlan.Count == 0)
                return;

            var snapshot = restorePlan.ToArray();

            // Keep the temporary role only long enough for the already-imminent
            // native MurderPlayer packet to create ViperDeadBody locally.
            // The restore is viewer-only and never changes host canonical roles.
            _ = new LateTask(
                () =>
                {
                    foreach (var entry in snapshot)
                    {
                        var viewer = entry.Viewer;
                        if (viewer == null ||
                            viewer.Data == null ||
                            viewer.Data.Disconnected)
                        {
                            continue;
                        }

                        SendViperViewerRoleAbV1(
                            viewer,
                            killer,
                            entry.RestoreRole,
                            "RESTORE");
                    }
                },
                0.35f,
                "ViperVanillaDissolveBridgeABV1Restore");
        }

        public static void Prefix(PlayerControl __instance, PlayerControl target, ref bool didSucceed)
        {
            var attackSource = TownOfHostForE.Roles.Core.Class.AegisReflectionGuard.GetAttackSource(__instance, target);
            if (didSucceed && TownOfHostForE.Roles.Core.Class.AegisReflectionGuard.TryReflect(attackSource, target, "RpcMurderPlayer"))
            {
                didSucceed = false;
                return;
            }
            // RENEGADE_R2G_RPC_MURDER_LOCKDOWN
            // CheckMurderを通らない直接RPC経路もHost側で最終遮断する。
            // 覚醒レネゲイド本人（接触キル/タイムアウト自己死）は除外。
            if (didSucceed &&
                AmongUsClient.Instance.AmHost &&
                Renegade.IsAwakeningActive() &&
                !Renegade.IsAwakeningPlayer(__instance))
            {
                didSucceed = false;
                logger.Info(
                    $"R2G awakened chase: RpcMurderPlayer blocked killer={__instance.PlayerId} target={target?.PlayerId}");
                return;
            }

            // RENEGADE_R2B_RPC_MURDER_GUARD
            if (didSucceed &&
                Renegade.ShouldBlockPlayerCausedMurder(
                    __instance,
                    target,
                    "RpcMurderPlayer"))
            {
                didSucceed = false;
                logger.Info("レネゲイド覚醒無敵により成功Murder RPCを失敗扱いへ変更");
                return;
            }

            if (didSucceed)
            {
                if (target.shapeshifting)
                {
                    //シェイプシフトアニメーション中
                    //アニメーション時間を考慮して1s、加えてクライアントとのラグを考慮して+0.5s遅延する
                    _ = new LateTask(
                        () =>
                        {
                            if (GameStates.IsInTask && target.Data != null && !target.Data.Disconnected && target.IsAlive())
                            {
                                if (TownOfHostForE.Roles.Core.Class.AegisReflectionGuard.TryReflect(attackSource, target, "DelayedShapeshiftKill")) return;
                                target.RpcShapeshift(target, false);
                                Camouflage.RpcSetSkin(target, ForceRevert: true, RevertToDefault: true);
                                TownOfHostForE.Roles.Core.Class.AegisReflectionGuard.RunAttack(attackSource, target, () => target.RpcMurderPlayer(target, true));
                            }
                        },
                        1.5f, "RevertShapeshift");
                    didSucceed = false;
                    logger.Info($"{__instance.GetNameWithRole()} => {target.GetNameWithRole()}(シェイプシフト中のため遅延)");
                    return;
                }
                if (Main.CheckShapeshift.TryGetValue(target.PlayerId, out var shapeshifting) && shapeshifting)
                {
                    //シェイプシフト強制解除
                    target.RpcShapeshift(target, false);
                }
                Camouflage.RpcSetSkin(target, ForceRevert: true, RevertToDefault: true);
            }

            // VIEWER_NATIVE_TOPOLOGY_LIFECYCLE_AB_V27:
            // RpcMurderPlayer本体のMurder RPC送信前にsole sentinelを交代。
            if (didSucceed && AmongUsClient.Instance.AmHost)
            {
                AntiBlackout.TryRotateViewerNativeSentinelBeforeMurderAbV27(target);

                // VIPER_VANILLA_DISSOLVE_BRIDGE_AB_V1:
                // Arm only after the victim-sentinel rotation so restore roles
                // represent the latest viewer-effective native topology.
                if (__instance.Is(CustomRoles.NormalViper))
                    ArmViperVanillaDissolveBridgeAbV1(__instance);
            }

            logger.Info($"{__instance.GetNameWithRole()} => {target.GetNameWithRole()}(didSucceed:{didSucceed})");
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
    class MurderPlayerPatch
    {
        private static readonly LogHandler logger = Logger.Handler(nameof(PlayerControl.MurderPlayer));
        public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target, [HarmonyArgument(1)] MurderResultFlags resultFlags, ref bool __state /* 成功したキルかどうか */ )
        {
            logger.Info($"{__instance.GetNameWithRole()} => {target.GetNameWithRole()}({resultFlags})");
            var isProtectedByClient = resultFlags.HasFlag(MurderResultFlags.DecisionByHost) && target.IsProtected();
            var isProtectedByHost = resultFlags.HasFlag(MurderResultFlags.FailedProtected);
            var isFailed = resultFlags.HasFlag(MurderResultFlags.FailedError);
            var isSucceeded = __state = !isProtectedByClient && !isProtectedByHost && !isFailed;

            // A reflected death bypasses role/Guardian defenses, but uses the normal death postfix.
            if (TownOfHostForE.Roles.Core.Class.AegisReflectionGuard.IsReflectionDeath(target))
            {
                __state = true;
                if (AmongUsClient.Instance.AmHost)
                    AntiBlackout.TryRotateViewerNativeSentinelBeforeDeathAbV28(target, "MURDER_LOCAL_PREFIX");
                return true;
            }
            if (isSucceeded && TownOfHostForE.Roles.Core.Class.AegisReflectionGuard.TryReflect(TownOfHostForE.Roles.Core.Class.AegisReflectionGuard.GetAttackSource(__instance, target), target, "MurderPlayer"))
            {
                __state = false;
                return false;
            }

            // RENEGADE_R2G_LOCAL_MURDER_LOCKDOWN
            // Balancer/Penguin等の直接MurderPlayer経路もHost canonical stateで遮断。
            if (isSucceeded &&
                AmongUsClient.Instance.AmHost &&
                Renegade.IsAwakeningActive() &&
                !Renegade.IsAwakeningPlayer(__instance))
            {
                __state = false;
                logger.Info(
                    $"R2G awakened chase: local MurderPlayer blocked killer={__instance.PlayerId} target={target?.PlayerId}");
                return false;
            }

            // RENEGADE_R2B_LOCAL_MURDER_GUARD
            // Failed/protected MurderPlayer results remain untouched.
            if (isSucceeded &&
                Renegade.ShouldBlockPlayerCausedMurder(
                    __instance,
                    target,
                    "MurderPlayer"))
            {
                __state = false;
                return false;
            }

            // VIEWER_NATIVE_TOPOLOGY_OUTCOME_BARRIER_AB_V28:
            // Catch direct local MurderPlayer paths (Balancer/Penguin/etc.) that
            // do not necessarily pass RpcMurderPlayer first.
            if (isSucceeded && AmongUsClient.Instance.AmHost)
            {
                AntiBlackout.TryRotateViewerNativeSentinelBeforeDeathAbV28(
                    target,
                    "MURDER_LOCAL_PREFIX");
            }

            // ROLEBUGTRACE_STAGE1A_PERMANENT
            RoleBugTrace.KillResult(__instance, target, isSucceeded, resultFlags.ToString());
            if (isProtectedByClient)
            {
                logger.Info("守護されているため，キルは失敗します");
            }
            if (isProtectedByHost)
            {
                logger.Info("守護されているため，キルはホストによってキャンセルされました");
            }
            if (isFailed)
            {
                logger.Info("キルはホストによってキャンセルされました");
            }

            if (isSucceeded)
            {
                logger.Info("キルは実行されます");
            }

            return true;
        }
        public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target, bool __state)
        {
            // キルが成功していない場合，何もしない
            if (!__state)
            {
                return;
            }
            if (target.AmOwner) RemoveDisableDevicesPatch.UpdateDisableDevices();
            if (!target.Data.IsDead || !AmongUsClient.Instance.AmHost) return;
            //以降ホストしか処理しない
            // 処理は全てCustomRoleManager側で行う
            CustomRoleManager.OnMurderPlayer(__instance, target);
        }
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckShapeshift))]


    [HarmonyPatch(typeof(PlayerControl), "Exiled")]
    class ViewerNativeTopologyExiledAbV28Patch
    {
        public static bool Prefix(PlayerControl __instance)
        {
            // RENEGADE_R2B_EXILED_BACKSTOP
            if (Renegade.ShouldBlockForcedExile(
                    __instance,
                    "PlayerControl.Exiled"))
            {
                return false;
            }

            AntiBlackout.TryRotateViewerNativeSentinelBeforeDeathAbV28(
                __instance,
                "EXILED_PREFIX");

            return true;
        }
    }

    public static class PlayerControlCheckShapeshiftPatch
    {
        private static readonly LogHandler logger = Logger.Handler(nameof(PlayerControl.CheckShapeshift));

        public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target, [HarmonyArgument(1)] bool shouldAnimate)
        {
            if (AmongUsClient.Instance.IsGameOver || !AmongUsClient.Instance.AmHost)
            {
                return false;
            }

            // RENEGADE_R2G_GLOBAL_SHAPESHIFT_LOCKDOWN
            // 新規変身だけ拒否し、既に変身中のself-target解除は通す。
            if (Renegade.IsAwakeningActive() &&
                target != null &&
                __instance.PlayerId != target.PlayerId)
            {
                logger.Info(
                    $"R2G awakened chase: shapeshift blocked player={__instance.PlayerId} target={target.PlayerId}");
                __instance.RpcRejectShapeshift();
                return false;
            }

            // 無効な変身を弾く．これより前に役職等の処理をしてはいけない
            if (!CheckInvalidShapeshifting(__instance, target!, shouldAnimate))
            {
                __instance.RpcRejectShapeshift();
                return false;
            }

            var shapeshifter = __instance;
            var shapeshifting = shapeshifter.PlayerId != target.PlayerId;
            // ROLEBUGTRACE_STAGE1B_PERMANENT: generic validity gates passed
            RoleBugTrace.ShapeshiftAttempt(shapeshifter, target);
            // 変身したとき一番近い人をマッドメイトにする処理
            if (shapeshifter.CanMakeMadmate() && shapeshifting)
            {
                var sidekickable = shapeshifter.GetRoleClass() as ISidekickable;
                var targetRole = sidekickable?.SidekickTargetRole ?? CustomRoles.SKMadmate;

                Vector2 shapeshifterPosition = shapeshifter.transform.position;//変身者の位置
                Dictionary<PlayerControl, float> mpdistance = new();
                float dis;
                foreach (var p in Main.AllAlivePlayerControls)
                {
                    if (p.Data.Role.Role != RoleTypes.Shapeshifter && !p.Is(CustomRoleTypes.Impostor) && !p.Is(targetRole))
                    {
                        dis = Vector2.Distance(shapeshifterPosition, p.transform.position);
                        mpdistance.Add(p, dis);
                    }
                }
                if (mpdistance.Count != 0)
                {
                    var min = mpdistance.OrderBy(c => c.Value).FirstOrDefault();//一番値が小さい
                    PlayerControl targetm = min.Key;
                    targetm.RpcSetCustomRole(targetRole);
                    Logger.Info($"Make SKMadmate:{targetm.name}", "Shapeshift");
                    Main.SKMadmateNowCount++;
                    Utils.MarkEveryoneDirtySettings();
                    Utils.NotifyRoles();
                }
            }
            // 役職の処理
            var role = shapeshifter.GetRoleClass();
            if (role?.OnCheckShapeshift(target, ref shouldAnimate) == false)
            {
                if (role.CanDesyncShapeshift)
                {
                    shapeshifter.RpcSpecificRejectShapeshift(target, shouldAnimate);
                }
                else
                {
                    shapeshifter.RpcRejectShapeshift();
                }
                return false;
            }

            shapeshifter.RpcShapeshift(target, shouldAnimate);
            return false;
        }
        private static bool CheckInvalidShapeshifting(PlayerControl instance, PlayerControl target, bool animate)
        {
            logger.Info($"Checking shapeshift {instance.GetNameWithRole()} -> {(target == null || target.Data == null ? "(null)" : target.GetNameWithRole())}");

            if (target is null || !target || target.Data == null)
            {
                logger.Info("targetがnullのため変身をキャンセルします");
                return false;
            }
            if (!instance.IsAlive())
            {
                logger.Info("変身者が死亡しているため変身をキャンセルします");
                return false;
            }
            // RoleInfoによるdesyncシェイプシフター用の判定を追加
            if (instance.Data.Role.Role != RoleTypes.Shapeshifter && instance.GetCustomRole().GetRoleInfo()?.BaseRoleType?.Invoke() != RoleTypes.Shapeshifter)
            {
                logger.Info("変身者がシェイプシフターではないため変身をキャンセルします");
                return false;
            }
            if (instance.Data.Disconnected)
            {
                logger.Info("変身者が切断済のため変身をキャンセルします");
                return false;
            }
            if (target.IsMushroomMixupActive() && animate)
            {
                logger.Info("キノコカオス中のため変身をキャンセルします");
                return false;
            }
            if (MeetingHud.Instance && animate)
            {
                logger.Info("会議中のため変身をキャンセルします");
                return false;
            }
            return true;
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Shapeshift))]
    class ShapeshiftPatch
    {
        public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
        {
            Logger.Info($"{__instance?.GetNameWithRole()} => {target?.GetNameWithRole()}", "Shapeshift");

            var shapeshifter = __instance;
            var shapeshifting = shapeshifter.PlayerId != target.PlayerId;

            if (Main.CheckShapeshift.TryGetValue(shapeshifter.PlayerId, out var last) && last == shapeshifting)
            {
                Logger.Info($"{__instance?.GetNameWithRole()}:Cancel Shapeshift.Prefix", "Shapeshift");
                return;
            }

            Main.CheckShapeshift[shapeshifter.PlayerId] = shapeshifting;
            Main.ShapeshiftTarget[shapeshifter.PlayerId] = target.PlayerId;
            // ROLEBUGTRACE_STAGE1B_PERMANENT: duplicate state guard passed
            RoleBugTrace.Shapeshift(shapeshifter, target, shapeshifting);

            shapeshifter.GetRoleClass()?.OnShapeshift(target);
            GreatDetective.TargetOnShapeShifft(shapeshifter, shapeshifting);
            // ROLEBUGTRACE_SHAPESHIFT_AFTER_PERMANENT_CALL
            // 役職OnShapeshift等の適用後stateを中央診断へ記録する。
            RoleBugTrace.ShapeshiftAfter(shapeshifter, target, shapeshifting);

            if (!AmongUsClient.Instance.AmHost) return;

            if (!shapeshifting) Camouflage.RpcSetSkin(__instance);

            //変身解除のタイミングがずれて名前が直せなかった時のために強制書き換え
            if (!shapeshifting)
            {
                _ = new LateTask(() =>
                {
                    Utils.NotifyRoles(NoCache: true);
                },
                1.2f, "ShapeShiftNotify");
            }
        }

        public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
        {
            // SHAPESHIFT_DEATH_OUTFIT_RESTORE_AB_V1:
            // OnShapeshift is invoked from ShapeshiftPatch.Prefix before the native Shapeshift body.
            // A role can die there; the outer native Shapeshift may then reapply the target outfit.
            // Restore only players that actually died during a real shapeshift start.
            if (!AmongUsClient.Instance.AmHost ||
                __instance == null ||
                target == null ||
                __instance.PlayerId == target.PlayerId)
            {
                return;
            }

            var player = __instance;
            _ = new LateTask(
                () =>
                {
                    if (player == null ||
                        player.Data == null ||
                        player.Data.Disconnected)
                    {
                        return;
                    }

                    var playerState = PlayerState.GetByPlayerId(player.PlayerId);
                    var isDead =
                        player.Data.IsDead ||
                        (playerState?.IsDead ?? false);

                    if (!isDead)
                    {
                        return;
                    }

                    var trackedShapeshifting =
                        Main.CheckShapeshift.TryGetValue(player.PlayerId, out var tracked) &&
                        tracked;

                    var nativeOutfitNotDefault =
                        player.CurrentOutfitType != PlayerOutfitType.Default;

                    if (!trackedShapeshifting && !nativeOutfitNotDefault)
                    {
                        return;
                    }

                    Logger.Info(
                        $"PlayerId={player.PlayerId} | DataIsDead={player.Data.IsDead} | PlayerStateIsDead={playerState?.IsDead} | CurrentOutfitType={player.CurrentOutfitType} | TrackedShapeshifting={trackedShapeshifting}",
                        "SHAPESHIFT_DEATH_OUTFIT_RESTORE_AB_V1");

                    player.RpcShapeshift(player, false);
                    Camouflage.RpcSetSkin(
                        player,
                        ForceRevert: true,
                        RevertToDefault: true);
                },
                0.1f,
                "ShapeshiftDeathOutfitRestoreABV1");
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.ReportDeadBody))]
    class ReportDeadBodyPatch
    {
        public static NetworkedPlayerInfo reporter;
        public static NetworkedPlayerInfo ReportTarget;

        public static Dictionary<byte, bool> CanReport;
        public static Dictionary<byte, bool> CanReportByDeadBody;
        public static Dictionary<byte, List<NetworkedPlayerInfo>> WaitReport = new();
        public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] NetworkedPlayerInfo target)
        {
            if (GameStates.IsMeeting) return false;
            if (Options.IsFirstTurnMeetingActive)
            {
                Logger.Info($"{__instance.GetNameWithRole()}の通報をゲーム開始直後の強制会議前のためキャンセルしました。", "ReportDeadBody");
                return false;
            }
            // RENEGADE_R2A_GLOBAL_REPORT_BLOCK
            // Host authority: 覚醒中は死体通報も緊急会議も全員拒否。
            // reporter / ReportTarget / WaitReport / UsedButtonCount より前で止める。
            if (AmongUsClient.Instance.AmHost &&
                Renegade.IsAwakeningActive())
            {
                Logger.Warn(
                    $"レネゲイド覚醒中のため通報/緊急会議を拒否: " +
                    $"playerId={__instance.PlayerId} " +
                    $"target={(target == null ? "BUTTON" : "BODY")}",
                    "Renegade");

                return false;
            }

            reporter = __instance.Data;
            ReportTarget = target;
            Logger.Info($"{__instance.GetNameWithRole()} => {target?.Object?.GetNameWithRole() ?? "null"}", "ReportDeadBody");
            if (Options.IsStandardHAS && target != null && __instance == target.Object) return true; //[StandardHAS] ボタンでなく、通報者と死体が同じなら許可
            if (Options.CurrentGameMode == CustomGameMode.HideAndSeek || Options.IsStandardHAS) return false;
            if (Options.CurrentGameMode == CustomGameMode.SuperBombParty) return false;
            if (reporter.Object.Is(CustomRoles.NonReport) &&
                target != null && !target.Object.Is(CustomRoles.Bait) && !target.Object.Is(CustomRoles.AddBait)) return false;

            // Scavenger
            if (target != null && !CanReportByDeadBody[target.PlayerId]) return false;

            if (!CanReport[__instance.PlayerId])
            {
                WaitReport[__instance.PlayerId].Add(target);
                Logger.Warn($"{__instance.GetNameWithRole()}:通報禁止中のため可能になるまで待機します", "ReportDeadBody");
                return false;
            }
            //if (target != null && !Vulture.VultureReport(__instance, target.PlayerId)) return false;
            if (!AmongUsClient.Instance.AmHost) return true;

            //通報者が死んでいる場合、本処理で会議がキャンセルされるのでここで止める
            if (__instance.Data.IsDead) return false;

            // RENEGADE_EMERGENCY_INPUT_R1: first press is deferred for an offering candidate.
            // A second press inside 3 seconds continues the existing normal meeting path exactly once.
            // Keep this before SyncButtonMode / UsedButtonCount and every meeting side effect.
            if (target == null && Renegade.ShouldBlockEmergency(__instance))
                return false;

            if (Options.SyncButtonMode.GetBool() && target == null)
            {
                Logger.Info("最大:" + Options.SyncedButtonCount.GetInt() + ", 現在:" + Options.UsedButtonCount, "ReportDeadBody");
                if (Options.SyncedButtonCount.GetFloat() <= Options.UsedButtonCount)
                {
                    Logger.Info("使用可能ボタン回数が最大数を超えているため、ボタンはキャンセルされました。", "ReportDeadBody");
                    return false;
                }
                else Options.UsedButtonCount++;
                if (Options.SyncedButtonCount.GetFloat() == Options.UsedButtonCount)
                {
                    Logger.Info("使用可能ボタン回数が最大数に達しました。", "ReportDeadBody");
                }
            }

            //デスゲーム中でボタンならキャンセル
            if (DarkGameMaster.IsDeathGameTime && target == null)
                return false;

            //=============================================
            //以下、ボタンが押されることが確定したものとする。
            //=============================================
            GameStates.InTask = false;

            Main.isFirstTurn = false;
            MeetingDisplay.CreateButtonInfo(__instance,target);
            Tiikawa.ChangeTiikawaRole();
            foreach (var role in CustomRoleManager.AllActiveRoles.Values)
            {
                bool result =  role.OnReportDeadBody(__instance, target);
                if (!result)
                {
                    return result;
                }
            }

            Main.AllPlayerControls
                .Where(pc => Main.CheckShapeshift.ContainsKey(pc.PlayerId))
                .Do(pc => Camouflage.RpcSetSkin(pc, RevertToDefault: true));
            MeetingTimeManager.OnReportDeadBody();

            Utils.NotifyRoles(isForMeeting: true, NoCache: true);
            

            Utils.SyncAllSettings();

            if (ShapeKiller.DummyReportDeadBody(__instance, target)) return false;
            // ROLEBUGTRACE_STAGE1A_PERMANENT: all report cancellation gates have passed
            RoleBugTrace.Report(__instance, target?.Object, target == null);
            return true;
        }
        public static async void ChangeLocalNameAndRevert(string name, int time)
        {
            //async Taskじゃ警告出るから仕方ないよね。
            var revertName = PlayerControl.LocalPlayer.name;
            PlayerControl.LocalPlayer.RpcSetNameEx(name);
            await Task.Delay(time);
            PlayerControl.LocalPlayer.RpcSetNameEx(revertName);
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
    public static class PlayerControlStartMeetingPatch
    {
        public static void Prefix()
        {
            foreach (var kvp in PlayerState.AllPlayerStates)
            {
                var pc = Utils.GetPlayerById(kvp.Key);
                kvp.Value.LastRoom = pc.GetPlainShipRoom();
            }
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
    class FixedUpdatePatch
    {
        private static StringBuilder Mark = new(20);
        private static StringBuilder Suffix = new(120);
        public static void Postfix(PlayerControl __instance)
        {
            var player = __instance;

            if (!GameStates.IsModHost) return;

            TargetArrow.OnFixedUpdate(player);
            VentSelect.OnFixedUpdate(player);
            CustomRoleManager.OnFixedUpdate(player);

            if (AmongUsClient.Instance.AmHost)
            {
                //実行クライアントがホストの場合のみ実行
                if (GameStates.IsLobby && (ModUpdater.hasUpdate || ModUpdater.isBroken || !Main.AllowPublicRoom || !VersionChecker.IsSupported || !Main.IsPublicAvailableOnThisVersion) && AmongUsClient.Instance.IsGamePublic)
                    AmongUsClient.Instance.ChangeGamePublic(false);

                if (GameStates.IsInTask && ReportDeadBodyPatch.CanReport[__instance.PlayerId] && ReportDeadBodyPatch.WaitReport[__instance.PlayerId].Count > 0)
                {
                    var info = ReportDeadBodyPatch.WaitReport[__instance.PlayerId][0];
                    ReportDeadBodyPatch.WaitReport[__instance.PlayerId].Clear();
                    Logger.Info($"{__instance.GetNameWithRole()}:通報可能になったため通報処理を行います", "ReportDeadbody");
                    __instance.ReportDeadBody(info);
                }

                DoubleTrigger.OnFixedUpdate(player);

                //ターゲットのリセット
                if (GameStates.IsInTask && player.IsAlive() && Options.LadderDeath.GetBool())
                {
                    FallFromLadder.FixedUpdate(player);
                }

                if (GameStates.IsInGame) LoversManager.LoversSuicide();

                if (GameStates.IsInGame && player.AmOwner)
                    DisableDevice.FixedUpdate();

                if (__instance.AmOwner)
                {
                    Utils.ApplySuffix();
                }
            }
            //LocalPlayer専用
            if (__instance.AmOwner)
            {
                //キルターゲットの上書き処理
                //if (GameStates.IsInTask && !(__instance.Is(CustomRoleTypes.Impostor) || __instance.Is(CustomRoles.Egoist)) && __instance.CanUseKillButton() && !__instance.Data.IsDead)
                if (GameStates.IsInTask && !((__instance.Is(CustomRoleTypes.Impostor) && !__instance.Is(CustomRoles.StrayWolf)) || __instance.Is(CustomRoles.Egoist)) && __instance.CanUseKillButton() && !__instance.Data.IsDead)
                {
                    var players = __instance.GetPlayersInAbilityRangeSorted(false);
                    PlayerControl closest = players.Count <= 0 ? null : players[0];
                    HudManager.Instance.KillButton.SetTarget(closest);
                }
            }

            //役職テキストの表示
            var RoleTextTransform = __instance.cosmetics.nameText.transform.Find("RoleText");
            var RoleText = RoleTextTransform.GetComponent<TMPro.TextMeshPro>();
            if (RoleText != null && __instance != null)
            {
                if (GameStates.IsLobby)
                {
                    if (Main.playerVersion.TryGetValue(__instance.PlayerId, out var ver))
                    {
                        if (Main.ForkId != ver.forkId) // フォークIDが違う場合
                            __instance.cosmetics.nameText.text = $"<color=#ff0000><size=1.2>{ver.forkId}</size>\n{__instance?.name}</color>";
                        else if (Main.version.CompareTo(ver.version) == 0)
                            __instance.cosmetics.nameText.text = ver.tag == RPC.CurrentVersionTag ? $"<color=#87cefa>{__instance.name}</color>" : $"<color=#ffff00><size=1.2>{ver.tag}</size>\n{__instance?.name}</color>";
                        else __instance.cosmetics.nameText.text = $"<color=#ff0000><size=1.2>v{ver.version}</size>\n{__instance?.name}</color>";
                    }
                    else __instance.cosmetics.nameText.text = __instance?.Data?.PlayerName;
                }
                if (GameStates.IsInGame)
                {
                    //if (Options.CurrentGameMode == CustomGameMode.HideAndSeek)
                    //{
                    //    var hasRole = main.AllPlayerCustomRoles.TryGetValue(__instance.PlayerId, out var role);
                    //    if (hasRole) RoleTextData = Utils.GetRoleTextHideAndSeek(__instance.Data.Role.Role, role);
                    //}
                    (RoleText.enabled, RoleText.text) = Utils.GetRoleNameAndProgressTextData(false,PlayerControl.LocalPlayer, __instance);
                    if (!AmongUsClient.Instance.IsGameStarted && AmongUsClient.Instance.NetworkMode != NetworkModes.FreePlay)
                    {
                        RoleText.enabled = false; //ゲームが始まっておらずフリープレイでなければロールを非表示
                        if (!__instance.AmOwner) __instance.cosmetics.nameText.text = __instance?.Data?.PlayerName;
                    }

                    //変数定義
                    var seer = PlayerControl.LocalPlayer;
                    var seerRole = seer.GetRoleClass();
                    var target = __instance;

                    // EVILHACKER_SIMPLEADMIN_UI_AB_V2:
                    // 毎frameの通常name/role描画より先にAdmin表示を退避する。
                    var hasEvilHackerAdminDisplay = false;
                    var evilHackerAdminDisplay = string.Empty;

                    if (seer == target)
                    {
                        // Host-local:
                        // Host内部の同じAdmin snapshotを直接取得する。
                        if (
                            seerRole is EvilHacker localEvilHacker &&
                            localEvilHacker.TryGetRealtimeAdminSelfDisplay(
                                out var hostLocalAdminDisplay)
                        )
                        {
                            hasEvilHackerAdminDisplay = true;
                            // EVILHACKER_AIRSHIP_LAYOUT_LOCAL_DEMO_AB_V6:
                            // Airship host-local uses the all-room visual demo; other maps keep RealData.
                            // Visual A/B only; network sender paths are unchanged.
                            evilHackerAdminDisplay =
                                hostLocalAdminDisplay;
                        }
                        // Remote MOD:
                        // Hostからnative SetNameで届いた共通LiveTextを、
                        // native Phantom OPEN状態の間だけ保持する。
                        else if (
                            EvilHackerTextMinimap.IsRealtimeAdminLiveText(
                                target.cosmetics.nameText.text) &&
                            target.Data?.Role is { } nativeRole &&
                            nativeRole.Role == RoleTypes.Phantom &&
                            nativeRole.TryCast<PhantomRole>(
                                out var phantomRole) &&
                            phantomRole.IsInvisible
                        )
                        {
                            hasEvilHackerAdminDisplay = true;
                            evilHackerAdminDisplay =
                                target.cosmetics.nameText.text;
                        }
                    }

                    string RealName;
                    Mark.Clear();
                    Suffix.Clear();

                    //名前変更
                    RealName = target.GetRealName();

                    //名前色変更処理
                    //自分自身の名前の色を変更
                    if (target.AmOwner && AmongUsClient.Instance.IsGameStarted)
                    { //targetが自分自身
                        if (target.Is(CustomRoles.SeeingOff) || target.Is(CustomRoles.Sending))
                            RealName = Sending.RealNameChange(RealName,target);
                    }

                    //NameColorManager準拠の処理
                    RealName = RealName.ApplyNameColorData(seer, target, false);

                    //seer役職が対象のMark
                    Mark.Append(seerRole?.GetMark(seer, target, false));
                    //seerに関わらず発動するMark
                    Mark.Append(CustomRoleManager.GetMarkOthers(seer, target, false));

                    //ハートマークを付ける(会議中MOD視点)
                    //if (__instance.Is(CustomRoles.Lovers) && PlayerControl.LocalPlayer.Is(CustomRoles.Lovers))
                    if (LoversManager.CheckMyLovers(PlayerControl.LocalPlayer.PlayerId,__instance.PlayerId) || (PlayerControl.LocalPlayer.Is(CustomRoles.God) && __instance.Is(CustomRoles.Lovers)))
                    {
                        //Mark.Append($"<color={Utils.GetRoleColorCode(CustomRoles.Lovers)}>♥</color>");
                        Mark.Append($"<color=#{ColorUtility.ToHtmlStringRGB(LoversManager.GetLeaderColor(__instance.PlayerId))}>♥</color>");
                    }
                    else if (__instance.Is(CustomRoles.Lovers) && PlayerControl.LocalPlayer.Data.IsDead)
                    {
                        //Mark.Append($"<color={Utils.GetRoleColorCode(CustomRoles.Lovers)}>♥</color>");
                        Mark.Append($"<color=#{ColorUtility.ToHtmlStringRGB(LoversManager.GetLeaderColor(__instance.PlayerId))}>♥</color>");
                    }

                    //seerに関わらず発動するLowerText
                    Suffix.Append(CustomRoleManager.GetLowerTextOthers(seer, target));

                    //seer役職が対象のSuffix
                    Suffix.Append(seerRole?.GetSuffix(seer, target));

                    //seerに関わらず発動するSuffix
                    Suffix.Append(CustomRoleManager.GetSuffixOthers(seer, target));

                    /*if(main.AmDebugger.Value && main.BlockKilling.TryGetValue(target.PlayerId, out var isBlocked)) {
                        Mark = isBlocked ? "(true)" : "(false)";
                    }*/
                    if ((Utils.IsActive(SystemTypes.Comms) && Options.CommsCamouflage.GetBool()) ||
                        (Options.CurrentGameMode == CustomGameMode.SuperBombParty && SuperBakuretsuBros.SBPHideName.GetBool()))
                        RealName = $"<size=0>{RealName}</size> ";

                    string DeathReason = seer.Data.IsDead && seer.KnowDeathReason(target) ? $"({Utils.ColorString(Utils.GetRoleColor(CustomRoles.Doctor), Utils.GetVitalText(target.PlayerId))})" : "";
                    //Mark・Suffixの適用
                    string fixText = $"{RealName}{DeathReason}{Mark}";

                    if (BetWinTeams.BetWinTeamMode.GetBool() && !BetWinTeams.DisableShogo.GetBool())
                    {
                        bool shapeshifting = Main.CheckShapeshift.TryGetValue(target.PlayerId, out bool ss) && ss;
                        if (BetWinTeams.IsCamofuluge())
                        {
                            //カモフラージュのコミュサボ中は何もしない。
                        }
                        //変身中なら
                        else if (shapeshifting)
                        {
                            byte ShapeTargetId = Main.shapeSwitchPlayerIds.Contains(target.PlayerId) ? target.PlayerId : Main.ShapeshiftTarget[target.PlayerId];
                            PlayerControl shapeTargetPc = Utils.GetPlayerById(ShapeTargetId);

                            if (BetWinTeams.BetPoint.ContainsKey(shapeTargetPc.FriendCode))
                            {
                                if (BetWinTeams.BetPoint[shapeTargetPc.FriendCode].Syougo != null &&
                                    BetWinTeams.BetPoint[shapeTargetPc.FriendCode].Syougo != "")
                                {
                                    string tempClientName = target.GetRealName();
                                    fixText = fixText + "\r\n" + BetWinTeams.BetPoint[shapeTargetPc.FriendCode].Syougo;
                                }
                            }
                        }
                        //変身中じゃない、変身できない奴
                        else
                        {
                            if (BetWinTeams.BetPoint.ContainsKey(target.FriendCode))
                            {
                                if (BetWinTeams.BetPoint[target.FriendCode].Syougo != null)
                                {
                                    string tempClientName = target.GetRealName();
                                    fixText = fixText + "\r\n" + BetWinTeams.BetPoint[target.FriendCode].Syougo;
                                }
                            }
                        }
                    }

                    //全体書き換え
                    if (seer == target)
                    {
                        //seerに関わらず発動するMark
                        string tempSelfName = CustomRoleManager.GetOverrideOthers(seer, target, false);
                        if (tempSelfName != "")
                        {
                            fixText = tempSelfName;
                            RoleText.text = "";
                        }
                        (fixText,bool changed) = Utils.OverrideSpecialText(fixText, seer.PlayerId);
                        if (changed)
                        {
                            RoleText.text = "";
                        }
                    }

                    target.cosmetics.nameText.text = fixText;

                    if (Suffix.ToString() != "" || BetWinTeams.setSubNameFlag(target.FriendCode) || target.GetCustomRole() == CustomRoles.Rainbow)
                    {
                        //名前が2行になると役職テキストを上にずらす必要がある
                        RoleText.transform.SetLocalY(0.45f);
                        target.cosmetics.nameText.text += "\r\n" + Suffix.ToString();

                    }
                    else
                    {
                        //役職テキストの座標を初期値に戻す
                        RoleText.transform.SetLocalY(0.3f);
                    }

                    // EvilHacker SimpleAdmin OPEN中だけ、
                    // 通常name/role/suffixの最終結果を共通LiveTextへ置換する。
                    if (hasEvilHackerAdminDisplay)
                    {
                        target.cosmetics.nameText.text =
                            evilHackerAdminDisplay;

                        RoleText.text = string.Empty;
                        RoleText.enabled = false;
                        RoleText.transform.SetLocalY(0.3f);
                    }
                }
                else
                {
                    //役職テキストの座標を初期値に戻す
                    RoleText.transform.SetLocalY(0.3f);
                }
            }
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Die))]
        public static class PlayerControlDiePatch
        {
            public static void Postfix(PlayerControl __instance)
            {
                if (AmongUsClient.Instance.AmHost)
                {
                    // DEAD_PET_IMMEDIATE_CLEANUP_V1:
                    // Vanilla / MODを問わず死亡地点のPet表示を即時除去する。
                    TownOfHostForE.Patches.PetSettings.RemoveDeadPetVisualReliable(
                        __instance,
                        "PlayerControl.Die");
                }
            }
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Start))]
    class PlayerStartPatch
    {
        public static void Postfix(PlayerControl __instance)
        {
            var roleText = UnityEngine.Object.Instantiate(__instance.cosmetics.nameText);
            roleText.transform.SetParent(__instance.cosmetics.nameText.transform);
            roleText.transform.localPosition = new Vector3(0f, 0.2f, 0f);
            roleText.transform.localScale = new(1f, 1f, 1f);
            roleText.fontSize = Main.RoleTextSize;
            roleText.text = "RoleText";
            roleText.gameObject.name = "RoleText";
            roleText.enabled = false;
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.SetColor))]
    class SetColorPatch
    {
        public static bool IsAntiGlitchDisabled = false;
        public static bool Prefix(PlayerControl __instance, int bodyColor)
        {
            //色変更バグ対策
            if (!AmongUsClient.Instance.AmHost || __instance.CurrentOutfit.ColorId == bodyColor || IsAntiGlitchDisabled) return true;
            if (AmongUsClient.Instance.IsGameStarted && Options.CurrentGameMode == CustomGameMode.HideAndSeek)
            {
                //ゲーム中に色を変えた場合
                __instance.RpcMurderPlayer(__instance);
            }
            return true;
        }
    }
    [HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.CoEnterVent))]
    class CoEnterVentPatch
    {
        public static bool Prefix(PlayerPhysics __instance, [HarmonyArgument(0)] int id)
        {
            if (AmongUsClient.Instance.AmHost)
            {
                var user = __instance.myPlayer;
                var renegadeLockdown =
                    Renegade.IsAwakeningActive();

                if (Options.CurrentGameMode == CustomGameMode.HideAndSeek && Options.IgnoreVent.GetBool())
                    __instance.RpcBootFromVent(id);
                if (Options.CurrentGameMode == CustomGameMode.SuperBombParty)
                    __instance.RpcBootFromVent(id);

                bool onEnterVentResult =
                    renegadeLockdown
                        ? true
                        : user.GetCustomRole().IsNotAssignRoles()
                            ? false
                            : (!user.GetRoleClass()?.OnEnterVent(__instance, id) ?? false);

                var forceBootByMode = Options.CurrentGameMode == CustomGameMode.HideAndSeek && Options.IgnoreVent.GetBool()
                    || Options.CurrentGameMode == CustomGameMode.SuperBombParty;
                var blockedByRoleOrBase =
                    renegadeLockdown ||
                    onEnterVentResult ||
                    (user.Data.Role.Role != RoleTypes.Engineer && !user.CanUseImpostorVentButton());
                // ROLEBUGTRACE_STAGE1B_PERMANENT: current source has no central ExitVent hook
                RoleBugTrace.VentEnter(
                    user,
                    id,
                    !(forceBootByMode || blockedByRoleOrBase),
                    renegadeLockdown
                        ? "RenegadeAwakening"
                        : forceBootByMode
                            ? "GameMode"
                            : (blockedByRoleOrBase ? "RoleOrBasePermission" : "Allowed"));

                if (blockedByRoleOrBase)
                {
                    _ = new LateTask(() =>
                    {
                        __instance.RpcBootFromVent(id);
                    }, 0.5f, "Cancel Vent");
                }
            }
            else
            {
                var user = __instance.myPlayer;
                user.GetRoleClass()?.OnEnterVent(__instance, id);
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.SetName))]
    class SetNamePatch
    {
        public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] string name)
        {
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CompleteTask))]
    class PlayerControlCompleteTaskPatch
    {
        public static bool Prefix(PlayerControl __instance)
        {
            var pc = __instance;

            Logger.Info($"TaskComplete:{pc.GetNameWithRole()}", "CompleteTask");
            var taskState = pc.GetPlayerTaskState();

            taskState.Update(pc);

            var roleClass = pc.GetRoleClass();
            var ret = true;
            var renegadeLockdown =
                Renegade.IsAwakeningActive();

            // RENEGADE_R2G_TASK_ONLY_LOCKDOWN
            // タスクそのものは進行させるが、役職固有のタスク完了能力は追跡中だけ発火させない。
            // Workhorseだけは追加タスク数の既存ルールを壊さないため維持する。
            if (!renegadeLockdown)
            {
                if (roleClass != null)
                {
                    ret = roleClass.OnCompleteTask();
                }

                CompreteCrew.OnCompleteTask(pc);
            }
            else
            {
                Logger.Info(
                    $"R2G awakened chase: role task-complete hooks suppressed player={pc.PlayerId}",
                    "Renegade");
            }

            ret &= Workhorse.OnCompleteTask(pc);

            Utils.NotifyRoles();
            // ROLEBUGTRACE_STAGE1A_PERMANENT
            RoleBugTrace.Task(pc, ret, taskState.CompletedTasksCount, taskState.AllTasksCount);
            return ret;
        }
        public static void Postfix()
        {
            //人外のタスクを排除して再計算
            GameData.Instance.RecomputeTaskCounts();
            Logger.Info($"TotalTaskCounts = {GameData.Instance.CompletedTasks}/{GameData.Instance.TotalTasks}", "TaskState.Update");
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.ProtectPlayer))]
    class PlayerControlProtectPlayerPatch
    {
        public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
        {
            Logger.Info($"{__instance.GetNameWithRole()} => {target.GetNameWithRole()}", "ProtectPlayer");
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RemoveProtection))]
    class PlayerControlRemoveProtectionPatch
    {
        public static void Postfix(PlayerControl __instance)
        {
            Logger.Info($"{__instance.GetNameWithRole()}", "RemoveProtection");
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
    class PlayerControlSetRolePatch
    {
        public static bool Prefix(PlayerControl __instance, ref RoleTypes roleType)
        {
            var target = __instance;
            var targetName = __instance.GetNameWithRole();
            Logger.Info($"{targetName} =>{roleType}", "PlayerControl.RpcSetRole");
            if (!ShipStatus.Instance.enabled) return true;
            if (roleType is RoleTypes.CrewmateGhost or RoleTypes.ImpostorGhost)
            {
                // HOST_EXILE_REMOTE_VANILLA_CRITICAL_LEASE_AB_V3:
                // Safety role + SetIsDead送信後、host Ghost Roleを配る直前に
                // 必要viewerの既存safety dummyだけを+2s finalizerまでleaseする。
                // PERSISTENT_LEASE_REMOTE_MOD_CALL_AB_V8L: Hostまたは認識済みremote MOD追放でlease準備を呼ぶ。
                if (target.AmOwner || target.IsModClient())
                {
                    ExileControllerWrapUpPatch.VanillaActualExileCriticalLeaseAbV14A
                        .TryPrepareHostExileSafetyDummyLeaseAbV3(target);
                }

                // INFLUENCER_V19_NATIVE_GHOST_BRIDGE:
                // Keep official native SpiritGuide instead of collapsing it into Crew/ImpostorGhost.
                // The outer gate intentionally remains Crew/ImpostorGhost only.
                var requestedSpiritGuide =
                    target.GetCustomRole() == CustomRoles.Influencer ||
                    target.Data?.Role?.Role == RoleTypes.SpiritGuide ||
                    AntiBlackout.WasSpiritGuide(target.PlayerId);

                if (requestedSpiritGuide)
                {
                    roleType = RoleTypes.SpiritGuide;

                    foreach (var seer in Main.AllPlayerControls)
                    {
                        SelectRolesPatch.UpdateExileDisplayRoleViewPermanent(
                            seer.PlayerId,
                            target.PlayerId,
                            RoleTypes.SpiritGuide);
                    }

                    return true;
                }

                // RENEGADE_R2C_CREWMATE_GHOST
                // Renegade is an IKiller only while awakened, but must not inherit
                // ImpostorGhost UI (especially Sabotage) after timeout death.
                var targetIsKiller =
                    target.GetRoleClass() is IKiller &&
                    !target.Is(CustomRoles.Renegade);
                var ghostRoles = new Dictionary<PlayerControl, RoleTypes>();
                foreach (var seer in Main.AllPlayerControls)
                {
                    var self = seer.PlayerId == target.PlayerId;
                    var seerIsKiller = seer.GetRoleClass() is IKiller;

                    var viewerGhostRole =
                        (self && targetIsKiller) || (!seerIsKiller && target.Is(CustomRoleTypes.Impostor))
                            ? RoleTypes.ImpostorGhost
                            : RoleTypes.CrewmateGhost;

                    // MOD_EXILE_REMOTE_VANILLA_GHOST_GUARD_AB_V7:
            // v2のHost実追放ghost guardを、remote認識済みMOD導入者の実追放にも拡張する。
            // remote Vanilla viewerだけ、初回Ghost配布時点でImpostorGhostをCrewmateGhostへ安全化する。
            // HOST_EXILE_REMOTE_VANILLA_GHOST_GUARD_AB_V2:
                    // 4-15人real-targeted実追放中のMODホスト追放だけを対象にする。
                    // remote VanillaだけImpostorGhostをCrewmateGhostへ安全化する。
                    // 通常死亡・MOD viewer・Vanilla本人追放(V14A)には影響させない。
                    if (viewerGhostRole == RoleTypes.ImpostorGhost &&
                        AmongUsClient.Instance.AmHost &&
                        AntiBlackout.IsRealExileDisplaySafetyAppliedPermanent &&
                        (target.AmOwner || target.IsModClient()) &&
                        seer.PlayerId != target.PlayerId &&
                        !seer.AmOwner &&
                        !seer.IsModClient() &&
                        MeetingVoteManager.H4TryGetActualExileLedger(out var actualExileLedger) &&
                        // A/B v2: ホスト追放ではtargeted VotingComplete送信ループからownerが除外されるため
                        // RealVotingCompleteDeliveredは構造上falseのまま。SafetyApplied + PREPARED_REAL_TARGETEDで限定する。
                        actualExileLedger.Path == "PREPARED_REAL_TARGETED" &&
                        !actualExileLedger.IsTie &&
                        actualExileLedger.ActualExiledPlayerId == target.PlayerId)
                    {
                        Logger.Info(
                            $"Stage=REMOTE_VANILLA_MOD_EXILE_GHOST_GUARD | TargetId={target.PlayerId} | ViewerId={seer.PlayerId} | ViewerClientId={seer.GetClientId()} | Intended=ImpostorGhost | Applied=CrewmateGhost",
                            "MOD_EXILE_REMOTE_VANILLA_GHOST_GUARD_AB_V7");

                        viewerGhostRole = RoleTypes.CrewmateGhost;
                    }

                    ghostRoles[seer] = viewerGhostRole;
                }
                if (ghostRoles.All(kvp => kvp.Value == RoleTypes.CrewmateGhost))
                {
                    roleType = RoleTypes.CrewmateGhost;

                    // ExileDisplayRealPermanent:
                    // broadcastされる同一Ghost Roleを全viewerの保持RoleViewへ反映する。
                    foreach (var seer in Main.AllPlayerControls)
                    {
                        SelectRolesPatch.UpdateExileDisplayRoleViewPermanent(
                            seer.PlayerId,
                            target.PlayerId,
                            roleType);
                    }
                }
                else if (ghostRoles.All(kvp => kvp.Value == RoleTypes.ImpostorGhost))
                {
                    roleType = RoleTypes.ImpostorGhost;

                    // ExileDisplayRealPermanent:
                    foreach (var seer in Main.AllPlayerControls)
                    {
                        SelectRolesPatch.UpdateExileDisplayRoleViewPermanent(
                            seer.PlayerId,
                            target.PlayerId,
                            roleType);
                    }
                }
                else
                {
                    foreach ((var seer, var role) in ghostRoles)
                    {
                        Logger.Info($"Desync {targetName} =>{role} for{seer.GetNameWithRole()}", "PlayerControl.RpcSetRole");
                        var h4PreparedFirstSelfHold =
                            ExileControllerWrapUpPatch.VanillaActualExileAtomicDeadGhostAbV34
                                .TryPrepareFirstSelfGhostHold(target, seer, role);
                        // ExileDisplayRealPermanent:
                        // viewerごとに異なるGhost Roleを正確に保持する。
                        SelectRolesPatch.UpdateExileDisplayRoleViewPermanent(
                            seer.PlayerId,
                            target.PlayerId,
                            role);
                        if (h4PreparedFirstSelfHold &&
                            ExileControllerWrapUpPatch.VanillaActualExileAtomicDeadGhostAbV34
                                .TryCommitFirstSelfGhostHold())
                        {
                            continue;
                        }
                        target.RpcSetRoleDesync(role, seer.GetClientId());
                    }
                    return false;
                }
            }
            return true;
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MixUpOutfit))]
    public static class PlayerControlMixupOutfitPatch
    {
        public static void Postfix(PlayerControl __instance)
        {
            if (!__instance.IsAlive())
            {
                return;
            }
            // 自分がDesyncインポスターで，バニラ判定ではインポスターの場合，バニラ処理で名前が非表示にならないため，相手の名前を非表示にする
            if (
                PlayerControl.LocalPlayer.Data.Role.IsImpostor &&  // バニラ判定でインポスター
                !PlayerControl.LocalPlayer.Is(CustomRoleTypes.Impostor) &&  // Mod判定でインポスターではない
                PlayerControl.LocalPlayer.GetCustomRole().GetRoleInfo()?.IsDesyncImpostor == true)  // Desyncインポスター
            {
                // 名前を隠す
                __instance.cosmetics.ToggleNameVisible(false);
            }
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckSporeTrigger))]
    public static class PlayerControlCheckSporeTriggerPatch
    {
        public static bool Prefix()
        {
            if (Options.DisableFungleSporeTrigger.GetBool())
            {
                return false;
            }
            return true;
        }
    }
}
