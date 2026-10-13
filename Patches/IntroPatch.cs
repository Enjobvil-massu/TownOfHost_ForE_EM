using System;
using System.Linq;
using System.Threading.Tasks;
using AmongUs.GameOptions;
using HarmonyLib;
using UnityEngine;

using TownOfHostForE.Roles.Core;
using static TownOfHostForE.Translator;
using TownOfHostForE.Patches;
using TownOfHostForE.Modules;

namespace TownOfHostForE
{
    [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.CoBegin))]
    class SetUpRoleTextCoBeginPatch
    {
        public static void Postfix(IntroCutscene __instance, ref Il2CppSystem.Collections.IEnumerator __result)
        {
            //ShowRoleに直接パッチあて出来ないためCoBegin中にパッチを当てる
            var patcher = new CoroutinPatcher(__result);
            //ShowRoleはステートマシンクラスになっているためその実行前にパッチを当てる
            //元々Postfixだが、タイミング的にはPrefixの方が適切なのでPrefixに当てる
            patcher.AddPrefix(typeof(IntroCutscene._ShowRole_d__41), () => SetUpRoleTextPatch.Postfix(__instance));
            __result = patcher.EnumerateWithPatch();
        }
    }
    // Patchが当たらないが念のためコメントアウト
    //[HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.ShowRole))]
    class SetUpRoleTextPatch
    {
        // ALL_ROLE_INTRO_DISPLAY_AB_V1
        // Vanilla: targeted self-name overlay only. Mod clients: local RoleText/RoleBlurbText only.
        private static readonly System.Collections.Generic.Dictionary<byte, string>
            VanillaIntroRestoreSelfNamesABV1 = new();
        private static bool VanillaIntroOverlaySentABV1;
        private static bool ShowRoleEnteredABV1;

        public static void ResetAllRoleIntroDisplayABV1()
        {
            VanillaIntroRestoreSelfNamesABV1.Clear();
            VanillaIntroOverlaySentABV1 = false;
            ShowRoleEnteredABV1 = false;
        }

        private static void ScheduleLocalAllRoleIntroTextABV1(IntroCutscene __instance)
        {
            if (__instance == null || PlayerControl.LocalPlayer == null) return;
            if (!StartFlowReadiness.CanUseIntroRoleState()) return;

            _ = new LateTask(() =>
            {
                if (__instance == null || PlayerControl.LocalPlayer == null) return;
                if (!StartFlowReadiness.CanUseIntroRoleState()) return;

                CustomRoles role = PlayerControl.LocalPlayer.GetCustomRole();

                __instance.YouAreText.color = Utils.GetRoleColor(role);
                __instance.RoleText.text = Utils.GetRoleName(role);
                __instance.RoleText.color = Utils.GetRoleColor(role);
                __instance.RoleBlurbText.color = Utils.GetRoleColor(role);
                __instance.RoleBlurbText.text = PlayerControl.LocalPlayer.GetRoleInfo() ?? "";

                foreach (var subRole in PlayerState.GetByPlayerId(PlayerControl.LocalPlayer.PlayerId).SubRoles)
                    __instance.RoleBlurbText.text += "\n" + Utils.ColorString(Utils.GetRoleColor(subRole), GetString($"{subRole}Info"));
                __instance.RoleText.text += Utils.GetSubRolesText(PlayerControl.LocalPlayer.PlayerId);

                Logger.Info(
                    $"ALL_ROLE_INTRO_DISPLAY_AB_V1 LOCAL_APPLY PlayerId={PlayerControl.LocalPlayer.PlayerId} Role={role}",
                    "AllRoleIntroAB");
            }, 0.01f, "AllRoleIntroLocalTextABV1");
        }

        // VANILLA_INTRO_TOHK_SETNAME_PAYLOAD_AB_V5:
        // Intro display transport only.
        // Keep V4 targeted reliable GameDataTo transport, but construct the SetName RPC
        // with the current TOH-K payload shape: player.NetId / displayName / true.
        private static bool SendVanillaIntroSelfNameTargetedABV5(
            PlayerControl player,
            string displayName,
            string phase)
        {
            if (
                player == null ||
                player.Data == null ||
                player.Data.Disconnected ||
                player.AmOwner ||
                player.IsModClient() ||
                string.IsNullOrEmpty(displayName)
            )
            {
                return false;
            }

            if (!player.TryGetClientId(out var targetClientId) || targetClientId < 0)
            {
                Logger.Warn(
                    $"VANILLA_INTRO_TOHK_SETNAME_PAYLOAD_AB_V5 SKIP_NO_CLIENT Phase={phase} PlayerId={player.PlayerId}",
                    "AllRoleIntroAB");
                return false;
            }

            try
            {
                // VANILLA_INTRO_NATURAL_WRAP_SPACES_AB_V11B
                // Display-only A/B: plain spaces only; no new TMP tag and no CR/LF.
                var wireDisplayName = displayName.Replace(
                    "</size>  ",
                    "</size>              ");

                var sender = CustomRpcSender.Create(
                    $"VanillaIntroTohkPayloadABV5-{phase}-P{player.PlayerId}",
                    Hazel.SendOption.Reliable);

                sender.StartMessage(targetClientId);
                // VANILLA_INTRO_SETNAME_DATA_NETID_AB_V8A: match RpcSetNamePrivate first payload field.
                sender.StartRpc(player.NetId, (byte)RpcCalls.SetName)
                    .Write(player.Data.NetId)
                    .Write(wireDisplayName)
                    .Write(true)
                    .EndRpc();
                sender.SendMessage();

                Logger.Info(
                    $"VANILLA_INTRO_SETNAME_DATA_NETID_AB_V8A SEND Phase={phase} PlayerId={player.PlayerId} ClientId={targetClientId} RpcTargetNetId={player.NetId} BodyDataNetId={player.Data.NetId} FinalBool=True Length={wireDisplayName.Length}",
                    "AllRoleIntroAB");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, "VanillaIntroTohkPayloadABV5");
                return false;
            }
        }

        // VANILLA_INTRO_PRECAPTURE_TIMING_AB_V6A:
        // Timing A/B only. Do not alter role assignment, native role, task state,
        // NotifyRoles, StartFlowReadiness, or the existing V5 transport.
        // Send the already-committed custom role name + short role info to each
        // remote Vanilla self immediately when RunCustomIntroSync becomes ready,
        // before diagnostics / task-state work / pets / Utils.NotifyRoles.
        public static void SendVanillaIntroPreCaptureABV6A()
        {
            if (!AmongUsClient.Instance.AmHost) return;
            if (!StartFlowReadiness.CanUseIntroRoleState()) return;

            foreach (var player in Main.AllPlayerControls)
            {
                if (
                    player == null ||
                    player.Data == null ||
                    player.Data.Disconnected ||
                    player.AmOwner ||
                    player.IsModClient()
                )
                {
                    continue;
                }

                var role = player.GetCustomRole();
                if (role == CustomRoles.NotAssigned)
                {
                    Logger.Warn(
                        $"VANILLA_INTRO_PRECAPTURE_TIMING_AB_V6A SKIP_NOT_ASSIGNED PlayerId={player.PlayerId}",
                        "AllRoleIntroAB");
                    continue;
                }

                var roleName = Utils.GetRoleName(role);
                var roleInfo = player.GetRoleInfo() ?? "";
                var displayName = string.IsNullOrEmpty(roleInfo)
                    ? Utils.ColorString(Utils.GetRoleColor(role), roleName)
                    : $"{Utils.ColorString(Utils.GetRoleColor(role), roleName)}\r\n{roleInfo}";

                if (string.IsNullOrEmpty(displayName))
                {
                    Logger.Warn(
                        $"VANILLA_INTRO_PRECAPTURE_TIMING_AB_V6A SKIP_EMPTY PlayerId={player.PlayerId} Role={role}",
                        "AllRoleIntroAB");
                    continue;
                }

                var sent = SendVanillaIntroSelfNameTargetedABV5(
                    player,
                    displayName,
                    "PRECAPTURE_V6A");

                Logger.Info(
                    $"VANILLA_INTRO_PRECAPTURE_TIMING_AB_V6A {(sent ? "SEND" : "SEND_FAILED")} PlayerId={player.PlayerId} Role={role} Length={displayName.Length}",
                    "AllRoleIntroAB");
            }
        }

        private static void SendVanillaAllRoleIntroOverlayABV1()
        {
            if (VanillaIntroOverlaySentABV1) return;
            if (!AmongUsClient.Instance.AmHost) return;
            if (!StartFlowReadiness.CanUseIntroRoleState()) return;

            VanillaIntroOverlaySentABV1 = true;

            foreach (var player in Main.AllPlayerControls)
            {
                if (
                    player == null ||
                    player.Data == null ||
                    player.Data.Disconnected ||
                    player.AmOwner ||
                    player.IsModClient()
                )
                {
                    continue;
                }

                string restoreName;
                if (
                    Main.LastNotifyNames != null &&
                    Main.LastNotifyNames.TryGetValue(
                        (player.PlayerId, player.PlayerId),
                        out var cachedName
                    ) &&
                    !string.IsNullOrEmpty(cachedName) &&
                    StartFlowReadiness.IsSafeCachedSelfName(player, cachedName)
                )
                {
                    restoreName = cachedName;
                }
                else
                {
                    restoreName = string.IsNullOrEmpty(player.Data.PlayerName)
                        ? player.name
                        : player.Data.PlayerName;
                }

                VanillaIntroRestoreSelfNamesABV1[player.PlayerId] = restoreName;

                var role = player.GetCustomRole();

                // v245 success authority:
                // reuse the self-name produced by Utils.NotifyRoles() instead of
                // constructing a second independent Intro payload.
                var introOverlay = restoreName;

                SendVanillaIntroSelfNameTargetedABV5(
                    player,
                    introOverlay,
                    "COBEGIN");

                CriticalStartFlowPacketDiagnostic.Mark(
                    "ALL_ROLE_INTRO_VANILLA_SELF_SEND",
                    CriticalStartFlowPacketDiagnostic.PlayerSnapshot(player));

                Logger.Info(
                    $"ALL_ROLE_INTRO_DISPLAY_AB_V1 VANILLA_SEND PlayerId={player.PlayerId} Role={role} Length={introOverlay.Length}",
                    "AllRoleIntroAB");
            }
        }

        private static void ReassertVanillaIntroSelfNameAtShowRoleABV5()
        {
            if (!AmongUsClient.Instance.AmHost) return;
            if (!StartFlowReadiness.CanUseIntroRoleState()) return;

            foreach (var player in Main.AllPlayerControls)
            {
                if (
                    player == null ||
                    player.Data == null ||
                    player.Data.Disconnected ||
                    player.AmOwner ||
                    player.IsModClient()
                )
                {
                    continue;
                }

                if (
                    !VanillaIntroRestoreSelfNamesABV1.TryGetValue(
                        player.PlayerId,
                        out var introOverlay
                    ) ||
                    string.IsNullOrEmpty(introOverlay)
                )
                {
                    Logger.Warn(
                        $"VANILLA_INTRO_TOHK_SETNAME_PAYLOAD_AB_V5 SKIP_NO_CACHED_SELF Phase=SHOWROLE PlayerId={player.PlayerId}",
                        "AllRoleIntroAB");
                    continue;
                }

                SendVanillaIntroSelfNameTargetedABV5(
                    player,
                    introOverlay,
                    "SHOWROLE");
            }
        }

        // VANILLA_INTRO_SHOWROLE_CANONICAL_RESEND_AB_V12B
        // V12A runtime proved that CoBegin NotifyRoles alone does not reach
        // the Vanilla Intro name capture. Re-send the exact canonical
        // NotifyRoles self-name synchronously at the ShowRole boundary.
        private static void ReassertCanonicalNotifyRolesSelfNameAtShowRoleABV12B()
        {
            if (!AmongUsClient.Instance.AmHost) return;
            if (!StartFlowReadiness.CanUseIntroRoleState()) return;

            foreach (var player in Main.AllPlayerControls)
            {
                if (
                    player == null ||
                    player.Data == null ||
                    player.Data.Disconnected ||
                    player.AmOwner ||
                    player.IsModClient()
                )
                {
                    continue;
                }

                if (
                    Main.LastNotifyNames == null ||
                    !Main.LastNotifyNames.TryGetValue(
                        (player.PlayerId, player.PlayerId),
                        out var cachedSelf
                    ) ||
                    string.IsNullOrEmpty(cachedSelf)
                )
                {
                    Logger.Warn(
                        $"VANILLA_INTRO_SHOWROLE_CANONICAL_RESEND_AB_V12B SKIP_NO_CACHED_SELF PlayerId={player.PlayerId}",
                        "AllRoleIntroAB");
                    continue;
                }

                if (
                    !player.TryGetClientId(out var targetClientId) ||
                    targetClientId < 0
                )
                {
                    Logger.Warn(
                        $"VANILLA_INTRO_SHOWROLE_CANONICAL_RESEND_AB_V12B SKIP_NO_CLIENT PlayerId={player.PlayerId}",
                        "AllRoleIntroAB");
                    continue;
                }

                player.RpcSetNamePrivate(
                    cachedSelf,
                    true,
                    player,
                    force: true
                );

                Logger.Info(
                    $"VANILLA_INTRO_SHOWROLE_CANONICAL_RESEND_AB_V12B SEND PlayerId={player.PlayerId} ClientId={targetClientId} Length={cachedSelf.Length}",
                    "AllRoleIntroAB");
            }
        }
        public static void ApplyAllRoleIntroDisplayWhenReadyABV1(IntroCutscene __instance)
        {
            if (!GameStates.IsModHost) return;
            if (!StartFlowReadiness.CanUseIntroRoleState()) return;

            // VANILLA_INTRO_NOTIFYROLES_SOLE_PRECAPTURE_V12A: cached-self overlay SetName disabled before Intro capture.

            if (ShowRoleEnteredABV1)
                ScheduleLocalAllRoleIntroTextABV1(__instance);
        }

        public static void RestoreVanillaAllRoleIntroOverlayABV1()
        {
            if (!AmongUsClient.Instance.AmHost)
            {
                VanillaIntroRestoreSelfNamesABV1.Clear();
                VanillaIntroOverlaySentABV1 = false;
                ShowRoleEnteredABV1 = false;
                return;
            }

            foreach (var entry in VanillaIntroRestoreSelfNamesABV1)
            {
                var player = Utils.GetPlayerById(entry.Key);
                if (player == null || player.Data == null || player.Data.Disconnected)
                    continue;

                player.RpcSetNamePrivate(
                    entry.Value,
                    true,
                    player,
                    force: true
                );

                Logger.Info(
                    $"ALL_ROLE_INTRO_DISPLAY_AB_V1 RESTORE PlayerId={player.PlayerId} Length={entry.Value.Length}",
                    "AllRoleIntroAB");
            }

            VanillaIntroRestoreSelfNamesABV1.Clear();
            VanillaIntroOverlaySentABV1 = false;
            ShowRoleEnteredABV1 = false;
        }

        public static void Postfix(IntroCutscene __instance)
        {
            if (!GameStates.IsModHost) return;

            ShowRoleEnteredABV1 = true;

            CriticalStartFlowPacketDiagnostic.Mark("SHOW_ROLE_PATCH_ENTER");
            CriticalStartFlowPacketDiagnostic.MarkAllPlayers("SHOW_ROLE_PLAYER_STATE");

            if (!StartFlowReadiness.CanUseIntroRoleState())
            {
                CriticalStartFlowPacketDiagnostic.Mark(
                    "SHOW_ROLE_CUSTOM_SYNC_WAITING_FOR_READY");
                return;
            }

            // VANILLA_SELF_INTRO_GATE_AB_V12C:
            // V12B ShowRole SetName resend is intentionally disabled for this A/B.
            // Keep the V12B implementation dormant so this test has one causal variable:
            // whether gating the Vanilla viewer's own GameData until the role bundle
            // reaches the client changes the native Intro capture result.
            ApplyAllRoleIntroDisplayWhenReadyABV1(__instance);
        }
    }
    [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.CoBegin))]
    class CoBeginPatch
    {
        public static void Prefix(IntroCutscene __instance)
        {
            SetUpRoleTextPatch.ResetAllRoleIntroDisplayABV1();
            CriticalStartFlowPacketDiagnostic.Mark("INTRO_COBEGIN_PREFIX_ENTER");
            CriticalStartFlowPacketDiagnostic.MarkAllPlayers("INTRO_COBEGIN_ENTRY_PLAYER_STATE");
            CriticalStartFlowPacketDiagnostic.Mark(
                "INTRO_COBEGIN_ENTRY_TASK_GLOBAL",
                CriticalStartFlowPacketDiagnostic.GlobalTaskSnapshot());

            StartFlowReadiness.RequestIntroSync(() => RunCustomIntroSync(__instance));
        }

        private static void RunCustomIntroSync(IntroCutscene __instance)
        {
            // VANILLA_INTRO_PRECAPTURE_TIMING_AB_V6A:
            // This must stay before diagnostics, TaskState.Init, pets and NotifyRoles.
            // VANILLA_INTRO_NOTIFYROLES_SOLE_PRECAPTURE_V12A: V6A direct SetName disabled; canonical NotifyRoles owns the pre-capture self-name.

            CriticalStartFlowPacketDiagnostic.Mark("INTRO_CUSTOM_SYNC_ENTER_READY");
            var logger = Logger.Handler("Info");
            logger.Info("------------名前表示------------");
            foreach (var pc in Main.AllPlayerControls)
            {
                logger.Info($"{(pc.AmOwner ? "[*]" : ""),-3}{pc.PlayerId,-2}:{pc.name.PadRightV2(20)}:{pc.cosmetics.nameText.text}({Palette.ColorNames[pc.Data.DefaultOutfit.ColorId].ToString().Replace("Color", "")})");
                pc.cosmetics.nameText.text = pc.name;
            }
            logger.Info("----------役職割り当て----------");
            foreach (var pc in Main.AllPlayerControls)
            {
                logger.Info($"{(pc.AmOwner ? "[*]" : ""),-3}{pc.PlayerId,-2}:{pc?.Data?.PlayerName?.PadRightV2(20)}:{pc.GetAllRoleName().RemoveHtmlTags()}");
            }
            logger.Info("--------------環境--------------");
            foreach (var pc in Main.AllPlayerControls)
            {
                try
                {
                    var text = pc.AmOwner ? "[*]" : "   ";
                    text += $"{pc.PlayerId,-2}:{pc.Data?.PlayerName?.PadRightV2(20)}:{pc.GetClient()?.PlatformData?.Platform.ToString()?.Replace("Standalone", ""),-11}";
                    if (Main.playerVersion.TryGetValue(pc.PlayerId, out PlayerVersion pv))
                        text += $":Mod({pv.forkId}/{pv.version}:{pv.tag})";
                    else text += ":Vanilla";
                    logger.Info(text);
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex, "Platform");
                }
            }
            logger.Info("------------基本設定------------");
            var tmp = GameOptionsManager.Instance.CurrentGameOptions.ToHudString(GameData.Instance ? GameData.Instance.PlayerCount : 10).Split("\r\n").Skip(1);
            foreach (var t in tmp) logger.Info(t);
            logger.Info("------------詳細設定------------");
            foreach (var o in OptionItem.AllOptions)
                if (!o.IsHiddenOn(Options.CurrentGameMode) && (o.Parent == null ? !o.GetString().Equals("0%") : o.Parent.GetBool()))
                    logger.Info($"{(o.Parent == null ? o.Name.PadRightV2(40) : $"┗ {o.Name}".PadRightV2(41))}:{o.GetString().RemoveHtmlTags()}");
            logger.Info("-------------その他-------------");
            logger.Info($"プレイヤー数: {Main.AllPlayerControls.Count()}人");
            CriticalStartFlowPacketDiagnostic.Mark("TASKSTATE_INIT_ALL_CALL");
            Main.AllPlayerControls.Do(x => PlayerState.GetByPlayerId(x.PlayerId).InitTask(x));
            CriticalStartFlowPacketDiagnostic.MarkAllPlayers("TASKSTATE_INIT_ALL_RETURN_PLAYER_STATE");
            CriticalStartFlowPacketDiagnostic.Mark(
                "RECOMPUTE_TASK_COUNTS_CALL",
                CriticalStartFlowPacketDiagnostic.GlobalTaskSnapshot());
            GameData.Instance.RecomputeTaskCounts();
            TaskState.InitialTotalTasks = GameData.Instance.TotalTasks;
            CriticalStartFlowPacketDiagnostic.Mark(
                "RECOMPUTE_TASK_COUNTS_RETURN",
                CriticalStartFlowPacketDiagnostic.GlobalTaskSnapshot());

            //ペット付与
            PetSettings.SetPetRoleInPet();

            // ペット付与後の通常衣装を迷彩復元用に再保存
            foreach (var pc in Main.AllPlayerControls)
            {
                var outfit = pc.Data.DefaultOutfit;
                TownOfHostForE.Camouflage.PlayerSkins[pc.PlayerId] =
                    new NetworkedPlayerInfo.PlayerOutfit
                    {
                        PlayerName = outfit.PlayerName,
                        ColorId = outfit.ColorId,
                        HatId = outfit.HatId,
                        SkinId = outfit.SkinId,
                        VisorId = outfit.VisorId,
                        PetId = outfit.PetId
                    };
            }

            TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.Pets("GAME_START_RENTAL_AND_OUTFIT_SAVED"));
            CriticalStartFlowPacketDiagnostic.MarkAllPlayers("NOTIFY_ROLES_BEFORE_PLAYER_STATE");
            CriticalStartFlowPacketDiagnostic.Mark("NOTIFY_ROLES_CALL");
            // VANILLA_INTRO_NOTIFYROLES_SOLE_PRECAPTURE_V12A:
            // Force TOH4E canonical RpcSetNamePrivate onto the wire before ShowTeam/ShowRole.
            // Historical v6180.245 success and TOH-K both rely on the normal NotifyRoles self-name path.
            // VANILLA_INTRO_RESEARCH_PARKED_20260924:
            // V12A NoCache experiment was runtime FAIL.
            // Restore normal NotifyRoles behavior.
            Utils.NotifyRoles();
            CriticalStartFlowPacketDiagnostic.Mark("NOTIFY_ROLES_RETURNED");
            CriticalStartFlowPacketDiagnostic.MarkAllPlayers("NOTIFY_ROLES_AFTER_PLAYER_STATE");

            GameStates.InGame = true;
            SetUpRoleTextPatch.ApplyAllRoleIntroDisplayWhenReadyABV1(__instance);
            CriticalStartFlowPacketDiagnostic.Mark("INTRO_COBEGIN_PREFIX_RETURN");
        }
    }
    [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.BeginCrewmate))]
    class BeginCrewmatePatch
    {
        public static void Prefix(IntroCutscene __instance, ref Il2CppSystem.Collections.Generic.List<PlayerControl> teamToDisplay)
        {
            CriticalStartFlowPacketDiagnostic.Mark(
                "SHOW_TEAM_BEGINCREWMATE_PREFIX",
                CriticalStartFlowPacketDiagnostic.PlayerSnapshot(PlayerControl.LocalPlayer));
            if (PlayerControl.LocalPlayer.Is(CustomRoleTypes.Neutral) || PlayerControl.LocalPlayer.Is(CustomRoles.StrayWolf) || PlayerControl.LocalPlayer.Is(CustomRoleTypes.Animals))
            {
                //ぼっち役職
                var soloTeam = new Il2CppSystem.Collections.Generic.List<PlayerControl>();
                soloTeam.Add(PlayerControl.LocalPlayer);
                teamToDisplay = soloTeam;
            }
        }
        public static void Postfix(IntroCutscene __instance, ref Il2CppSystem.Collections.Generic.List<PlayerControl> teamToDisplay)
        {
            //チーム表示変更
            CustomRoles role = PlayerControl.LocalPlayer.GetCustomRole();

            if (role.GetRoleInfo()?.IntroSound is AudioClip introSound)
            {
                PlayerControl.LocalPlayer.Data.Role.IntroSound = introSound;
            }
            if (!Options.ChangeIntro.GetBool())
            {
                switch (role.GetCustomRoleTypes())
                {
                    case CustomRoleTypes.Neutral:
                        __instance.TeamTitle.text = GetString("Neutral");
                        __instance.TeamTitle.color = Color.gray;
                        //__instance.TeamTitle.text = Utils.GetRoleName(role);
                        //__instance.TeamTitle.color = Utils.GetRoleColor(role);
                        __instance.ImpostorText.gameObject.SetActive(true);
                        __instance.ImpostorText.text = GetString("NeutralInfo");
                        __instance.BackgroundBar.material.color = Color.gray;
                        //if (!Options.CurrentGameMode.IsOneNightMode())
                            StartFadeIntro(__instance, Color.gray, Utils.GetRoleColor(role));
                        break;
                    case CustomRoleTypes.Animals:
                        __instance.TeamTitle.text = Utils.GetRoleName(role);
                        __instance.TeamTitle.color = Utils.GetRoleColor(role);
                        __instance.ImpostorText.gameObject.SetActive(true);
                        __instance.ImpostorText.text = GetString("TeamAnimals");
                        __instance.BackgroundBar.material.color = Utils.GetRoleColor(role);
                        break;
                    case CustomRoleTypes.Madmate:
                        //if (!Options.CurrentGameMode.IsOneNightMode())
                            StartFadeIntro(__instance, Palette.CrewmateBlue, Palette.ImpostorRed);
                        var impostorBehaviour = default(RoleBehaviour);
                        foreach (var vanillaRole in RoleManager.Instance.AllRoles)
                        {
                            if (vanillaRole.Role != RoleTypes.Impostor) continue;
                            impostorBehaviour = vanillaRole;
                            break;
                        }
                        if (impostorBehaviour != null)
                            PlayerControl.LocalPlayer.Data.Role.IntroSound = impostorBehaviour.IntroSound;
                        break;
                }
                switch (role)
                {
                    case CustomRoles.Jackal:
                    case CustomRoles.JackalReaver:
                    case CustomRoles.JClient:
                        __instance.TeamTitle.text = Utils.GetRoleName(CustomRoles.Jackal);
                        __instance.TeamTitle.color = Utils.GetRoleColor(CustomRoles.Jackal);
                        __instance.ImpostorText.gameObject.SetActive(true);
                        __instance.ImpostorText.text = GetString("TeamJackal");
                        __instance.BackgroundBar.material.color = Utils.GetRoleColor(CustomRoles.Jackal);
                        break;
                    
                    case CustomRoles.MadSheriff:
                        __instance.ImpostorText.gameObject.SetActive(true);
                        var numImpostors = Main.NormalOptions.NumImpostors;
                        __instance.ImpostorText.text = numImpostors == 1
                            ? DestroyableSingleton<TranslationController>.Instance.GetString(StringNames.NumImpostorsS)
                            : __instance.ImpostorText.text = string.Format(DestroyableSingleton<TranslationController>.Instance.GetString(StringNames.NumImpostorsP), numImpostors);
                        __instance.ImpostorText.text = __instance.ImpostorText.text.Replace("[FF1919FF]", "<color=#FF1919FF>").Replace("[]", "</color>");
                        break;
                    
                }
            }
            else
            {
                switch (role.GetCustomRoleTypes())
                {
                    case CustomRoleTypes.Neutral:
                        __instance.TeamTitle.text = Utils.GetRoleName(role);
                        __instance.TeamTitle.color = Utils.GetRoleColor(role);
                        __instance.ImpostorText.gameObject.SetActive(true);
                        __instance.ImpostorText.text = role switch
                        {
                            CustomRoles.Egoist => GetString("TeamEgoist"),
                            CustomRoles.Jackal or CustomRoles.JackalReaver => GetString("TeamJackal"),
                            CustomRoles.JClient => GetString("TeamJackal"),
                            _ => GetString("NeutralInfo"),
                        };
                        __instance.BackgroundBar.material.color = Utils.GetRoleColor(role);
                        break;
                    case CustomRoleTypes.Animals:
                        __instance.TeamTitle.text = Utils.GetRoleName(role);
                        __instance.TeamTitle.color = Utils.GetRoleColor(role);
                        __instance.ImpostorText.gameObject.SetActive(true);
                        __instance.ImpostorText.text = GetString("TeamAnimals");
                        __instance.BackgroundBar.material.color = Utils.GetRoleColor(role);
                        break;
                    case CustomRoleTypes.Madmate:
                        __instance.TeamTitle.text = GetString("Madmate");
                        __instance.TeamTitle.color = Utils.GetRoleColor(CustomRoles.Madmate);
                        __instance.ImpostorText.text = GetString("TeamImpostor");
                        StartFadeIntro(__instance, Palette.CrewmateBlue, Palette.ImpostorRed);
                        break;
                }
            }
            switch (role)
            {
                case CustomRoles.StrayWolf:
                    __instance.TeamTitle.text = GetString("Impostor");
                    __instance.TeamTitle.color = Palette.ImpostorRed;
                    __instance.ImpostorText.gameObject.SetActive(false);
                    __instance.BackgroundBar.material.color = Palette.ImpostorRed;
                    break;
                case CustomRoles.Sheriff:
                case CustomRoles.Hunter:
                case CustomRoles.SillySheriff:
                case CustomRoles.Metaton:
                    __instance.BackgroundBar.material.color = Palette.CrewmateBlue;
                    __instance.ImpostorText.gameObject.SetActive(true);
                    var numImpostors = Main.NormalOptions.NumImpostors;
                    var text = numImpostors == 1
                        ? GetString(StringNames.NumImpostorsS)
                        : string.Format(GetString(StringNames.NumImpostorsP), numImpostors);
                    __instance.ImpostorText.text = text.Replace("[FF1919FF]", "<color=#FF1919FF>").Replace("[]", "</color>");
                    break;

                case CustomRoles.GM:
                    __instance.TeamTitle.text = Utils.GetRoleName(role);
                    __instance.TeamTitle.color = Utils.GetRoleColor(role);
                    __instance.BackgroundBar.material.color = Utils.GetRoleColor(role);
                    __instance.ImpostorText.gameObject.SetActive(false);
                    break;
            }

            if (Input.GetKey(KeyCode.RightShift))
            {
                __instance.TeamTitle.text = Main.ModNameRichText;
                __instance.ImpostorText.gameObject.SetActive(true);
                __instance.ImpostorText.text = "https://github.com/tukasa0001/TownOfHost" +
                    "\r\nOut Now on Github";
                __instance.TeamTitle.color = Color.cyan;
                StartFadeIntro(__instance, Color.cyan, Color.yellow);
            }
            if (Input.GetKey(KeyCode.RightControl))
            {
                __instance.TeamTitle.text = "Discord Server";
                __instance.ImpostorText.gameObject.SetActive(true);
                __instance.ImpostorText.text = "https://discord.gg/v8SFfdebpz";
                __instance.TeamTitle.color = Color.magenta;
                StartFadeIntro(__instance, Color.magenta, Color.magenta);
            }
        }
        private static async void StartFadeIntro(IntroCutscene __instance, Color start, Color end)
        {
            await Task.Delay(2000);
            int milliseconds = 0;
            while (true)
            {
                await Task.Delay(20);
                milliseconds += 20;
                float time = (float)milliseconds / (float)500;
                Color LerpingColor = Color.Lerp(start, end, time);
                if (__instance == null || milliseconds > 500)
                {
                    Logger.Info("ループを終了します", "StartFadeIntro");
                    break;
                }
                __instance.BackgroundBar.material.color = LerpingColor;
            }
        }
    }
    [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.BeginImpostor))]
    class BeginImpostorPatch
    {
        public static bool Prefix(IntroCutscene __instance, ref Il2CppSystem.Collections.Generic.List<PlayerControl> yourTeam)
        {
            CriticalStartFlowPacketDiagnostic.Mark(
                "SHOW_TEAM_BEGINIMPOSTOR_PREFIX",
                CriticalStartFlowPacketDiagnostic.PlayerSnapshot(PlayerControl.LocalPlayer));
            if (PlayerControl.LocalPlayer.Is(CustomRoles.Sheriff)
                ||PlayerControl.LocalPlayer.Is(CustomRoles.Hunter)
                ||PlayerControl.LocalPlayer.Is(CustomRoles.Metaton)
                ||PlayerControl.LocalPlayer.Is(CustomRoles.SillySheriff))
            {
                //シェリフの場合はキャンセルしてBeginCrewmateに繋ぐ
                yourTeam = new Il2CppSystem.Collections.Generic.List<PlayerControl>();
                yourTeam.Add(PlayerControl.LocalPlayer);
                foreach (var pc in Main.AllPlayerControls)
                {
                    if (!pc.AmOwner) yourTeam.Add(pc);
                }
                __instance.BeginCrewmate(yourTeam);
                __instance.overlayHandle.color = Palette.CrewmateBlue;
                return false;
            }
            BeginCrewmatePatch.Prefix(__instance, ref yourTeam);
            return true;
        }
        public static void Postfix(IntroCutscene __instance, ref Il2CppSystem.Collections.Generic.List<PlayerControl> yourTeam)
        {
            BeginCrewmatePatch.Postfix(__instance, ref yourTeam);
        }
    }
    [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
    class IntroCutsceneDestroyPatch
    {
        public static void Postfix(IntroCutscene __instance)
        {
            SetUpRoleTextPatch.RestoreVanillaAllRoleIntroOverlayABV1();
            if (!GameStates.IsInGame) return;
            Options.FirstTurnMeetingInProgress =
                AmongUsClient.Instance.AmHost &&
                Options.FirstTurnMeeting.GetBool() &&
                Options.CurrentGameMode == CustomGameMode.Standard &&
                MeetingStates.FirstMeeting;
            Main.isFirstTurn = true;

            var mapId = Main.NormalOptions.MapId;
            // エアシップではまだ湧かない
            if ((MapNames)mapId != MapNames.Airship)
            {
                foreach (var state in PlayerState.AllPlayerStates.Values)
                {
                    state.HasSpawned = true;
                }
            }

            if (AmongUsClient.Instance.AmHost)
            {
                if (mapId != 4)
                {
                    Main.AllPlayerControls.Do(pc =>
                    {
                        pc.GetRoleClass()?.OnSpawn(true);
                        pc.SyncSettings();
                        if (!Options.IsFirstTurnMeetingActive)
                            pc.RpcResetAbilityCooldown();
                    });
                    if (Options.FixFirstKillCooldown.GetBool() && !Options.IsFirstTurnMeetingActive)
                        _ = new LateTask(() =>
                        {
                            Main.AllPlayerControls.Do(pc => pc.SetKillCooldown(Main.AllPlayerKillCooldown[pc.PlayerId] - 2f));
                        }, 2f, "FixKillCooldownTask");
                }
                _ = new LateTask(() =>
                {
                    foreach (var viewer in Main.AllPlayerControls)
                    {
                        if (viewer == null || viewer.AmOwner || viewer.Data == null || viewer.Data.Disconnected)
                            continue;

                        if (!viewer.TryGetClientId(out var clientId)) continue;
                        var hasBarrierCandidate =
                            SelectRolesPatch.ClientReadyBarrierCandidateByViewer.TryGetValue(
                                viewer.PlayerId,
                                out var barrierCandidateId);

                        var sender = CustomRpcSender.Create(
                            $"SetImpostorForServer Viewer={viewer.PlayerId}",
                            Hazel.SendOption.Reliable,
                            false);

                        sender.StartMessage(clientId);

                        var targetCount = 0;
                        var candidateWriteCount = 0;

                        foreach (var target in Main.AllPlayerControls)
                        {
                            if (target == null)
                                continue;

                            var canOverride =
                                hasBarrierCandidate &&
                                target.PlayerId == barrierCandidateId;

                            // CLIENTREADY_CANDIDATE_INTENDED_ROLE_RESTORE_AB_V9:
                            // ClientReady barrierで一時未確定にしたcandidateだけは、
                            // 2秒後にShapeshifterへ固定せず、開始時に記録済みの
                            // viewer-specific VanillaIntroRoleMapへcanOverride=trueで戻す。
                            var roleForViewer = RoleTypes.Shapeshifter;
                            var intendedRoleFound = false;
                            if (canOverride &&
                                SelectRolesPatch.TryGetExileDisplayRoleViewPermanent(
                                    viewer.PlayerId,
                                    target.PlayerId,
                                    out var intendedRole))
                            {
                                roleForViewer = intendedRole;
                                intendedRoleFound = true;
                            }

                            if (canOverride)
                            {
                                Logger.Info(
                                    $"Stage=CANDIDATE_FINALIZE | Viewer={viewer.PlayerId} | Candidate={target.PlayerId} | IntendedRoleFound={intendedRoleFound} | AppliedRole={roleForViewer} | CanOverride=True",
                                    "CLIENTREADY_CANDIDATE_INTENDED_ROLE_RESTORE_AB_V9");
                            }

                            sender.StartRpc(target.NetId, (byte)RpcCalls.SetRole)
                                .Write((ushort)roleForViewer)
                                .Write(canOverride)
                                .EndRpc();

                            targetCount++;
                            if (canOverride)
                                candidateWriteCount++;
                        }

                        sender.EndMessage();

                        Logger.Info(
                            $"CLIENTREADY_INTRO_BARRIER_AB_PHASE1_RETRY2 SetImpostorForServer " +
                            $"Viewer={viewer.PlayerId} ClientId={viewer.GetClientId()} " +
                            $"Barrier={hasBarrierCandidate} Candidate=" +
                            $"{(hasBarrierCandidate ? barrierCandidateId : byte.MaxValue)} " +
                            $"CandidateWrites={candidateWriteCount} Targets={targetCount} " +
                            $"RootLength={sender.stream.Length}",
                            "ClientReadyIntroBarrierAB1Retry2");

                        sender.SendMessage();
                    }
                }, 2f, "SetImpostorForServer");
                if (PlayerControl.LocalPlayer.Is(CustomRoles.GM))
                {
                    PlayerControl.LocalPlayer.RpcExile();
                    PlayerState.GetByPlayerId(PlayerControl.LocalPlayer.PlayerId).SetDead();
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

                // そのままだとホストのみDesyncImpostorの暗室内での視界がクルー仕様になってしまう
                var roleInfo = PlayerControl.LocalPlayer.GetCustomRole().GetRoleInfo();
                var amDesyncImpostor = roleInfo?.IsDesyncImpostor == true;
                if (amDesyncImpostor)
                {
                    PlayerControl.LocalPlayer.Data.Role.AffectedByLightAffectors = false;
                }
            }
            Logger.Info("OnDestroy", "IntroCutscene");

            GameStates.InTask = true;
            Logger.Info("タスクフェイズ開始", "Phase");

            if (AmongUsClient.Instance.AmHost &&
                Options.FirstTurnMeeting.GetBool() &&
                Options.CurrentGameMode == CustomGameMode.Standard &&
                MeetingStates.FirstMeeting)
            {
                GameStates.InTask = false;
                var firstTurnMeetingLag = 0.23f;
                if (GameStates.IsOnlineGame)
                {
                    var serverName = ServerManager.Instance.CurrentRegion.TranslateName;
                    if (serverName is StringNames.ServerNA or StringNames.ServerEU or StringNames.ServerSA)
                        firstTurnMeetingLag = 0.43f;
                }

                _ = new LateTask(() => TryStartFirstTurnMeeting(), 0.7f + firstTurnMeetingLag, "FirstTurnMeeting");
            }
        }

        private static void TryStartFirstTurnMeeting()
        {
            if (!Options.IsFirstTurnMeetingActive)
            {
                Options.FirstTurnMeetingInProgress = false;
                Main.isFirstTurn = false;
                if (GameStates.IsInGame)
                {
                    GameStates.InTask = true;
                }
                return;
            }

            if (GameStates.IsMeeting || MeetingStates.MeetingCalled) return;

            var hasUnspawnedPlayer = Main.AllPlayerControls.Any(pc =>
                pc != null &&
                pc.Data != null &&
                !pc.Data.Disconnected &&
                PlayerState.GetByPlayerId(pc.PlayerId)?.HasSpawned != true);

            if (hasUnspawnedPlayer)
            {
                _ = new LateTask(() => TryStartFirstTurnMeeting(), 0.5f, "WaitFirstTurnMeetingSpawn");
                return;
            }

            var reporter = PlayerControl.LocalPlayer;
            if (reporter == null || reporter.Data == null)
            {
                Logger.Warn("強制会議の通報者を取得できなかったため中止しました。", "FirstTurnMeeting");
                Options.FirstTurnMeetingInProgress = false;
                Main.isFirstTurn = false;
                if (GameStates.IsInGame)
                {
                    GameStates.InTask = true;
                }
                return;
            }

            var target = reporter.Data;

            GameStates.InTask = false;
            Main.isFirstTurn = false;
            MeetingDisplay.CreateButtonInfo(reporter, target);
            MeetingTimeManager.OnReportDeadBody();
            Utils.NotifyRoles(isForMeeting: true, NoCache: true);
            Utils.SyncAllSettings();
            Logger.Info($"{reporter.GetNameWithRole()}を通報者としてゲーム開始直後の強制会議を発動します。", "FirstTurnMeeting");
            reporter.NoCheckStartMeeting(target);
        }
    }
}
