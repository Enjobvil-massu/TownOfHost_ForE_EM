using System;
using System.Collections.Generic;
using AmongUs.Data;
using AmongUs.GameOptions;
using HarmonyLib;
using InnerNet;
using UnityEngine;

using TownOfHostForE.Modules;
using TownOfHostForE.Roles;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Neutral;
using static TownOfHostForE.Translator;
using TownOfHostForE.Modules.OtherServices;

namespace TownOfHostForE
{
    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
    class OnGameJoinedPatch
    {
        public static void Postfix(AmongUsClient __instance)
        {
            // HACKING_SESSION_EPOCH_DIAGNOSTIC_AB_V1
            TownOfHostForE.Modules.HackingSessionEpochDiagnostic.Safe(() =>
                TownOfHostForE.Modules.HackingSessionEpochDiagnostic.BeginLocalSession(__instance));
            while (!Options.IsLoaded) System.Threading.Tasks.Task.Delay(1);
            Logger.Info($"{__instance.GameId}に参加", "OnGameJoined");
            TownOfHostForE.Modules.HackingSessionEpochDiagnostic.Safe(() =>
                TownOfHostForE.Modules.HackingSessionEpochDiagnostic.Mark("PLAYER_VERSION_REPLACE",
                    $"Source=OnGameJoined CountBefore={Main.playerVersion.Count}"));
            Main.playerVersion = new Dictionary<byte, PlayerVersion>();
            RPC.RpcVersionCheck();
            SoundManager.Instance.ChangeAmbienceVolume(DataManager.Settings.Audio.AmbienceVolume);

            ChatUpdatePatch.DoBlockChat = false;
            GameStates.InGame = false;
            ErrorText.Instance.Clear();
            BGMSettings.SetLobbyBGM();
            if (AmongUsClient.Instance.AmHost) //以下、ホストのみ実行
            {
                if (Main.NormalOptions.KillCooldown == 0f)
                    Main.NormalOptions.KillCooldown = Main.LastKillCooldown.Value;

                AURoleOptions.SetOpt(Main.NormalOptions.Cast<IGameOptions>());
                if (AURoleOptions.ShapeshifterCooldown == 0f)
                    AURoleOptions.ShapeshifterCooldown = Main.LastShapeshifterCooldown.Value;
            }
        }
    }
    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.DisconnectInternal))]
    class DisconnectInternalPatch
    {
        public static void Prefix(InnerNetClient __instance, DisconnectReasons reason, string stringReason)
        {
            // HACKING_SESSION_EPOCH_DIAGNOSTIC_AB_V1
            TownOfHostForE.Modules.HackingSessionEpochDiagnostic.Safe(() =>
                TownOfHostForE.Modules.HackingSessionEpochDiagnostic.MarkDisconnectInternal(
                    __instance, reason, stringReason));
            TownOfHostForE.Modules.HackingSessionEpochDiagnostic.Safe(() =>
                TownOfHostForE.Modules.HackingSessionEpochDiagnostic.Mark("PLAYER_VERSION_CLEAR",
                    $"Source=DisconnectInternal CountBefore={Main.playerVersion.Count}"));
            Main.playerVersion.Clear();
            TownOfHostForE.Modules.HackingSessionEpochDiagnostic.Safe(() =>
                TownOfHostForE.Modules.HackingSessionEpochDiagnostic.Mark("POST_DISCONNECT_VERSIONCHECK_CALL"));
            RPC.RpcVersionCheck();
            if (AmongUsClient.Instance.AmHost && GameStates.IsLobby)
                ChatCommands.StartButtonReset = true;

            Logger.Info($"切断(理由:{reason}:{stringReason}, ping:{__instance.Ping})", "Session");

            if (AmongUsClient.Instance.AmHost && GameStates.InGame)
                GameManager.Instance.RpcEndGame(GameOverReason.ImpostorDisconnect, false);

            //ホストであり、霊界投票が有効であれば切断時にCSVを保存する。
            if (AmongUsClient.Instance.AmHost && BetWinTeams.BetWinTeamMode.GetBool() && BetWinTeams.readedCSV)
            {
                BetWinTeams.WriteCSVPlayerData();
            }
            //if(BGMSettings.BGMMode.GetBool())
            BGMSettings.StopSound();


            CustomRoleManager.Dispose();
        }
    }
    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerJoined))]
    class OnPlayerJoinedPatch
    {
        public static void Postfix(AmongUsClient __instance, [HarmonyArgument(0)] ClientData client)
        {
            // HACKING_SESSION_EPOCH_DIAGNOSTIC_AB_V1
            TownOfHostForE.Modules.HackingSessionEpochDiagnostic.Safe(() =>
                TownOfHostForE.Modules.HackingSessionEpochDiagnostic.MarkPlayerJoined(__instance, client));
            Logger.Info($"{client.PlayerName}(ClientID:{client.Id})が参加", "Session");
            if (AmongUsClient.Instance.AmHost && client.FriendCode == "" && Options.KickPlayerFriendCodeNotExist.GetBool())
            {
                AmongUsClient.Instance.KickPlayer(client.Id, false);
                Logger.SendInGame(string.Format(GetString("Message.KickedByNoFriendCode"), client.PlayerName));
                Logger.Info($"フレンドコードがないプレイヤーを{client?.PlayerName}をキックしました。", "Kick");
            }
            if (DestroyableSingleton<FriendsListManager>.Instance.IsPlayerBlockedUsername(client.FriendCode) && AmongUsClient.Instance.AmHost)
            {
                AmongUsClient.Instance.KickPlayer(client.Id, true);
                Logger.Info($"ブロック済みのプレイヤー{client?.PlayerName}({client.FriendCode})をBANしました。", "BAN");
            }
            BanManager.CheckBanPlayer(client);
            BanManager.CheckDenyNamePlayer(client);
            // HACKING_VERSIONCHECK_ONPLAYERJOINED_TARGETED_AB_V3:
            // Host announces its MOD version only to the newly joined client.
            // Existing MOD clients must not receive another host VersionCheck when somebody else joins.
            // Non-host OnPlayerJoined behavior remains suppressed.
            if (__instance.AmHost)
            {
                RPC.RpcVersionCheck(client.Id);
                Logger.Info(
                    $"Host RpcVersionCheck on OnPlayerJoined: JoinedClientId={client.Id}, LocalClientId={__instance.ClientId}",
                    "VersionCheckAB");
            }
            else
            {
                Logger.Info(
                    $"Skip non-host RpcVersionCheck on OnPlayerJoined: JoinedClientId={client.Id}, LocalClientId={__instance.ClientId}",
                    "VersionCheckAB");
            }
        }
    }
    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft))]
    class OnPlayerLeftPatch
    {
        static void Prefix([HarmonyArgument(0)] ClientData data)
        {
            // ONPLAYERLEFT_PREFIX_NULL_GUARD_PERMANENT:
            // OnPlayerLeftはロビー再接続/切断整理時にCharacter未生成のClientDataでも呼ばれ得る。
            // Characterが存在する通常退出時のExecutioner/Lawyer処理は従来どおり維持する。
            if (data == null || data.Character == null) return;

            if (CustomRoles.Executioner.IsPresent())
                Executioner.ChangeRoleByTarget(data.Character.PlayerId);
            if (CustomRoles.Lawyer.IsPresent())
                Lawyer.ChangeRoleByTarget(data.Character);
        }
        public static void Postfix(AmongUsClient __instance, [HarmonyArgument(0)] ClientData data, [HarmonyArgument(1)] DisconnectReasons reason)
        {
            // HACKING_SESSION_EPOCH_DIAGNOSTIC_AB_V1
            TownOfHostForE.Modules.HackingSessionEpochDiagnostic.Safe(() =>
                TownOfHostForE.Modules.HackingSessionEpochDiagnostic.MarkPlayerLeft(__instance, data, reason));
            var isFailure = false;

            try
            {
                if (data == null)
                {
                    isFailure = true;
                    Logger.Warn("退出者のClientDataがnull", nameof(OnPlayerLeftPatch));
                }
                else if (data.Character == null)
                {
                    isFailure = !(GameStates.IsLobby && reason == DisconnectReasons.DuplicateConnectionDetected);
                    Logger.Warn("退出者のPlayerControlがnull", nameof(OnPlayerLeftPatch));
                }
                else if (data.Character.Data == null)
                {
                    isFailure = true;
                    Logger.Warn("退出者のPlayerInfoがnull", nameof(OnPlayerLeftPatch));
                }
                else
                {
                    if (GameStates.IsInGame)
                    {
                        if (data.Character.Is(CustomRoles.Lovers) && !data.Character.Data.IsDead)
                        {
                            var loversList = LoversManager.GetLoversList(data.Character);
                            byte ownerId = loversList[0].PlayerId;
                            foreach (var lovers in loversList.ToArray())
                            {
                                PlayerState.GetByPlayerId(lovers.PlayerId).RemoveSubRole(CustomRoles.Lovers);
                            }
                            LoversManager.LoversMaster.Remove(ownerId);
                        }
                        var state = PlayerState.GetByPlayerId(data.Character.PlayerId);
                        if (state.DeathReason == CustomDeathReason.etc) //死因が設定されていなかったら
                        {
                            state.DeathReason = CustomDeathReason.Disconnected;
                            state.SetDead();
                        }
                        data.Character?.GetRoleClass()?.Dispose();
                        AntiBlackout.OnDisconnect(data.Character.Data);
                        PlayerGameOptionsSender.RemoveSender(data.Character);
                    }
                    // HACKING_SESSION_EPOCH_DIAGNOSTIC_AB_V1
                    var removedPlayerVersion = Main.playerVersion.Remove(data.Character.PlayerId);
                    TownOfHostForE.Modules.HackingSessionEpochDiagnostic.Safe(() =>
                        TownOfHostForE.Modules.HackingSessionEpochDiagnostic.Mark("PLAYER_VERSION_REMOVE",
                            $"PlayerId={data.Character.PlayerId} Removed={removedPlayerVersion} CountAfter={Main.playerVersion.Count}"));
                    Logger.Info($"{data.PlayerName}(ClientID:{data.Id})が切断(理由:{reason}, ping:{AmongUsClient.Instance.Ping})", "Session");
                }
            }
            catch (Exception e)
            {
                Logger.Warn("切断処理中に例外が発生", nameof(OnPlayerLeftPatch));
                Logger.Exception(e, nameof(OnPlayerLeftPatch));
                isFailure = true;
            }

            if (isFailure)
            {
                Logger.Warn($"正常に完了しなかった切断 - 名前:{(data == null || data.PlayerName == null ? "(不明)" : data.PlayerName)}, 理由:{reason}, ping:{AmongUsClient.Instance.Ping}", "Session");
                ErrorText.Instance.AddError(AmongUsClient.Instance.GameState is InnerNetClient.GameStates.Started ? ErrorCode.OnPlayerLeftPostfixFailedInGame : ErrorCode.OnPlayerLeftPostfixFailedInLobby);
            }
        }

    }
    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.CreatePlayer))]
    class CreatePlayerPatch
    {
        public static void Postfix(AmongUsClient __instance, [HarmonyArgument(0)] ClientData client)
        {
            if (AmongUsClient.Instance.AmHost)
            {
                // CREATEPLAYER_JOIN_FULLSYNC_SUPPRESS_PERMANENT: CreatePlayer does not rebroadcast all custom settings; MOD joins use the existing targeted VersionCheck settings path.
                _ = new LateTask(() =>
                {
                    if (client.Character == null) return;
                    //if (AmongUsClient.Instance.IsGamePublic) Utils.SendMessage(string.Format(GetString("Message.AnnounceUsingTOH"), Main.PleviewPluginVersion), client.Character.PlayerId);
                    Utils.JoinLobbyModInfo(client);
                    TemplateManager.SendTemplate("welcome", client.Character.PlayerId, true);
                    BetWinTeams.JoinLobbySyougo(client.Character);
                    //ポスと
                    Main.BlueSkyMain.PostRecruit();

                }, 3f, "Welcome Message");
                if (Options.AutoDisplayLastResult.GetBool() && PlayerState.AllPlayerStates.Count != 0 && Main.clientIdList.Contains(client.Id))
                {
                    _ = new LateTask(() =>
                    {
                        if (!AmongUsClient.Instance.IsGameStarted && client.Character != null)
                        {
                            Main.isChatCommand = true;
                            Utils.ShowLastResult(client.Character.PlayerId);
                        }
                    }, 3f, "DisplayLastRoles");
                }
                if (Options.AutoDisplayKillLog.GetBool() && PlayerState.AllPlayerStates.Count != 0 && Main.clientIdList.Contains(client.Id))
                {
                    _ = new LateTask(() =>
                    {
                        if (!GameStates.IsInGame && client.Character != null)
                        {
                            Main.isChatCommand = true;
                            Utils.ShowKillLog(client.Character.PlayerId);
                        }
                    }, 3f, "DisplayKillLog");
                }
            }
        }
    }
}