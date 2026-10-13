using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Assets.CoreScripts;
using HarmonyLib;
using Hazel;
using System.Text.RegularExpressions;

using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Descriptions;
using static TownOfHostForE.Translator;
using TownOfHostForE.Roles.Crewmate;
using TownOfHostForE.Roles.AddOns.Common;
using AmongUs.GameOptions;
using Rewired;
using static System.Int32;
using TownOfHostForE.GameMode;
using LibCpp2IL.MachO;
using UnityEngine;

namespace TownOfHostForE
{
    [HarmonyPatch(typeof(ChatController), nameof(ChatController.SendChat))]
    class ChatCommands
    {
        public static List<string> ChatHistory = new();
        private static Dictionary<CustomRoles, string> roleCommands;

        public enum ChatInputKind
        {
            NormalChat,
            AcceptedCommand,
            RejectedSlashInput,
        }

        public readonly struct ChatInputParseResult
        {
            public ChatInputKind Kind { get; }
            public string RoutedText { get; }

            public ChatInputParseResult(ChatInputKind kind, string routedText)
            {
                Kind = kind;
                RoutedText = routedText;
            }
        }

        public static ChatInputParseResult ParseChatInput(string rawText)
        {
            if (string.IsNullOrEmpty(rawText) || rawText[0] != '/')
                return new(ChatInputKind.NormalChat, rawText);

            const string commandPrefix = "/cmd ";
            if (!rawText.StartsWith(commandPrefix))
                return new(ChatInputKind.RejectedSlashInput, string.Empty);

            var commandText = rawText[commandPrefix.Length..];
            if (commandText.Length == 0 ||
                commandText[0] == '/' ||
                char.IsWhiteSpace(commandText[0]))
            {
                return new(ChatInputKind.RejectedSlashInput, string.Empty);
            }

            return new(ChatInputKind.AcceptedCommand, "/" + commandText);
        }

        private enum RemoteParticipantCommandKind
        {
            None,
            ChatCommand,
            Guess,
            Bet,
        }

        private static RemoteParticipantCommandKind GetRemoteParticipantCommandKind(string routedText)
        {
            if (string.IsNullOrEmpty(routedText) || routedText[0] != '/')
                return RemoteParticipantCommandKind.None;

            var separatorIndex = routedText.IndexOf(' ');
            var command = separatorIndex < 0
                ? routedText
                : routedText[..separatorIndex];

            switch (command)
            {
                case "/l":
                case "/lastresult":
                case "/kl":
                case "/killlog":
                case "/n":
                case "/now":
                case "/h":
                case "/help":
                case "/m":
                case "/myrole":
                case "/t":
                case "/template":
                case "/vo":
                case "/voice":
                    return RemoteParticipantCommandKind.ChatCommand;

                case "/BetPoint":
                case "/bp":
                case "/AllBetPoint":
                case "/albp":
                case "/PointShopList":
                case "/psl":
                case "/BuyShop":
                case "/bs":
                case "/SetShougo":
                case "/ss":
                case "/MyShougoList":
                case "/msl":
                case "/SetMyShougo":
                case "/sms":
                case "/Bet":
                case "/b":
                    return RemoteParticipantCommandKind.Bet;
            }

            if (routedText.Equals("/id", StringComparison.OrdinalIgnoreCase) ||
                routedText.Equals("/guesslist", StringComparison.OrdinalIgnoreCase) ||
                routedText.Equals("/g", StringComparison.OrdinalIgnoreCase))
            {
                return RemoteParticipantCommandKind.Guess;
            }

            // GUESS_COMMAND_BT_ONLY_PERMANENT: Remote Dispatchでも /cmd bt のみ受理
            if (command.Equals("/bt", StringComparison.OrdinalIgnoreCase))
            {
                return RemoteParticipantCommandKind.Guess;
            }

            return RemoteParticipantCommandKind.None;
        }

        public static bool IsRemoteParticipantCommand(string routedText) =>
            GetRemoteParticipantCommandKind(routedText) != RemoteParticipantCommandKind.None;

        //ロビーリセット一式
        public static int LobbyLimit = 15;
        public static bool StartButtonReset = false;
        public static bool ResetVersionCheckFlag = false;
        public static List<byte> OtherVersionPlayerId = new();

        public static bool Prefix(ChatController __instance)
        {
            // クイックチャットなら横流し
            if (__instance.quickChatField.Visible)
            {
                return true;
            }
            // 入力欄に何も書かれてなければブロック
            if (__instance.freeChatField.textArea.text == "")
            {
                return false;
            }
            __instance.timeSinceLastMessage = 3f;
            var rawText = __instance.freeChatField.textArea.text;
            var chatInput = ParseChatInput(rawText);
            if (chatInput.Kind == ChatInputKind.RejectedSlashInput)
            {
                __instance.freeChatField.textArea.Clear();
                __instance.freeChatField.textArea.SetText("");
                return false;
            }
            var text = chatInput.RoutedText;
            if (chatInput.Kind == ChatInputKind.AcceptedCommand)
                Logger.Info("CMD_PREFIX_PERMANENT Local normalized", "ChatCommand");
            if (ChatHistory.Count == 0 || ChatHistory[^1] != rawText) ChatHistory.Add(rawText);
            ChatControllerUpdatePatch.CurrentHistorySelection = ChatHistory.Count;
            string[] args = text.Split(' ');
            var commandKind = chatInput.Kind == ChatInputKind.AcceptedCommand
                ? GetRemoteParticipantCommandKind(text)
                : RemoteParticipantCommandKind.None;
            string subArgs = "";
            var canceled = chatInput.Kind == ChatInputKind.AcceptedCommand;
            var cancelVal = "";
            Main.isChatCommand = true;
            Logger.Info(text, "SendChat");

            if (commandKind == RemoteParticipantCommandKind.Guess &&
                GuessManager.GuesserMsg(PlayerControl.LocalPlayer, text))
            {
                canceled = true;
            }

            if (chatInput.Kind == ChatInputKind.NormalChat)
            {
                var tag = !PlayerControl.LocalPlayer.Data.IsDead ? "SendChatHost" : "SendChatDeadHost";
                if (text.StartsWith("試合結果:") || text.StartsWith("キル履歴:")) tag = "SendSystemChat";
                VoiceReader.ReadHost(text, tag);
            }

            switch (args[0])
            {
                case "/dump":
                    canceled = true;
                    Utils.DumpLog();
                    break;
                case "/v":
                case "/version":
                    canceled = true;
                    string version_text = "";
                    foreach (var kvp in Main.playerVersion.OrderBy(pair => pair.Key))
                    {
                        version_text += $"{kvp.Key}:{Utils.GetPlayerById(kvp.Key)?.Data?.PlayerName}:{kvp.Value.forkId}/{kvp.Value.version}({kvp.Value.tag})\n";
                    }
                    if (version_text != "") HudManager.Instance.Chat.AddChat(PlayerControl.LocalPlayer, version_text);
                    break;
                default:
                    Main.isChatCommand = false;
                    break;
            }
            if (!AmongUsClient.Instance.AmHost &&
                chatInput.Kind == ChatInputKind.AcceptedCommand &&
                IsRemoteParticipantCommand(text))
            {
                RPC.TrySendChatCommandRequest(rawText);
            }
            if (AmongUsClient.Instance.AmHost)
            {
                Main.isChatCommand = true;

                if (!GameStates.IsLobby)
                {
                    var cRole = PlayerControl.LocalPlayer.GetCustomRole();
                    if (!cRole.IsNotAssignRoles())
                    {
                        PlayerControl.LocalPlayer.GetRoleClass().OnReceiveChat(PlayerControl.LocalPlayer, text);
                    }
                    Ojou.OjouOnReceiveChat(PlayerControl.LocalPlayer, args[0]);
                    Chu2Byo.Chu2OnReceiveChat(PlayerControl.LocalPlayer, args[0]);
                    WordLimit.OnReceiveChat(PlayerControl.LocalPlayer, args[0]);
                }
                var shouldRunBetHandler =
                    chatInput.Kind == ChatInputKind.NormalChat ||
                    commandKind == RemoteParticipantCommandKind.ChatCommand ||
                    commandKind == RemoteParticipantCommandKind.Bet ||
                    args[0] == "/resetbp";
                if (shouldRunBetHandler)
                    canceled |= BetWinTeams.BetOnReceiveChat(PlayerControl.LocalPlayer, text);
                switch (args[0])
                {
                    case "/win":
                    case "/winner":
                        canceled = true;
                        Utils.SendMessage("Winner: " + string.Join(",", Main.winnerList.Select(b => Main.AllPlayerNames[b])));
                        break;

                    case "/l":
                    case "/lastresult":
                        canceled = true;
                        Utils.ShowLastResult();
                        break;

                    case "/kl":
                    case "/killlog":
                        canceled = true;
                        Utils.ShowKillLog();
                        break;

                    case "/r":
                    case "/rename":
                        canceled = true;
                        Main.nickName = args.Length > 1 ? Main.nickName = args[1] : "";
                        break;

                    case "/hn":
                    case "/hidename":
                        canceled = true;
                        Main.HideName.Value = args.Length > 1 ? args.Skip(1).Join(delimiter: " ") : Main.HideName.DefaultValue.ToString();
                        GameStartManagerPatch.HideName.text = Main.HideName.Value;
                        break;

                    case "/n":
                    case "/now":
                        canceled = true;
                        subArgs = args.Length < 2 ? "" : args[1];
                        switch (subArgs)
                        {
                            case "r":
                            case "roles":
                                Utils.ShowActiveRoles();
                                break;
                            default:
                                Utils.ShowActiveSettings();
                                break;
                        }
                        break;
                    case "/SetWordLimit":
                    case "/swl":
                        canceled = true;
                        if (args.Length < 2) break;
                        WordLimit.SetLimitWord(args[1]);
                        Utils.SendMessage($"制限ワード「{args[1]}」を適用しました！\n制限モードを有効にしてお楽しみください。");
                        break;
                    case "/w":
                        canceled = true;
                        if (!GameStates.IsInGame) break;
                        subArgs = args.Length < 2 ? "" : args[1];
                        switch (subArgs)
                        {
                            case "crewmate":
                                GameManager.Instance.enabled = false;
                                CustomWinnerHolder.WinnerTeam = CustomWinner.Crewmate;
                                foreach (var player in Main.AllPlayerControls.Where(pc => pc.Is(CustomRoleTypes.Crewmate)))
                                {
                                    CustomWinnerHolder.WinnerIds.Add(player.PlayerId);
                                }
                                GameEndChecker.StartEndGame(GameOverReason.CrewmatesByTask);
                                break;
                            case "impostor":
                                GameManager.Instance.enabled = false;
                                CustomWinnerHolder.WinnerTeam = CustomWinner.Impostor;
                                foreach (var player in Main.AllPlayerControls.Where(pc => pc.Is(CustomRoleTypes.Impostor) || pc.Is(CustomRoleTypes.Madmate)))
                                {
                                    CustomWinnerHolder.WinnerIds.Add(player.PlayerId);
                                }
                                GameEndChecker.StartEndGame(GameOverReason.ImpostorsByKill);
                                break;
                            case "none":
                                GameManager.Instance.enabled = false;
                                CustomWinnerHolder.WinnerTeam = CustomWinner.None;
                                GameEndChecker.StartEndGame(GameOverReason.ImpostorsByKill);
                                break;
                            case "jackal":
                                GameManager.Instance.enabled = false;
                                CustomWinnerHolder.WinnerTeam = CustomWinner.Jackal;
                                foreach (var player in Main.AllPlayerControls.Where(pc => (pc.GetCustomRole() is CustomRoles.Jackal or CustomRoles.JackalReaver) || pc.Is(CustomRoles.JSchrodingerCat)))
                                {
                                    CustomWinnerHolder.WinnerIds.Add(player.PlayerId);
                                }
                                GameEndChecker.StartEndGame(GameOverReason.ImpostorsByKill);
                                break;
                            case "animals":
                                GameManager.Instance.enabled = false;
                                CustomWinnerHolder.WinnerTeam = CustomWinner.Animals;
                                foreach (var player in Main.AllPlayerControls.Where(pc => pc.Is(CustomRoles.Coyote) ||
                                                                                    pc.Is(CustomRoles.Vulture) ||
                                                                                    pc.Is(CustomRoles.Badger) ||
                                                                                    pc.Is(CustomRoles.Braki) ||
                                                                                    pc.Is(CustomRoles.Leopard) ||
                                                                                    pc.Is(CustomRoles.Nyaoha) ||
                                                                                    pc.Is(CustomRoles.Chicken) ||
                                                                                    pc.Is(CustomRoles.Dolphin) ||
                                                                                    pc.Is(CustomRoles.Monkey) ||
                                                                                    pc.Is(CustomRoles.Kraken) ||
                                                                                    pc.Is(CustomRoles.AOjouSama) ||
                                                                                    pc.Is(CustomRoles.ASchrodingerCat)))
                                {
                                    CustomWinnerHolder.WinnerIds.Add(player.PlayerId);
                                }
                                GameEndChecker.StartEndGame(GameOverReason.ImpostorsByKill);
                                break;

                            default:
                                __instance.AddChat(PlayerControl.LocalPlayer, "crewmate | impostor | jackal | animals | none");
                                cancelVal = "/cmd w";
                                break;
                        }
                        break;

                    case "/dis":
                        canceled = true;
                        if (!GameStates.IsInGame) break;
                        subArgs = args.Length < 2 ? "" : args[1];
                        switch (subArgs)
                        {
                            case "crewmate":
                                GameManager.Instance.enabled = false;
                                GameManager.Instance.RpcEndGame(GameOverReason.CrewmateDisconnect, false);
                                ShipStatus.Instance.RpcUpdateSystem(SystemTypes.Admin, 0);
                                break;

                            case "impostor":
                                GameManager.Instance.enabled = false;
                                GameManager.Instance.RpcEndGame(GameOverReason.ImpostorDisconnect, false);
                                ShipStatus.Instance.RpcUpdateSystem(SystemTypes.Admin, 0);
                                break;

                            default:
                                __instance.AddChat(PlayerControl.LocalPlayer, "crewmate | impostor");
                                cancelVal = "/cmd dis";
                                break;
                        }
                        break;

                    case "/h":
                    case "/help":
                        canceled = true;
                        subArgs = args.Length < 2 ? "" : args[1];
                        switch (subArgs)
                        {
                            case "r":
                            case "roles":
                                subArgs = args.Length < 3 ? "" : args[2];
                                GetRolesInfo(subArgs);
                                break;

                            case "a":
                            case "addons":
                                subArgs = args.Length < 3 ? "" : args[2];
                                switch (subArgs)
                                {
                                    case "lastimpostor":
                                    case "limp":
                                        Utils.SendMessage(
                                            Utils.AppendRoleHelpNetworkBottomPadding(
                                                Utils.GetRoleName(CustomRoles.LastImpostor) +
                                                GetString("LastImpostorInfoLong")));
                                        break;

                                    default:
                                        Utils.SendMessage($"{GetString("Command.h_args")}:\n lastimpostor(limp)");
                                        break;
                                }
                                break;

                            case "m":
                            case "modes":
                                subArgs = args.Length < 3 ? "" : args[2];
                                switch (subArgs)
                                {
                                    case "hideandseek":
                                    case "has":
                                        Utils.SendMessage(GetString("HideAndSeekInfo"));
                                        break;

                                    case "nogameend":
                                    case "nge":
                                        Utils.SendMessage(GetString("NoGameEndInfo"));
                                        break;

                                    case "syncbuttonmode":
                                    case "sbm":
                                        Utils.SendMessage(GetString("SyncButtonModeInfo"));
                                        break;

                                    case "randommapsmode":
                                    case "rmm":
                                        Utils.SendMessage(GetString("RandomMapsModeInfo"));
                                        break;

                                    default:
                                        Utils.SendMessage($"{GetString("Command.h_args")}:\n hideandseek(has), nogameend(nge), syncbuttonmode(sbm), randommapsmode(rmm)");
                                        break;
                                }
                                break;

                            case "n":
                            case "now":
                                Utils.ShowActiveSettingsHelp();
                                break;
                            case "b":
                            case "bet":
                                //判定自体は霊界の奴に持たせる
                                break;

                            default:
                                Utils.ShowHelp();
                                break;
                        }
                        break;

                    case "/m":
                    case "/myrole":
                        canceled = true;
                        if (GameStates.IsInGame)
                        {
                            var role = PlayerControl.LocalPlayer.GetCustomRole();
                            string roleHelp;
                            // CMD_ROLE_DESC_PERMANENT: Crewmate・Impostorは既存説明へフォールバック
                            // VIPER_JUDGE_CMD_M_OFFICIAL_BLURB_AB_V1: native wrapperは公式BlurbLong経路を使用
                            if (role is CustomRoles.Crewmate or CustomRoles.Impostor or CustomRoles.NormalPhantom or CustomRoles.NormalViper or CustomRoles.NormalJudge)
                            {
                                roleHelp = GetString(role.ToString()) + PlayerControl.LocalPlayer.GetRoleInfo(true);
                                Logger.Info($"CMD_ROLE_DESC_PERMANENT Local vanilla fallback Role={role}", "ChatCommand");
                            }
                            else if (role == CustomRoles.Renegade)
                            {
                                // RENEGADE_DYNAMIC_CMD_R1: values come directly from current role options.
                                roleHelp =
                                    GetString(role.ToString()) +
                                    "\n" +
                                    TownOfHostForE.Roles.Neutral.Renegade.GetRoleDescription(true);
                            }
                            else
                            {
                                roleHelp = role.GetRoleInfo()?.Description?.FullFormatHelp ??
                                    // roleInfoがない役職
                                    GetString(role.ToString()) + PlayerControl.LocalPlayer.GetRoleInfo(true);
                            }
                            // CMD_MYROLE_ADDON_DESC_PERMANENT: メイン役職説明の形式を維持して付随役職説明を追記
                            roleHelp += Utils.GetSubRoleInfo(PlayerControl.LocalPlayer);
                            roleHelp = Utils.AppendRoleHelpRichBottomPadding(roleHelp);
                            HudManager.Instance.Chat.AddChat(
                                PlayerControl.LocalPlayer,
                                roleHelp);
                        }
                        break;

                    case "/t":
                    case "/template":
                        canceled = true;
                        if (args.Length > 1) TemplateManager.SendTemplate(args[1]);
                        else HudManager.Instance.Chat.AddChat(PlayerControl.LocalPlayer, $"{GetString("ForExample")}:\n/cmd t test");
                        break;

                    case "/mw":
                    case "/messagewait":
                        canceled = true;
                        if (args.Length > 1 && float.TryParse(args[1], out float sec))
                        {
                            Main.MessageWait.Value = sec;
                            Utils.SendMessage(string.Format(GetString("Message.SetToSeconds"), sec), 0);
                        }
                        else Utils.SendMessage($"{GetString("Message.MessageWaitHelp")}\n{GetString("ForExample")}:\n/cmd mw 3", 0);
                        break;

                    case "/say":
                        canceled = true;
                        if (args.Length > 1)
                            Utils.SendMessage(args.Skip(1).Join(delimiter: " "), title: $"<color=#ff0000>{GetString("MessageFromTheHost")}</color>");
                        break;

                    case "/exile":
                        canceled = true;
                        if (!GameStates.IsInGame) break;
                        if (args.Length < 2 || !int.TryParse(args[1], out int id)) break;
                        Utils.GetPlayerById(id)?.RpcExile();
                        break;

                    case "/kill":
                        canceled = true;
                        if (!GameStates.IsInGame) break;
                        if (args.Length < 2 || !int.TryParse(args[1], out int id2)) break;
                        Utils.GetPlayerById(id2)?.RpcMurderPlayer(Utils.GetPlayerById(id2));
                        break;

                    case "/vo":
                    case "/voice":
                        canceled = true;
                        if (VoiceReader.VoiceReaderMode == null || !VoiceReader.VoiceReaderMode.GetBool())
                            Utils.SendMessage($"現在読上げは停止しています");
                        else if (args.Length > 1 && args[1] == "reset")
                        {
                            VoiceReader.ResetVoiceNo();
                        }
                        else if (args.Length > 1 && args[1] == "random")
                        {
                            VoiceReader.SetRandomVoiceNo();
                        }
                        else if (args.Length > 1 && int.TryParse(args[1], out int voiceNo))
                        {
                            var name = VoiceReader.SetHostVoiceNo(voiceNo);
                            if (name != null && name != "")
                                Utils.SendMessage($"ホスト の読上げを {name} に変更しました");
                        }
                        else
                            Utils.SendMessage(VoiceReader.GetVoiceIdxMsg());
                        break;

                    //case "/mp":
                    //    // Unfortunately server holds this - need to do more trickery
                    //    canceled = true;
                    //    if (AmongUsClient.Instance.CanBan())
                    //    {
                    //        if (!TryParse(text[4..], out LobbyLimit))
                    //        {
                    //            __instance.AddChat(PlayerControl.LocalPlayer, "使い方\n/mp {最大人数}");
                    //        }
                    //        else
                    //        {
                    //            if (LobbyLimit > 15)
                    //            {
                    //                __instance.AddChat(PlayerControl.LocalPlayer, $"プレイヤー最大人数は15人以下にしてください。");
                    //            }
                    //            else if (LobbyLimit != GameManager.Instance.LogicOptions.currentGameOptions.MaxPlayers)
                    //            {
                    //                GameManager.Instance.LogicOptions.currentGameOptions.SetInt(Int32OptionNames.MaxPlayers, LobbyLimit);
                    //                FastDestroyableSingleton<GameStartManager>.Instance.LastPlayerCount = LobbyLimit;
                    //                RoomOption.RpcSyncOption(GameManager.Instance.LogicOptions.currentGameOptions);
                    //                __instance.AddChat(PlayerControl.LocalPlayer, $"ロビーの最大人数を{LobbyLimit}人に変更しました！");
                    //            }
                    //            else
                    //            {
                    //                __instance.AddChat(PlayerControl.LocalPlayer, $"プレイヤー最小人数は {LobbyLimit}です。");
                    //            }
                    //        }
                    //    }
                    //    break;
                    case "/rrs":
                        canceled = true;
                        if (ResetVersionCheckFlag)
                        {
                            StartButtonReset = true;

                            Main.playerVersion.Clear();
                            foreach (var playerId in OtherVersionPlayerId)
                            {
                                //削除対象且つ、ホストのIDとは違う。
                                if (Main.playerVersion.ContainsKey(playerId) && playerId != PlayerControl.LocalPlayer.PlayerId)
                                {
                                    Main.playerVersion.Remove(playerId);
                                }
                            }
                            __instance.AddChat(PlayerControl.LocalPlayer, $"スタートボタンの表示をリセットしました");
                        }
                        break;

                    default:
                        Main.isChatCommand = false;
                        break;
                }
            }
            if (canceled)
            {
                Logger.Info("Command Canceled", "ChatCommand");
                __instance.freeChatField.textArea.Clear();
                __instance.freeChatField.textArea.SetText(cancelVal);
                //__instance.quickChatMenu.ResetGlyphs();
            }
            return !canceled;
        }

        public static void GetRolesInfo(string role)
        {
            // 初回のみ処理
            if (roleCommands == null)
            {
#pragma warning disable IDE0028  // Dictionary初期化の簡素化をしない
                roleCommands = new Dictionary<CustomRoles, string>();

                // GM
                roleCommands.Add(CustomRoles.GM, "ゲームマスター");

                // Impostor役職
                roleCommands.Add((CustomRoles)(-1), $"== {GetString("Impostor")} ==");  // 区切り用
                ConcatCommands(CustomRoleTypes.Impostor);

                // Madmate役職
                roleCommands.Add((CustomRoles)(-2), $"== {GetString("Madmate")} ==");  // 区切り用
                ConcatCommands(CustomRoleTypes.Madmate);
                roleCommands.Add(CustomRoles.SKMadmate, "サイドキックマッドメイト");

                // Crewmate役職
                roleCommands.Add((CustomRoles)(-3), $"== {GetString("Crewmate")} ==");  // 区切り用
                ConcatCommands(CustomRoleTypes.Crewmate);

                // Neutral役職
                roleCommands.Add((CustomRoles)(-4), $"== {GetString("Neutral")} ==");  // 区切り用
                ConcatCommands(CustomRoleTypes.Neutral);

                // Animals役職
                roleCommands.Add((CustomRoles)(-5), $"== {GetString("Animals")} ==");  // 区切り用
                ConcatCommands(CustomRoleTypes.Animals);

                // 属性
                roleCommands.Add((CustomRoles)(-6), $"== {GetString("Addons")} ==");  // 区切り用
                roleCommands.Add(CustomRoles.LastImpostor, "ラストインポスター");
                roleCommands.Add(CustomRoles.Lovers, "ラバーズ");
                roleCommands.Add(CustomRoles.Workhorse, "ワークホース");
                roleCommands.Add(CustomRoles.CompreteCrew, "コンプリートクルー");
                roleCommands.Add(CustomRoles.AddWatch, "ウォッチング");
                roleCommands.Add(CustomRoles.Sunglasses, "サングラス");
                roleCommands.Add(CustomRoles.AddLight, "ライティング");
                roleCommands.Add(CustomRoles.AddSeer, "シーイング");
                roleCommands.Add(CustomRoles.Autopsy, "オートプシー");
                roleCommands.Add(CustomRoles.VIP, "VIP");
                roleCommands.Add(CustomRoles.Clumsy, "クラムシー");
                roleCommands.Add(CustomRoles.Revenger, "リベンジャー");
                roleCommands.Add(CustomRoles.Management, "マネジメント");
                roleCommands.Add(CustomRoles.InfoPoor, "インフォプアー");
                roleCommands.Add(CustomRoles.Sending, "センディング");
                roleCommands.Add(CustomRoles.TieBreaker, "タイブレーカー");
                roleCommands.Add(CustomRoles.NonReport, "ノンレポート");
                roleCommands.Add(CustomRoles.Loyalty, "ロイヤルティ");
                roleCommands.Add(CustomRoles.PlusVote, "プラスボート");
                roleCommands.Add(CustomRoles.Guarding, "ガーディング");
                roleCommands.Add(CustomRoles.AddBait, "ベイティング");
                roleCommands.Add(CustomRoles.Refusing, "リフュージング");
                roleCommands.Add(CustomRoles.Chu2Byo, "中二病");
                roleCommands.Add(CustomRoles.Gambler, "ギャンブラー");

                // HAS
                roleCommands.Add((CustomRoles)(-7), $"== {GetString("HideAndSeek")} ==");  // 区切り用
                roleCommands.Add(CustomRoles.HASFox, "hfo");
                roleCommands.Add(CustomRoles.HASTroll, "htr");

                // HAS
                roleCommands.Add((CustomRoles)(-8), $"== {GetString("SuperBombParty")} ==");  // 区切り用
#pragma warning restore IDE0028
            }

            foreach (var r in roleCommands)
            {
                var roleName = r.Key.ToString();
                var roleShort = r.Value;

                if (String.Compare(role, roleName, true) == 0 || String.Compare(role, roleShort, true) == 0)
                {
                    var roleInfo = r.Key.GetRoleInfo();
                    if (roleInfo != null && roleInfo.Description != null)
                    {
                        Utils.SendRoleHelpMessage(roleInfo.Description);
                    }
                    // RoleInfoがない役職は従来の処理
                    else
                    {
                        Utils.SendMessage(
                            Utils.AppendRoleHelpNetworkBottomPadding(
                                GetString(roleName) + GetString($"{roleName}InfoLong")));
                    }
                    return;
                }
            }
            Utils.SendMessage(GetString("Message.HelpRoleNone"));
        }
        private static void ConcatCommands(CustomRoleTypes roleType)
        {
            var roles = CustomRoleManager.AllRolesInfo.Values.Where(role => role.CustomRoleType == roleType);
            foreach (var role in roles)
            {
                if (role.ChatCommand is null) continue;

                roleCommands[role.RoleName] = role.ChatCommand;
            }
        }

        public static string FixRoleNameInput(string text)
        {
            //日本語には関係ないね
            //text = text.Replace("着", "者").Trim().ToLower();
            return text switch
            {
                "GM" or "gm" or "ゲームマスター" => GetString("GM"),

                //インポスター
                "バウンティハンター" => GetString("BountyHunter"),
                "イビルトラッカー" => GetString("EvilTracker"),
                "イビルウォッチャー" => GetString("EvilWatcher"),
                "花火職人" => GetString("FireWorks"),
                "メアー" => GetString("Mare"),
                "パペッティア" => GetString("Puppeteer"),
                "シリアルキラー" => GetString("SerialKiller"),
                "スナイパー" => GetString("Sniper"),
                "タイムシーフ" => GetString("TimeThief"),
                "吸血鬼" or "ヴァンパイア" => GetString("Vampire"),
                "ウォーロック" => GetString("Warlock"),
                "魔女" or "ウィッチ" => GetString("Witch"),
                "マフィア" => GetString("Mafia"),
                "アンチアドミナー" => GetString("AntiAdminer"),
                "イビル猫又" => GetString("Evilneko"),
                "呪狼" => GetString("CursedWolf"),
                "グリーディア" => GetString("Greedier"),
                "アンビショナー" => GetString("Ambitioner"),
                "スカベンジャー" => GetString("Scavenger"),
                "イビルディバイナー" => GetString("EvilDiviner"),
                "テレパシスターズ" => GetString("Telepathisters"),
                "シェイプキラー" => GetString("ShapeKiller"),
                "爆裂魔" => GetString("SuicideBomber"),
                "シンデレラ" => GetString("Cinderella"),
                "シェイプマスター" => GetString("ShapeMaster"),
                "イビルゲッサー" => GetString("EvilGuesser"),
                "トークティブ" => GetString("Talktive"),
                "テレポーター" => GetString("Teleporter"),
                "ラストインポスター" => GetString("LastImpostor"),
                "シェイプシフター" => GetString("NormalShapeshifter"),

                //マッドメイト
                "マッドメイト" => GetString("Madmate"),
                "マッドガーディアン" => GetString("MadGuardian"),
                "マッドスニッチ" => GetString("MadSnitch"),
                "マッドディクテーター" => GetString("MadDictator"),
                "マッドネイチャコール" => GetString("MadNatureCalls"),
                "マッドブラックアウター" => GetString("MadBrackOuter"),
                "マッドシェリフ" => GetString("MadSheriff"),
                "サイドキックマッドメイト" => GetString("SKMadmate"),
                "うさぎ(M)" or "Mうさぎ" => GetString("IUsagi"),
                "Mシュレ猫" or "Mシュレディンガーの猫" => GetString("MSchrodingerCat"),
                "Mお嬢様" or "マッドお嬢様" => GetString("MOjouSama"),
                "マッドニムロッド" => GetString("MadNimrod"),

                //クルーメイト
                "ベイト" => GetString("Bait"),
                "ディクテーター" => GetString("Dictator"),
                "ドクター" => GetString("Doctor"),
                "ライター" => GetString("Lighter"),
                "メイヤー" => GetString("Mayor"),
                "ナイスウォッチャー" => GetString("NiceWatcher"),
                "サボタージュマスター" => GetString("SabotageMaster"),
                "シーア" => GetString("Seer"),
                "シェリフ" => GetString("Sheriff"),
                "スニッチ" => GetString("Snitch"),
                "スピードブースター" => GetString("SpeedBooster"),
                "トラッパー" => GetString("Trapper"),
                "ハンター" => GetString("Hunter"),
                "タイムマネージャー" => GetString("TimeManager"),
                "パン屋" => GetString("Bakery"),
                "エクスプレス" => GetString("Express"),
                "チェアマン" => GetString("Chairman"),
                "にじいろスター" or "にじいろ" => GetString("Rainbow"),
                "猫又" => GetString("Nekomata"),
                "見送り人" => GetString("SeeingOff"),
                "バカシェリフ" => GetString("SillySheriff"),
                "共鳴者" => GetString("Sympathizer"),
                "ブラインダー" => GetString("Blinder"),
                "メディック" => GetString("Medic"),
                "キャンドルライター" => GetString("CandleLighter"),
                "グラージシェリフ" => GetString("GrudgeSheriff"),
                "占い師" => GetString("FortuneTeller"),
                "霊媒師" => GetString("Psychic"),
                "お嬢様" => GetString("OjouSama"),
                "ネゴシエーター" => GetString("Counselor"),
                "ちいかわ" => GetString("Tiikawa"),
                "ハチワレ" or "はちわれ" => GetString("Hachiware"),
                "うさぎ" => GetString("Usagi"),
                "ナイスゲッサー" => GetString("NiceGuesser"),
                "ニムロッド" => GetString("Nimrod"),
                "エンジニア" => GetString("NormalEngineer"),
                "科学者" => GetString("NormalScientist"),

                //第3陣営
                "アーソニスト" => GetString("Arsonist"),
                "エゴイスト" => GetString("Egoist"),
                "エクスキューショナー" => GetString("Executioner"),
                "ジャッカル" => GetString("Jackal"),
                "ジェスター" => GetString("Jester"),
                "姫" or "純愛者" or "ラバーズ" => GetString("Lovers"),
                "オポチュニスト" or "オポチュニストキラー" => GetString("Opportunist"),
                "テロリスト" => GetString("Terrorist"),
                "シュレディンガーの猫" or "シュレ猫" => GetString("SchrodingerCat"),
                "Eシュレディンガーの猫" or "Eシュレ猫" => GetString("EgoSchrodingerCat"),
                "Oシュレディンガーの猫" or "Oシュレ猫" => GetString("OSchrodingerCat"),
                "Jシュレディンガーの猫" or "Jシュレ猫" => GetString("JSchrodingerCat"),
                "Dシュレディンガーの猫" or "Dシュレ猫" => GetString("DSchrodingerCat"),
                "Gシュレディンガーの猫" or "Gシュレ猫" => GetString("GSchrodingerCat"),
                "Eお嬢様" => GetString("EOjouSama"),
                "Oお嬢様" => GetString("OOjouSama"),
                "Jお嬢様" => GetString("JOjouSama"),
                "Dお嬢様" => GetString("DOjouSama"),
                "アンチコンプリート" => GetString("AntiComplete"),
                "ワーカホリック" => GetString("Workaholic"),
                "ダークハイド" => GetString("DarkHide"),
                "ラブカッター" => GetString("LoveCutter"),
                "弁護士" => GetString("Lawyer"),
                "クライアント" => GetString("JClient"),
                "トトカルチョ" => GetString("Totocalcio"),
                "義賊" => GetString("Gizoku"),
                "ワークホース" => GetString("Workhorse"),
                "決闘者" => GetString("Duelist"),

                //アニマルズ
                "コヨーテ" => GetString("Coyote"),
                "バルチャー" => GetString("Vulture"),
                "アナグマ" => GetString("Badger"),
                "ブラキディオス" => GetString("Braki"),
                "ヒョウ" => GetString("Leopard"),
                _ => text,
            };
        }
        public static bool GetRoleByInputName(string input, out CustomRoles output, bool includeVanilla = false)
        {
            output = new();
            input = Regex.Replace(input, @"[0-9]+", string.Empty);
            input = Regex.Replace(input, @"\s", string.Empty);
            input = Regex.Replace(input, @"[\x01-\x1F,\x7F]", string.Empty);
            input = input.ToLower().Trim().Replace("是", string.Empty);
            if (input == "" || input == string.Empty) return false;
            input = FixRoleNameInput(input).ToLower();
            foreach (CustomRoles role in Enum.GetValues(typeof(CustomRoles)))
            {
                if (!includeVanilla && role.IsVanilla() && role != CustomRoles.GuardianAngel) continue;
                if (input == GuessManager.ChangeNormal2Vanilla(role))
                {
                    output = role;
                    return true;
                }
            }
            return false;
        }
        public static void DispatchRemoteParticipantChat(PlayerControl player, ChatInputParseResult chatInput, out bool canceled)
        {
            var text = chatInput.RoutedText;
            canceled = chatInput.Kind != ChatInputKind.NormalChat;

            if (chatInput.Kind == ChatInputKind.RejectedSlashInput) return;

            var commandKind = chatInput.Kind == ChatInputKind.AcceptedCommand
                ? GetRemoteParticipantCommandKind(text)
                : RemoteParticipantCommandKind.None;
            if (chatInput.Kind == ChatInputKind.AcceptedCommand &&
                commandKind == RemoteParticipantCommandKind.None) return;

            if (chatInput.Kind == ChatInputKind.NormalChat && player != null)
            {
                var tag = !player.Data.IsDead ? "SendChatAlive" : "SendChatDead";
                VoiceReader.Read(text, Palette.GetColorName(player.Data.DefaultOutfit.ColorId), tag);
            }

            if (!AmongUsClient.Instance.AmHost || player == null) return;

            if (chatInput.Kind == ChatInputKind.AcceptedCommand)
                Logger.Info($"CMD_PREFIX_PERMANENT Receive normalized PlayerId={player.PlayerId}", "ChatCommand");

            if (commandKind == RemoteParticipantCommandKind.Guess)
                GuessManager.GuesserMsg(player, text);

            if (commandKind == RemoteParticipantCommandKind.ChatCommand)
            {
                string[] args = text.Split(' ');
                string subArgs = "";

                switch (args[0])
                {
                case "/l":
                case "/lastresult":
                    Utils.ShowLastResult(player.PlayerId);
                    break;

                case "/kl":
                case "/killlog":
                    Utils.ShowKillLog(player.PlayerId);
                    break;

                case "/n":
                case "/now":
                    subArgs = args.Length < 2 ? "" : args[1];
                    switch (subArgs)
                    {
                        case "r":
                        case "roles":
                            Utils.ShowActiveRoles(player.PlayerId);
                            break;

                        default:
                            Utils.ShowActiveSettings(player.PlayerId);
                            break;
                    }
                    break;

                case "/h":
                case "/help":
                    subArgs = args.Length < 2 ? "" : args[1];
                    switch (subArgs)
                    {
                        case "n":
                        case "now":
                            Utils.ShowActiveSettingsHelp(player.PlayerId);
                            break;
                        default:
                            if (args.Length == 1)
                                Utils.ShowParticipantHelp(player.PlayerId);
                            break;
                    }
                    break;

                case "/m":
                case "/myrole":
                    if (GameStates.IsInGame)
                    {
                        var role = player.GetCustomRole();
                        // CMD_ROLE_DESC_PERMANENT: Crewmate・Impostorは既存説明へフォールバック
                        if (role is CustomRoles.Crewmate or CustomRoles.Impostor or CustomRoles.NormalPhantom or CustomRoles.NormalViper or CustomRoles.NormalJudge)
                        {
                            Logger.Info($"CMD_ROLE_DESC_PERMANENT Receive vanilla fallback PlayerId={player.PlayerId} Role={role}", "ChatCommand");
                            Utils.SendMessage(
                                Utils.AppendRoleHelpNetworkBottomPadding(
                                    GetString(role.ToString()) + player.GetRoleInfo(true)),
                                player.PlayerId);
                        }
                        else if (role == CustomRoles.Renegade)
                        {
                            // RENEGADE_DYNAMIC_CMD_R1: Vanilla participant receives current configured values.
                            Utils.SendMessage(
                                Utils.AppendRoleHelpNetworkBottomPadding(
                                    GetString(role.ToString()) +
                                    "\n" +
                                    TownOfHostForE.Roles.Neutral.Renegade.GetRoleDescription(true)),
                                player.PlayerId);
                        }
                        else if (role.GetRoleInfo()?.Description is { } description)
                        {
                            Utils.SendRoleHelpMessage(description, player.PlayerId);
                        }
                        // roleInfoがない役職
                        else
                        {
                            Utils.SendMessage(
                                Utils.AppendRoleHelpNetworkBottomPadding(
                                    GetString(role.ToString()) + player.GetRoleInfo(true)),
                                player.PlayerId);
                        }

                        // CMD_MYROLE_ADDON_DESC_PERMANENT: 非ホスト参加者にも付随役職説明を追加送信
                        var subRoleHelp = Utils.GetSubRoleInfo(player);
                        if (!string.IsNullOrEmpty(subRoleHelp))
                        {
                            Utils.SendMessage(
                                Utils.AppendRoleHelpNetworkBottomPadding(subRoleHelp),
                                player.PlayerId);
                        }
                    }
                    break;

                case "/t":
                case "/template":
                    if (args.Length > 1) TemplateManager.SendTemplate(args[1], player.PlayerId);
                    else Utils.SendMessage($"{GetString("ForExample")}:\n/cmd t test", player.PlayerId);
                    break;

                case "/vo":
                case "/voice":
                    var color = Palette.GetColorName(player.Data.DefaultOutfit.ColorId);
                    if (VoiceReader.VoiceReaderMode == null || !VoiceReader.VoiceReaderMode.GetBool())
                        Utils.SendMessage($"現在読上げは停止しています", player.PlayerId);
                    else if (args.Length > 1 && args[1] == "n")
                        Utils.SendMessage($"{color} の現在の読上げは {VoiceReader.GetVoiceName(color)} です", player.PlayerId);
                    else if (args.Length > 1 && int.TryParse(args[1], out int voiceNo))
                    {
                        var name = VoiceReader.SetVoiceNo(color, voiceNo);
                        if (name != null && name != "")
                        {
                            Utils.SendMessage($"{color} の読上げを {name} に変更しました", player.PlayerId);
                            break;
                        }
                        Utils.SendMessage($"{color} の読上げを変更できませんでした", player.PlayerId);
                    }
                    else
                        Utils.SendMessage(VoiceReader.GetVoiceIdxMsg(), player.PlayerId);
                    break;

                default:
                    break;
                }
            }

            if (!GameStates.IsLobby)
            {
                var cRole = player.GetCustomRole();
                if (!cRole.IsNotAssignRoles())
                    player.GetRoleClass().OnReceiveChat(player, text);
                Ojou.OjouOnReceiveChat(player, text);
                Chu2Byo.Chu2OnReceiveChat(player, text);
                WordLimit.OnReceiveChat(player, text);
            }

            if (commandKind != RemoteParticipantCommandKind.Guess)
                BetWinTeams.BetOnReceiveChat(player, text);
        }

    }
    [HarmonyPatch(typeof(ChatController), nameof(ChatController.Update))]
    class ChatUpdatePatch
    {
        private const int OfficialChatDigitCompatV2MaxAsciiDigits = 5;
        private const int RoleHelpMaxTextElementsV8 = 140;
        private const int RoleHelpMaxNewlinesV8 = 7;
        private const int RoleHelpMaxEstimatedVisualRowsV8 = 8;
        private const int RoleHelpEstimatedVisualColumnsV8 = 32;
        private const int RoleHelpAbilityEmergencyTextElementsV9 = 240;
        private const int RoleHelpAbilityMaxEstimatedVisualRowsV9 = 8;
        private const int RoleHelpAbilityEstimatedVisualColumnsV9 = 46;
        private const int RoleHelpSettingsEmergencyTextElementsV9 = 220;
        private const int RoleHelpSettingsMaxEstimatedVisualRowsV9 = 8;
        private const int RoleHelpSettingsEstimatedVisualColumnsV9 = 40;
        private static readonly Regex OfficialChatDigitCompatV2LineHeightTagRegex = new(
            @"^<\s*(?:/\s*)?line-height(?:\s*=\s*[^<>]*)?\s*>$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex OfficialChatDigitCompatV2Size100OpeningTagRegex = new(
            @"^<\s*size\s*=\s*(?<quote>[""']?)100%\k<quote>\s*>$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex OfficialChatDigitCompatV3Size70OpeningTagRegex = new(
            @"^<\s*size\s*=\s*(?<quote>[""']?)70%\k<quote>\s*>$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex OfficialChatDigitCompatV2SizeOpeningTagRegex = new(
            @"^<\s*size(?:\s*=\s*[^<>]*)?\s*>$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex OfficialChatDigitCompatV2SizeClosingTagRegex = new(
            @"^<\s*/\s*size\s*>$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly char[] OfficialChatDigitCompatV1HexLetters = { 'a', 'b', 'c', 'd', 'e', 'f' };
        private static readonly Regex OfficialChatDigitCompatV1NamedColorTagRegex = new(
            @"^(?<prefix><\s*(?:color|mark)\s*=\s*(?<quote>[""']?)#)(?<hex>(?:[0-9a-f]{8}|[0-9a-f]{6}|[0-9a-f]{4}|[0-9a-f]{3}))(?<suffix>\k<quote>\s*>)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex OfficialChatDigitCompatV1ShortColorTagRegex = new(
            @"^(?<prefix><\s*#)(?<hex>(?:[0-9a-f]{8}|[0-9a-f]{6}|[0-9a-f]{4}|[0-9a-f]{3}))(?<suffix>\s*>)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Dictionary<(int R, int G, int B), string> OfficialChatDigitCompatV1ColorCache = new();
        private static readonly Dictionary<int, string> OfficialChatDigitCompatV1AlphaCache = new();
        private static string[] OfficialChatDigitCompatV1SafeRgbColors = Array.Empty<string>();
        private static readonly (string Name, int R, int G, int B)[] RoleHelpNamedColorsV9 =
        {
            ("black", 0, 0, 0),
            ("blue", 0, 0, 255),
            ("green", 0, 128, 0),
            ("orange", 255, 165, 0),
            ("purple", 128, 0, 128),
            ("red", 255, 0, 0),
            ("white", 255, 255, 255),
            ("yellow", 255, 255, 0),
        };
        private static readonly Dictionary<(byte R, byte G, byte B), string> RoleHelpSafeColorCacheV9 = new();
        private static readonly Regex RoleHelpColorOpeningTagRegexV9 = new(
            @"^<\s*color\s*=\s*(?<quote>[""']?)(?<value>#[0-9a-f]{3,8}|[a-z]+)\k<quote>\s*>$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RoleHelpShortColorOpeningTagRegexV9 = new(
            @"^<\s*(?<value>#[0-9a-f]{3,8})\s*>$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RoleHelpColorClosingTagRegexV9 = new(
            @"^<\s*/\s*color\s*>$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RoleHelpSizeOpeningTagRegexV9 = new(
            @"^<\s*size\s*=\s*[^<>]+\s*>$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RoleHelpSizeClosingTagRegexV9 = new(
            @"^<\s*/\s*size\s*>$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RoleHelpBoldOpeningTagRegexV9 = new(
            @"^<\s*b\s*>$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RoleHelpBoldClosingTagRegexV9 = new(
            @"^<\s*/\s*b\s*>$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly struct OfficialChatDigitCompatV2Result
        {
            private readonly string? networkText;
            public string NetworkText => networkText ?? ".";
            public int OriginalLength { get; }
            public int AfterOutsideDigitLength { get; }
            public int AfterLineHeightLength { get; }
            public int AfterSize100Length { get; }
            public int OriginalAsciiDigitCount { get; }
            public int AfterOutsideDigitAsciiDigitCount { get; }
            public int AfterLineHeightAsciiDigitCount { get; }
            public int AfterSize100AsciiDigitCount { get; }
            public int OutsideTagDigitReplacementCount { get; }
            public int LineHeightTagRemovalCount { get; }
            public int Size100OpeningTagRemovalCount { get; }
            public int Size100ClosingTagRemovalCount { get; }
            public int ColorReplacementCount { get; }
            public bool RecoveryApplied { get; }
            public bool UsedFallbackOriginal { get; }
            public bool UsedSafePlainFallback { get; }
            public string Reason { get; }

            public OfficialChatDigitCompatV2Result(
                string networkText,
                int originalLength,
                int afterOutsideDigitLength,
                int afterLineHeightLength,
                int afterSize100Length,
                int originalAsciiDigitCount,
                int afterOutsideDigitAsciiDigitCount,
                int afterLineHeightAsciiDigitCount,
                int afterSize100AsciiDigitCount,
                int outsideTagDigitReplacementCount,
                int lineHeightTagRemovalCount,
                int size100OpeningTagRemovalCount,
                int size100ClosingTagRemovalCount,
                int colorReplacementCount,
                bool recoveryApplied,
                bool usedFallbackOriginal,
                string reason,
                bool usedSafePlainFallback = false)
            {
                this.networkText = networkText;
                OriginalLength = originalLength;
                AfterOutsideDigitLength = afterOutsideDigitLength;
                AfterLineHeightLength = afterLineHeightLength;
                AfterSize100Length = afterSize100Length;
                OriginalAsciiDigitCount = originalAsciiDigitCount;
                AfterOutsideDigitAsciiDigitCount = afterOutsideDigitAsciiDigitCount;
                AfterLineHeightAsciiDigitCount = afterLineHeightAsciiDigitCount;
                AfterSize100AsciiDigitCount = afterSize100AsciiDigitCount;
                OutsideTagDigitReplacementCount = outsideTagDigitReplacementCount;
                LineHeightTagRemovalCount = lineHeightTagRemovalCount;
                Size100OpeningTagRemovalCount = size100OpeningTagRemovalCount;
                Size100ClosingTagRemovalCount = size100ClosingTagRemovalCount;
                ColorReplacementCount = colorReplacementCount;
                RecoveryApplied = recoveryApplied;
                UsedFallbackOriginal = usedFallbackOriginal;
                UsedSafePlainFallback = usedSafePlainFallback;
                Reason = reason;
            }
        }

        private sealed class RoleHelpNetworkBubbleV9
        {
            public RoleHelpNetworkBubbleV9(
                string plainText,
                string richText,
                string sections,
                string splitReason,
                bool usedPlainFallback,
                string fallbackReason)
            {
                PlainText = plainText;
                RichText = richText;
                Sections = sections;
                SplitReason = splitReason;
                UsedPlainFallback = usedPlainFallback;
                FallbackReason = fallbackReason;
            }

            public string PlainText { get; }
            public string RichText { get; }
            public string Sections { get; }
            public string SplitReason { get; }
            public bool UsedPlainFallback { get; }
            public string FallbackReason { get; }
        }

        private static List<RoleHelpNetworkBubbleV9> BuildRoleHelpNetworkBubblesV9(
            RoleHelpSections sections,
            out string canonicalPlain,
            out string splitReason)
        {
            var roleName = ToRoleHelpNetworkPlainV9(sections.RoleNamePlain).Trim('\n');
            var factionLabel = ToRoleHelpNetworkPlainV9(sections.FactionLabelPlain).Trim('\n');
            var factionName = ToRoleHelpNetworkPlainV9(sections.FactionNamePlain).Trim('\n');
            var ability = ToRoleHelpNetworkPlainV9(sections.AbilityPlain).Trim('\n');
            var settingsHeader = ToRoleHelpNetworkPlainV9(sections.SettingsHeaderPlain).Trim('\n');
            var settingLines = sections.SettingLines
                .Select(line => new
                {
                    Source = line,
                    NamePlain = ToRoleHelpNetworkPlainV9(line.NamePlain).Trim('\n'),
                    ValuePlain = ToRoleHelpNetworkPlainV9(line.ValuePlain).Trim('\n'),
                })
                .ToArray();

            var canonicalBuilder = new StringBuilder();
            canonicalBuilder
                .Append(roleName).Append('\n')
                .Append(factionLabel).Append(factionName).Append('\n')
                .Append(ability);
            if (!ability.EndsWith('\n'))
                canonicalBuilder.Append('\n');
            canonicalBuilder.Append(settingsHeader).Append('\n');
            foreach (var line in settingLines)
                canonicalBuilder.Append(line.NamePlain).Append(": ").Append(line.ValuePlain).Append('\n');
            canonicalPlain = canonicalBuilder.ToString();

            var abilityParts = SplitRoleHelpAbilityMeaningFirstV9(ability, out splitReason);
            var bubbles = new List<RoleHelpNetworkBubbleV9>();
            var roleColor = GetClosestRoleHelpSafeColorV9(sections.RoleNameReadableColor);
            var factionColor = GetClosestRoleHelpSafeColorV9(sections.FactionColor);

            for (var index = 0; index < abilityParts.Count; index++)
            {
                var isFirst = index == 0;
                var isLast = index == abilityParts.Count - 1;
                var plainBuilder = new StringBuilder();
                var richBuilder = new StringBuilder();
                if (isFirst)
                {
                    plainBuilder
                        .Append(roleName).Append('\n')
                        .Append(factionLabel).Append(factionName).Append('\n');
                    richBuilder
                        .Append("<size=130%><color=").Append(roleColor).Append('>')
                        .Append(roleName)
                        .Append("</color></size>\n<size=70%>")
                        .Append(factionLabel).Append("<color=").Append(factionColor).Append('>')
                        .Append(factionName)
                        .Append("</color>\n")
                        .Append(abilityParts[index])
                        .Append("</size>");
                }
                else
                {
                    richBuilder
                        .Append("<size=70%>")
                        .Append(abilityParts[index])
                        .Append("</size>");
                }

                plainBuilder.Append(abilityParts[index]);
                if (isLast)
                {
                    if (!ability.EndsWith('\n'))
                    {
                        plainBuilder.Append('\n');
                        richBuilder.Append('\n');
                    }
                    if (settingLines.Length == 0)
                    {
                        plainBuilder.Append(settingsHeader).Append('\n');
                        richBuilder.Append(settingsHeader).Append('\n');
                    }
                }

                bubbles.Add(FinalizeRoleHelpBubbleV9(
                    plainBuilder.ToString(),
                    richBuilder.ToString(),
                    isFirst ? "Header+Ability" : "AbilityRemainder",
                    splitReason,
                    richBuilder.ToString()));
            }

            if (settingLines.Length > 0)
            {
                var settingGroups = SplitRoleHelpSettingsByLineV9(
                    settingsHeader,
                    settingLines.Select(line => $"{line.NamePlain}: {line.ValuePlain}").ToArray());
                var settingOffset = 0;
                for (var groupIndex = 0; groupIndex < settingGroups.Count; groupIndex++)
                {
                    var groupCount = settingGroups[groupIndex];
                    var includeHeader = groupIndex == 0;
                    var group = sections.SettingLines
                        .Skip(settingOffset)
                        .Take(groupCount)
                        .ToArray();
                    var preferredRich = BuildRoleHelpSettingsRichV9(
                        settingsHeader,
                        group,
                        includeHeader,
                        false);
                    var safeColorRich = BuildRoleHelpSettingsRichV9(
                        settingsHeader,
                        group,
                        includeHeader,
                        true);
                    var plainBuilder = new StringBuilder();
                    if (includeHeader)
                        plainBuilder.Append(settingsHeader).Append('\n');
                    foreach (var line in settingLines.Skip(settingOffset).Take(groupCount))
                        plainBuilder.Append(line.NamePlain).Append(": ").Append(line.ValuePlain).Append('\n');

                    bubbles.Add(FinalizeRoleHelpBubbleV9(
                        plainBuilder.ToString(),
                        preferredRich,
                        includeHeader ? "Settings" : "SettingsRemainder",
                        settingGroups.Count > 1 ? "SettingsOverflow" : "SemanticBoundary",
                        safeColorRich));
                    settingOffset += groupCount;
                }
            }

            return bubbles;
        }

        private static string ToRoleHelpNetworkPlainV9(string text)
        {
            var normalized = (text ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .RemoveHtmlTags();
            return TransformOutsideTagDigitsForOfficialChatDigitCompatV2(
                normalized,
                out _);
        }

        private static List<string> SplitRoleHelpAbilityMeaningFirstV9(
            string ability,
            out string splitReason)
        {
            var parts = new List<string> { ability };
            var estimatedVisualRows = EstimateRoleHelpVisualRowsV9(
                ability,
                RoleHelpAbilityEstimatedVisualColumnsV9);
            if (estimatedVisualRows <= RoleHelpAbilityMaxEstimatedVisualRowsV9 &&
                GetRoleHelpTextElementCountV8(ability) <=
                    RoleHelpAbilityEmergencyTextElementsV9)
            {
                splitReason = "SemanticSections";
                return parts;
            }

            var boundary = FindRoleHelpAbilityBoundaryV9(
                ability,
                (text, index) =>
                    index >= 2 &&
                    text[index - 2] == '\n' &&
                    text[index - 1] == '\n');
            splitReason = "ParagraphBreak";
            if (boundary < 0)
            {
                boundary = FindRoleHelpAbilityBoundaryV9(
                    ability,
                    (text, index) => text[index - 1] == '\n');
                splitReason = "ExplicitNewline";
            }
            if (boundary < 0)
            {
                const string sentenceEnds = "。！？";
                boundary = FindRoleHelpAbilityBoundaryV9(
                    ability,
                    (text, index) => sentenceEnds.Contains(text[index - 1]));
                splitReason = "SentenceEnd";
            }
            if (boundary < 0)
            {
                var starts = StringInfo.ParseCombiningCharacters(ability);
                boundary = starts.Length < 2
                    ? -1
                    : starts[Math.Max(1, starts.Length / 2)];
                splitReason = "EmergencyTextElementMidpoint";
            }

            if (boundary <= 0 || boundary >= ability.Length)
            {
                splitReason = "EmergencyBoundaryUnavailable";
                return parts;
            }

            parts[0] = ability.Substring(0, boundary);
            parts.Add(ability.Substring(boundary));
            return parts;
        }

        private static int FindRoleHelpAbilityBoundaryV9(
            string text,
            Func<string, int, bool> predicate)
        {
            var starts = StringInfo.ParseCombiningCharacters(text);
            var minimum = text.Length * 3 / 10;
            var maximum = text.Length * 7 / 10;
            var midpoint = text.Length / 2;
            var bestBoundary = -1;
            var bestDistance = int.MaxValue;

            for (var index = 1; index <= starts.Length; index++)
            {
                var boundary = index == starts.Length ? text.Length : starts[index];
                if (boundary < minimum || boundary > maximum)
                    continue;
                if (!predicate(text, boundary))
                    continue;
                var distance = Math.Abs(boundary - midpoint);
                if (distance >= bestDistance)
                    continue;
                bestBoundary = boundary;
                bestDistance = distance;
            }

            return bestBoundary;
        }

        private static List<int> SplitRoleHelpSettingsByLineV9(
            string settingsHeader,
            string[] settingLines)
        {
            var allSettings = settingsHeader + "\n" +
                string.Join("\n", settingLines) + "\n";
            if (GetRoleHelpTextElementCountV8(allSettings) <=
                    RoleHelpSettingsEmergencyTextElementsV9 &&
                EstimateRoleHelpVisualRowsV9(
                    allSettings,
                    RoleHelpSettingsEstimatedVisualColumnsV9) <=
                    RoleHelpSettingsMaxEstimatedVisualRowsV9)
            {
                return new List<int> { settingLines.Length };
            }

            var groups = new List<int>();
            var offset = 0;
            while (offset < settingLines.Length)
            {
                var count = 0;
                while (offset + count < settingLines.Length)
                {
                    var candidateCount = count + 1;
                    var candidate = (groups.Count == 0 ? settingsHeader + "\n" : string.Empty) +
                        string.Join(
                            "\n",
                            settingLines.Skip(offset).Take(candidateCount)) +
                        "\n";
                    var fits =
                        GetRoleHelpTextElementCountV8(candidate) <=
                            RoleHelpSettingsEmergencyTextElementsV9 &&
                        EstimateRoleHelpVisualRowsV9(
                            candidate,
                            RoleHelpSettingsEstimatedVisualColumnsV9) <=
                            RoleHelpSettingsMaxEstimatedVisualRowsV9;
                    if (!fits && count > 0)
                        break;
                    count = candidateCount;
                    if (!fits)
                        break;
                }
                groups.Add(count);
                offset += count;
            }

            if (groups.Count > 1 &&
                groups[^1] == 1 &&
                groups[^2] > 2)
            {
                groups[^2]--;
                groups[^1]++;
            }
            return groups;
        }

        private static int EstimateRoleHelpVisualRowsV9(string text, int visualColumns)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            var rows = 1;
            var currentLineVisualUnits = 0;
            var starts = StringInfo.ParseCombiningCharacters(text);
            for (var index = 0; index < starts.Length; index++)
            {
                var start = starts[index];
                var end = index + 1 < starts.Length ? starts[index + 1] : text.Length;
                var textElement = text.Substring(start, end - start);
                if (textElement == "\n")
                {
                    rows++;
                    currentLineVisualUnits = 0;
                    continue;
                }

                var previousWraps = currentLineVisualUnits == 0
                    ? 0
                    : (currentLineVisualUnits - 1) / visualColumns;
                currentLineVisualUnits += EstimateRoleHelpTextElementWidthV8(textElement);
                var currentWraps = currentLineVisualUnits == 0
                    ? 0
                    : (currentLineVisualUnits - 1) / visualColumns;
                rows += currentWraps - previousWraps;
            }
            return rows;
        }

        private static string BuildRoleHelpSettingsRichV9(
            string settingsHeader,
            RoleHelpSettingLine[] lines,
            bool includeHeader,
            bool useSafeColors)
        {
            var builder = new StringBuilder("<size=80%>");
            if (includeHeader)
                builder.Append(settingsHeader).Append('\n');
            foreach (var line in lines)
            {
                builder
                    .Append(ToRoleHelpNetworkPlainV9(line.NamePlain).Trim('\n'))
                    .Append(": ")
                    .Append(SanitizeRoleHelpOptionRichTextV9(line.ValueRich, useSafeColors))
                    .Append('\n');
            }
            return builder.Append("</size>").ToString();
        }

        private static string SanitizeRoleHelpOptionRichTextV9(
            string valueRich,
            bool useSafeColors)
        {
            var normalized = (valueRich ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            var builder = new StringBuilder(normalized.Length);
            var colorStack = new Stack<bool>();
            var boldStack = new Stack<bool>();

            for (var index = 0; index < normalized.Length; index++)
            {
                if (normalized[index] == '<')
                {
                    var tagEnd = normalized.IndexOf('>', index + 1);
                    if (tagEnd >= 0)
                    {
                        var tag = normalized.Substring(index, tagEnd - index + 1);
                        var colorMatch = RoleHelpColorOpeningTagRegexV9.Match(tag);
                        if (!colorMatch.Success)
                            colorMatch = RoleHelpShortColorOpeningTagRegexV9.Match(tag);
                        if (colorMatch.Success)
                        {
                            var colorValue = colorMatch.Groups["value"].Value;
                            var validColor = TryParseRoleHelpColorV9(colorValue, out var color);
                            colorStack.Push(validColor);
                            if (validColor)
                            {
                                var safeValue = useSafeColors
                                    ? GetClosestRoleHelpSafeColorV9(color)
                                    : colorValue.ToLowerInvariant();
                                builder.Append("<color=").Append(safeValue).Append('>');
                            }
                            index = tagEnd;
                            continue;
                        }
                        if (RoleHelpColorClosingTagRegexV9.IsMatch(tag))
                        {
                            if (colorStack.Count > 0 && colorStack.Pop())
                                builder.Append("</color>");
                            index = tagEnd;
                            continue;
                        }
                        if (RoleHelpBoldOpeningTagRegexV9.IsMatch(tag))
                        {
                            boldStack.Push(true);
                            builder.Append("<b>");
                            index = tagEnd;
                            continue;
                        }
                        if (RoleHelpBoldClosingTagRegexV9.IsMatch(tag))
                        {
                            if (boldStack.Count > 0 && boldStack.Pop())
                                builder.Append("</b>");
                            index = tagEnd;
                            continue;
                        }

                        index = tagEnd;
                        continue;
                    }
                }

                var character = normalized[index];
                builder.Append(IsAsciiDigitForChatDigitCompatV2(character)
                    ? (char)('０' + character - '0')
                    : character);
            }

            while (boldStack.Count > 0)
            {
                boldStack.Pop();
                builder.Append("</b>");
            }
            while (colorStack.Count > 0)
            {
                if (colorStack.Pop())
                    builder.Append("</color>");
            }
            return builder.ToString();
        }

        private static bool TryParseRoleHelpColorV9(string value, out Color color)
        {
            foreach (var candidate in RoleHelpNamedColorsV9)
            {
                if (!string.Equals(value, candidate.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
                color = new Color32(
                    (byte)candidate.R,
                    (byte)candidate.G,
                    (byte)candidate.B,
                    byte.MaxValue);
                return true;
            }
            return ColorUtility.TryParseHtmlString(value, out color);
        }

        private static string GetClosestRoleHelpSafeColorV9(Color sourceColor)
        {
            Color32 color = sourceColor;
            var key = (color.r, color.g, color.b);
            if (RoleHelpSafeColorCacheV9.TryGetValue(key, out var cached))
                return cached;

            var bestDistance = int.MaxValue;
            var bestValue = "white";
            foreach (var candidate in RoleHelpNamedColorsV9)
            {
                var distance = OfficialChatDigitCompatV1ColorDistance(
                    color.r,
                    color.g,
                    color.b,
                    candidate.R,
                    candidate.G,
                    candidate.B);
                if (distance >= bestDistance)
                    continue;
                bestDistance = distance;
                bestValue = candidate.Name;
            }

            var safeHex = FindClosestOfficialChatDigitCompatV1SafeRgb(
                color.r,
                color.g,
                color.b);
            var safeHexDistance = OfficialChatDigitCompatV1ColorDistance(
                color.r,
                color.g,
                color.b,
                Convert.ToInt32(safeHex[..2], 16),
                Convert.ToInt32(safeHex.Substring(2, 2), 16),
                Convert.ToInt32(safeHex.Substring(4, 2), 16));
            if (safeHexDistance < bestDistance)
                bestValue = "#" + safeHex;

            RoleHelpSafeColorCacheV9[key] = bestValue;
            if (RoleHelpSafeColorCacheV9.Count > 4096)
                RoleHelpSafeColorCacheV9.Clear();
            return bestValue;
        }

        private static RoleHelpNetworkBubbleV9 FinalizeRoleHelpBubbleV9(
            string plainText,
            string preferredRichText,
            string sections,
            string splitReason,
            string safeColorRichText)
        {
            var richText = preferredRichText;
            var fallbackReason = "";
            var usedPlainFallback = false;
            if (CountAsciiDigitsForChatDigitCompatV2(richText) >
                    OfficialChatDigitCompatV2MaxAsciiDigits ||
                !ValidateRoleHelpRichTextV9(richText, out fallbackReason) ||
                !string.Equals(richText.RemoveHtmlTags(), plainText, StringComparison.Ordinal))
            {
                richText = safeColorRichText;
                fallbackReason = string.IsNullOrEmpty(fallbackReason)
                    ? "PreferredRichDigitOrContentInvariant"
                    : fallbackReason;
            }

            if (!ValidateRoleHelpRichTextV9(richText, out var safeFailure) ||
                CountAsciiDigitsForChatDigitCompatV2(richText) >
                    OfficialChatDigitCompatV2MaxAsciiDigits ||
                !string.Equals(richText.RemoveHtmlTags(), plainText, StringComparison.Ordinal))
            {
                richText = plainText;
                usedPlainFallback = true;
                fallbackReason = string.IsNullOrEmpty(safeFailure)
                    ? "SafeRichDigitOrContentInvariant"
                    : safeFailure;
            }

            return new RoleHelpNetworkBubbleV9(
                plainText,
                richText,
                sections,
                splitReason,
                usedPlainFallback,
                fallbackReason);
        }

        private static bool ValidateRoleHelpRichTextV9(string richText, out string reason)
        {
            var tagStack = new Stack<string>();
            for (var index = 0; index < richText.Length; index++)
            {
                if (richText[index] != '<')
                    continue;
                var tagEnd = richText.IndexOf('>', index + 1);
                if (tagEnd < 0)
                {
                    reason = "DanglingTagOpening";
                    return false;
                }
                var tag = richText.Substring(index, tagEnd - index + 1);
                if (RoleHelpSizeOpeningTagRegexV9.IsMatch(tag))
                    tagStack.Push("size");
                else if (RoleHelpSizeClosingTagRegexV9.IsMatch(tag))
                {
                    if (tagStack.Count == 0 || tagStack.Pop() != "size")
                    {
                        reason = "MismatchedSizeClosingTag";
                        return false;
                    }
                }
                else if (RoleHelpColorOpeningTagRegexV9.IsMatch(tag) ||
                         RoleHelpShortColorOpeningTagRegexV9.IsMatch(tag))
                    tagStack.Push("color");
                else if (RoleHelpColorClosingTagRegexV9.IsMatch(tag))
                {
                    if (tagStack.Count == 0 || tagStack.Pop() != "color")
                    {
                        reason = "MismatchedColorClosingTag";
                        return false;
                    }
                }
                else if (RoleHelpBoldOpeningTagRegexV9.IsMatch(tag))
                    tagStack.Push("b");
                else if (RoleHelpBoldClosingTagRegexV9.IsMatch(tag))
                {
                    if (tagStack.Count == 0 || tagStack.Pop() != "b")
                    {
                        reason = "MismatchedBoldClosingTag";
                        return false;
                    }
                }
                else
                {
                    reason = "UnsupportedTag";
                    return false;
                }
                index = tagEnd;
            }

            if (tagStack.Count != 0)
            {
                reason = $"UnbalancedTags:OpenCount={tagStack.Count}";
                return false;
            }
            if (!HasVisibleTextForOfficialChatSafePlainFallbackV1(richText.RemoveHtmlTags()))
            {
                reason = "NoVisibleText";
                return false;
            }
            reason = "";
            return true;
        }

        private static string PrepareRoleHelpSafeNetworkTextV8(string networkText)
        {
            var normalized = (networkText ?? ".")
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            return normalized.Contains('<')
                ? normalized.RemoveHtmlTags()
                : normalized;
        }

        private static List<string> SplitRoleHelpNetworkTextV8(string safeNetworkText)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(safeNetworkText))
                return chunks;

            var chunkStart = 0;
            while (chunkStart < safeNetworkText.Length)
            {
                var hardEnd = FindRoleHelpHardEndV8(safeNetworkText, chunkStart);
                if (hardEnd >= safeNetworkText.Length)
                {
                    chunks.Add(safeNetworkText.Substring(chunkStart));
                    break;
                }

                var chunkEnd = FindPreferredRoleHelpBoundaryV8(
                    safeNetworkText,
                    chunkStart,
                    hardEnd);
                if (chunkEnd <= chunkStart)
                    chunkEnd = hardEnd;

                chunks.Add(safeNetworkText.Substring(chunkStart, chunkEnd - chunkStart));
                chunkStart = chunkEnd;
            }

            return chunks;
        }

        private static int FindRoleHelpHardEndV8(string text, int chunkStart)
        {
            var textElementStarts = StringInfo.ParseCombiningCharacters(text);
            var elementIndex = Array.BinarySearch(textElementStarts, chunkStart);
            if (elementIndex < 0)
                throw new InvalidOperationException("RoleHelp chunk start is not a text-element boundary.");

            var textElementCount = 0;
            var newlineCount = 0;
            var estimatedVisualRows = 1;
            var currentLineVisualUnits = 0;
            var hardEnd = chunkStart;

            for (var index = elementIndex; index < textElementStarts.Length; index++)
            {
                var elementStart = textElementStarts[index];
                var elementEnd = index + 1 < textElementStarts.Length
                    ? textElementStarts[index + 1]
                    : text.Length;
                var textElement = text.Substring(elementStart, elementEnd - elementStart);
                var isNewline = textElement == "\n";
                var prospectiveTextElementCount = textElementCount + 1;
                var prospectiveNewlineCount = newlineCount + (isNewline ? 1 : 0);
                var prospectiveVisualRows = estimatedVisualRows;
                var prospectiveLineVisualUnits = currentLineVisualUnits;

                if (isNewline)
                {
                    prospectiveVisualRows++;
                    prospectiveLineVisualUnits = 0;
                }
                else
                {
                    var width = EstimateRoleHelpTextElementWidthV8(textElement);
                    var previousWrappedRows = prospectiveLineVisualUnits == 0
                        ? 0
                        : (prospectiveLineVisualUnits - 1) / RoleHelpEstimatedVisualColumnsV8;
                    prospectiveLineVisualUnits += width;
                    var wrappedRows = prospectiveLineVisualUnits == 0
                        ? 0
                        : (prospectiveLineVisualUnits - 1) / RoleHelpEstimatedVisualColumnsV8;
                    prospectiveVisualRows += wrappedRows - previousWrappedRows;
                }

                var exceedsLimit =
                    prospectiveTextElementCount > RoleHelpMaxTextElementsV8 ||
                    prospectiveNewlineCount > RoleHelpMaxNewlinesV8 ||
                    prospectiveVisualRows > RoleHelpMaxEstimatedVisualRowsV8;
                if (exceedsLimit && hardEnd > chunkStart)
                    break;

                textElementCount = prospectiveTextElementCount;
                newlineCount = prospectiveNewlineCount;
                estimatedVisualRows = prospectiveVisualRows;
                currentLineVisualUnits = prospectiveLineVisualUnits;
                hardEnd = elementEnd;

                if (exceedsLimit)
                    break;
            }

            return hardEnd;
        }

        private static int FindPreferredRoleHelpBoundaryV8(
            string text,
            int chunkStart,
            int hardEnd)
        {
            var settingsIndex = text.LastIndexOf(
                "【設定】",
                Math.Max(chunkStart, hardEnd - 1),
                hardEnd - chunkStart,
                StringComparison.Ordinal);
            if (settingsIndex > chunkStart &&
                settingsIndex + "【設定】".Length <= hardEnd &&
                IsRoleHelpTextElementBoundaryV8(text, settingsIndex) &&
                HasVisibleTextForOfficialChatSafePlainFallbackV1(
                    text.Substring(chunkStart, settingsIndex - chunkStart)))
            {
                return settingsIndex;
            }

            var blankLineBoundary = FindLastRoleHelpBoundaryV8(
                text,
                chunkStart,
                hardEnd,
                boundary =>
                    boundary >= 2 &&
                    text[boundary - 2] == '\n' &&
                    text[boundary - 1] == '\n');
            if (blankLineBoundary > chunkStart)
                return blankLineBoundary;

            var newlineBoundary = FindLastRoleHelpBoundaryV8(
                text,
                chunkStart,
                hardEnd,
                boundary => text[boundary - 1] == '\n');
            if (newlineBoundary > chunkStart)
                return newlineBoundary;

            const string japanesePunctuation = "。！？、";
            var punctuationBoundary = FindLastRoleHelpBoundaryV8(
                text,
                chunkStart,
                hardEnd,
                boundary => japanesePunctuation.Contains(text[boundary - 1]));
            if (punctuationBoundary > chunkStart)
                return punctuationBoundary;

            var whitespaceBoundary = FindLastRoleHelpBoundaryV8(
                text,
                chunkStart,
                hardEnd,
                boundary => char.IsWhiteSpace(text[boundary - 1]));
            if (whitespaceBoundary > chunkStart)
                return whitespaceBoundary;

            if (WouldOrphanRoleHelpSettingsHeaderV8(text, chunkStart, hardEnd))
            {
                var firstSettingLineEnd = FindFirstRoleHelpSettingLineEndV8(text, chunkStart);
                if (firstSettingLineEnd > hardEnd &&
                    CountNewlinesForRoleHelpV8(
                        text.Substring(chunkStart, firstSettingLineEnd - chunkStart)) <=
                    RoleHelpMaxNewlinesV8)
                {
                    return firstSettingLineEnd;
                }
            }

            return hardEnd;
        }

        private static int FindLastRoleHelpBoundaryV8(
            string text,
            int chunkStart,
            int hardEnd,
            Func<int, bool> predicate)
        {
            var textElementStarts = StringInfo.ParseCombiningCharacters(text);
            for (var index = textElementStarts.Length; index >= 0; index--)
            {
                var boundary = index == textElementStarts.Length
                    ? text.Length
                    : textElementStarts[index];
                if (boundary > hardEnd)
                    continue;
                if (boundary <= chunkStart)
                    break;
                if (!predicate(boundary))
                    continue;
                if (!HasVisibleTextForOfficialChatSafePlainFallbackV1(
                        text.Substring(chunkStart, boundary - chunkStart)))
                    continue;
                if (WouldOrphanRoleHelpSettingsHeaderV8(text, chunkStart, boundary))
                    continue;
                return boundary;
            }

            return -1;
        }

        private static bool WouldOrphanRoleHelpSettingsHeaderV8(
            string text,
            int chunkStart,
            int chunkEnd)
        {
            var chunk = text.Substring(chunkStart, chunkEnd - chunkStart);
            var settingsIndex = chunk.LastIndexOf("【設定】", StringComparison.Ordinal);
            if (settingsIndex < 0)
                return false;

            var afterSettingsHeader = chunk.Substring(settingsIndex + "【設定】".Length);
            return !HasVisibleTextForOfficialChatSafePlainFallbackV1(afterSettingsHeader);
        }

        private static int FindFirstRoleHelpSettingLineEndV8(string text, int chunkStart)
        {
            var settingsIndex = text.IndexOf("【設定】", chunkStart, StringComparison.Ordinal);
            if (settingsIndex != chunkStart)
                return -1;

            var firstLineStart = text.IndexOf('\n', settingsIndex);
            if (firstLineStart < 0)
                return text.Length;
            firstLineStart++;
            while (firstLineStart < text.Length && text[firstLineStart] == '\n')
                firstLineStart++;
            if (firstLineStart >= text.Length)
                return text.Length;

            var firstLineEnd = text.IndexOf('\n', firstLineStart);
            return firstLineEnd < 0 ? text.Length : firstLineEnd + 1;
        }

        private static bool IsRoleHelpTextElementBoundaryV8(string text, int boundary)
        {
            if (boundary is 0 || boundary == text.Length)
                return true;
            return Array.BinarySearch(
                StringInfo.ParseCombiningCharacters(text),
                boundary) >= 0;
        }

        private static int GetRoleHelpTextElementCountV8(string text) =>
            string.IsNullOrEmpty(text)
                ? 0
                : StringInfo.ParseCombiningCharacters(text).Length;

        private static int CountNewlinesForRoleHelpV8(string text)
        {
            var count = 0;
            foreach (var character in text)
            {
                if (character == '\n')
                    count++;
            }
            return count;
        }

        private static int EstimateRoleHelpTextElementWidthV8(string textElement)
        {
            if (string.IsNullOrEmpty(textElement))
                return 0;

            var codePoint = char.ConvertToUtf32(textElement, 0);
            var category = CharUnicodeInfo.GetUnicodeCategory(textElement, 0);
            var isWide =
                codePoint is >= 0x1100 and <= 0x11FF ||
                codePoint is >= 0x2E80 and <= 0xA4CF ||
                codePoint is >= 0xAC00 and <= 0xD7AF ||
                codePoint is >= 0xF900 and <= 0xFAFF ||
                codePoint is >= 0xFE10 and <= 0xFE6F ||
                codePoint is >= 0xFF01 and <= 0xFF60 ||
                codePoint is >= 0xFFE0 and <= 0xFFE6 ||
                codePoint is >= 0x1F000 and <= 0x1FAFF ||
                category == UnicodeCategory.OtherSymbol ||
                textElement.IndexOf('\u200D') >= 0 ||
                textElement.IndexOf('\uFE0F') >= 0;
            return isWide ? 2 : 1;
        }

        private static string ComputeRoleHelpSha256V8(string text) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)));

        private static bool IsOfficialAmongUsServerForChatDigitCompatV2(out string reason)
        {
            try
            {
                var client = AmongUsClient.Instance;
                if (client == null)
                {
                    reason = "AmongUsClientMissing";
                    return false;
                }

                if (client.NetworkMode != NetworkModes.OnlineGame)
                {
                    reason = "NetworkModeNotOnlineGame";
                    return false;
                }

                if (!ServerManager.InstanceExists)
                {
                    reason = "ServerManagerMissing";
                    return false;
                }

                var serverManager = ServerManager.Instance;
                if (serverManager == null)
                {
                    reason = "ServerManagerMissing";
                    return false;
                }

                var region = serverManager.CurrentRegion;
                if (region == null)
                {
                    reason = "CurrentRegionMissing";
                    return false;
                }

                return IsOfficialAmongUsServerHostForChatDigitCompatV2(region.PingServer, out reason);
            }
            catch (Exception ex)
            {
                reason = $"OfficialServerCheckException:{ex.GetType().Name}";
                return false;
            }
        }

        private static bool IsOfficialAmongUsServerHostForChatDigitCompatV2(string pingServer, out string reason)
        {
            if (string.IsNullOrWhiteSpace(pingServer))
            {
                reason = "PingServerMissing";
                return false;
            }

            try
            {
                var candidate = pingServer.Trim();
                if ((!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) &&
                    (!Uri.TryCreate($"https://{candidate}", UriKind.Absolute, out uri) || string.IsNullOrEmpty(uri.Host)))
                {
                    reason = "PingServerParseFailed";
                    return false;
                }

                var host = uri.IdnHost.TrimEnd('.');
                if (string.IsNullOrEmpty(host) || Uri.CheckHostName(host) != UriHostNameType.Dns)
                {
                    reason = "PingServerHostInvalid";
                    return false;
                }

                if (host.Equals("among.us", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "OfficialExactHost";
                    return true;
                }

                if (host.EndsWith(".among.us", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "OfficialSubdomainHost";
                    return true;
                }

                reason = "HostOutsideOfficialDomain";
                return false;
            }
            catch (Exception ex)
            {
                reason = $"PingServerParseException:{ex.GetType().Name}";
                return false;
            }
        }

        // JOIN_LOBBY_INFO_OFFICIAL_RICHTEXT_SAFE_V317:
        // Utils.csのauthoring色 #FFD966 / #FF0000 はそのまま保持する。
        // official transportでは赤だけを数字0個のnamed redへ変換する。
        // #FFD966はASCII数字3個なので、赤をnamed color化した後は
        // 本文中のTOH4E 2箇所を含めても合計5個で既存上限以内。
        // 追加の本文数字がある言語でも既存digit-compatがoutside digitsだけを
        // 全角化できるため、#FFD966タグそのものを壊さず安全化できる。
        // 全system messageの安全化は無効化しない。
        private static OfficialChatDigitCompatV2Result PrepareJoinLobbyInfoOfficialChatDigitCompatV317(
            string original,
            bool isOfficialServer)
        {
            if (!isOfficialServer)
            {
                return PrepareOfficialChatDigitCompatV2(original, false);
            }

            var transportRichText = (original ?? ".")
                .Replace(
                    "<color=#FF0000>",
                    "<color=red>",
                    StringComparison.OrdinalIgnoreCase);

            return PrepareOfficialChatDigitCompatV2(
                transportRichText,
                true);
        }
        private static OfficialChatDigitCompatV2Result PrepareOfficialChatDigitCompatV2(
            string original,
            bool isOfficialServer)
        {
            var originalLength = original?.Length ?? 0;
            var originalAsciiDigitCount = CountAsciiDigitsForChatDigitCompatV2(original);
            if (!isOfficialServer)
            {
                return new(
                    original!,
                    originalLength,
                    originalLength,
                    originalLength,
                    originalLength,
                    originalAsciiDigitCount,
                    originalAsciiDigitCount,
                    originalAsciiDigitCount,
                    originalAsciiDigitCount,
                    0,
                    0,
                    0,
                    0,
                    0,
                    false,
                    false,
                    "NotOfficialServer");
            }

            if (originalAsciiDigitCount <= OfficialChatDigitCompatV2MaxAsciiDigits)
            {
                return new(
                    original!,
                    originalLength,
                    originalLength,
                    originalLength,
                    originalLength,
                    originalAsciiDigitCount,
                    originalAsciiDigitCount,
                    originalAsciiDigitCount,
                    originalAsciiDigitCount,
                    0,
                    0,
                    0,
                    0,
                    0,
                    false,
                    false,
                    "AsciiDigitCountWithinLimit");
            }

            var afterOutsideDigit = TransformOutsideTagDigitsForOfficialChatDigitCompatV2(
                original!,
                out var outsideTagDigitReplacementCount);
            var afterOutsideDigitAsciiDigitCount =
                CountAsciiDigitsForChatDigitCompatV2(afterOutsideDigit);
            if (afterOutsideDigitAsciiDigitCount <= OfficialChatDigitCompatV2MaxAsciiDigits)
            {
                return new(
                    afterOutsideDigit,
                    originalLength,
                    afterOutsideDigit.Length,
                    afterOutsideDigit.Length,
                    afterOutsideDigit.Length,
                    originalAsciiDigitCount,
                    afterOutsideDigitAsciiDigitCount,
                    afterOutsideDigitAsciiDigitCount,
                    afterOutsideDigitAsciiDigitCount,
                    outsideTagDigitReplacementCount,
                    0,
                    0,
                    0,
                    0,
                    true,
                    false,
                    "OutsideDigitConversionSucceeded");
            }

            var afterLineHeight = RemoveLineHeightTagsForOfficialChatDigitCompatV2(
                afterOutsideDigit,
                out var lineHeightTagRemovalCount);
            var afterLineHeightAsciiDigitCount =
                CountAsciiDigitsForChatDigitCompatV2(afterLineHeight);
            if (afterLineHeightAsciiDigitCount <= OfficialChatDigitCompatV2MaxAsciiDigits)
            {
                return new(
                    afterLineHeight,
                    originalLength,
                    afterOutsideDigit.Length,
                    afterLineHeight.Length,
                    afterLineHeight.Length,
                    originalAsciiDigitCount,
                    afterOutsideDigitAsciiDigitCount,
                    afterLineHeightAsciiDigitCount,
                    afterLineHeightAsciiDigitCount,
                    outsideTagDigitReplacementCount,
                    lineHeightTagRemovalCount,
                    0,
                    0,
                    0,
                    true,
                    false,
                    "LineHeightRecoverySucceeded");
            }

            var afterSize100 = RemoveMatchedSize100TagsForOfficialChatDigitCompatV2(
                afterLineHeight,
                out var size100OpeningTagRemovalCount,
                out var size100ClosingTagRemovalCount,
                out var unsafeSize100Structure);
            var afterSize100AsciiDigitCount =
                CountAsciiDigitsForChatDigitCompatV2(afterSize100);

            if (unsafeSize100Structure)
            {
                return CreateOfficialChatDigitCompatV2SafePlainFallback(
                    original!,
                    originalLength,
                    afterOutsideDigit.Length,
                    afterLineHeight.Length,
                    afterSize100.Length,
                    originalAsciiDigitCount,
                    afterOutsideDigitAsciiDigitCount,
                    afterLineHeightAsciiDigitCount,
                    afterSize100AsciiDigitCount,
                    outsideTagDigitReplacementCount,
                    lineHeightTagRemovalCount,
                    size100OpeningTagRemovalCount,
                    size100ClosingTagRemovalCount,
                    0,
                    "UnsafeSize100Structure");
            }

            if (afterSize100AsciiDigitCount <= OfficialChatDigitCompatV2MaxAsciiDigits)
            {
                return new(
                    afterSize100,
                    originalLength,
                    afterOutsideDigit.Length,
                    afterLineHeight.Length,
                    afterSize100.Length,
                    originalAsciiDigitCount,
                    afterOutsideDigitAsciiDigitCount,
                    afterLineHeightAsciiDigitCount,
                    afterSize100AsciiDigitCount,
                    outsideTagDigitReplacementCount,
                    lineHeightTagRemovalCount,
                    size100OpeningTagRemovalCount,
                    size100ClosingTagRemovalCount,
                    0,
                    true,
                    false,
                    "Size100RecoverySucceeded");
            }

            var afterSize70 = RemoveUnmatchedSize70OpeningTagsForOfficialChatDigitCompatV3(
                afterSize100,
                out var size70OpeningTagRemovalCount);
            var afterSize70AsciiDigitCount =
                CountAsciiDigitsForChatDigitCompatV2(afterSize70);


            var useFallbackOriginal =
                afterSize70AsciiDigitCount > OfficialChatDigitCompatV2MaxAsciiDigits;
            var reason = !useFallbackOriginal
                ? "Size70RecoverySucceeded"
                : size70OpeningTagRemovalCount == 0
                    ? "NoApplicableSize70RecoveryTag"
                    : "RecoveryStillTooHighAfterSize70";

            if (!useFallbackOriginal)
            {
                return new(
                    afterSize70,
                    originalLength,
                    afterOutsideDigit.Length,
                    afterLineHeight.Length,
                    afterSize100.Length,
                    originalAsciiDigitCount,
                    afterOutsideDigitAsciiDigitCount,
                    afterLineHeightAsciiDigitCount,
                    afterSize100AsciiDigitCount,
                    outsideTagDigitReplacementCount,
                    lineHeightTagRemovalCount,
                    size100OpeningTagRemovalCount,
                    size100ClosingTagRemovalCount,
                    0,
                    true,
                    false,
                    reason);
            }

            return CreateOfficialChatDigitCompatV2SafePlainFallback(
                original!,
                originalLength,
                afterOutsideDigit.Length,
                afterLineHeight.Length,
                afterSize100.Length,
                originalAsciiDigitCount,
                afterOutsideDigitAsciiDigitCount,
                afterLineHeightAsciiDigitCount,
                afterSize100AsciiDigitCount,
                outsideTagDigitReplacementCount,
                lineHeightTagRemovalCount,
                size100OpeningTagRemovalCount,
                size100ClosingTagRemovalCount,
                0,
                reason);
        }

        private static OfficialChatDigitCompatV2Result CreateOfficialChatDigitCompatV2SafePlainFallback(
            string original,
            int originalLength,
            int afterOutsideDigitLength,
            int afterLineHeightLength,
            int afterSize100Length,
            int originalAsciiDigitCount,
            int afterOutsideDigitAsciiDigitCount,
            int afterLineHeightAsciiDigitCount,
            int afterSize100AsciiDigitCount,
            int outsideTagDigitReplacementCount,
            int lineHeightTagRemovalCount,
            int size100OpeningTagRemovalCount,
            int size100ClosingTagRemovalCount,
            int colorReplacementCount,
            string triggerReason)
        {
            var normalized = original
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            var safePlain = normalized.RemoveHtmlTags();
            var safePlainBuilder = new StringBuilder(safePlain.Length);
            foreach (var character in safePlain)
            {
                safePlainBuilder.Append(
                    IsAsciiDigitForChatDigitCompatV2(character)
                        ? (char)('０' + character - '0')
                        : character);
            }
            safePlain = safePlainBuilder.ToString();

            var safeAsciiDigitCount = CountAsciiDigitsForChatDigitCompatV2(safePlain);
            var visibleNonempty = HasVisibleTextForOfficialChatSafePlainFallbackV1(safePlain);
            var invariantValid =
                visibleNonempty &&
                safeAsciiDigitCount <= OfficialChatDigitCompatV2MaxAsciiDigits;
            var reason = invariantValid
                ? $"SafePlainFallback:{triggerReason}"
                : $"SafePlainFallbackInvariantFailure:{triggerReason}";

            if (!invariantValid)
            {
                safePlain = ".";
            }

            return new(
                safePlain,
                originalLength,
                afterOutsideDigitLength,
                afterLineHeightLength,
                afterSize100Length,
                originalAsciiDigitCount,
                afterOutsideDigitAsciiDigitCount,
                afterLineHeightAsciiDigitCount,
                afterSize100AsciiDigitCount,
                outsideTagDigitReplacementCount,
                lineHeightTagRemovalCount,
                size100OpeningTagRemovalCount,
                size100ClosingTagRemovalCount,
                colorReplacementCount,
                true,
                false,
                reason,
                true);
        }

        private static bool HasVisibleTextForOfficialChatSafePlainFallbackV1(string text)
        {
            foreach (var character in text)
            {
                if (!char.IsWhiteSpace(character) &&
                    !char.IsControl(character) &&
                    character is not '\u200B' and not '\u200C' and not '\u200D' and
                    not '\u200E' and not '\u200F' and not '\u2060' and not '\uFEFF')
                {
                    return true;
                }
            }

            return false;
        }

        private static string TransformOutsideTagDigitsForOfficialChatDigitCompatV2(
            string text,
            out int outsideTagDigitReplacementCount)
        {
            outsideTagDigitReplacementCount = 0;
            var result = new StringBuilder(text.Length);

            for (var index = 0; index < text.Length; index++)
            {
                var current = text[index];
                if (current == '<')
                {
                    var tagEnd = text.IndexOf('>', index + 1);
                    if (tagEnd >= 0)
                    {
                        result.Append(text, index, tagEnd - index + 1);
                        index = tagEnd;
                        continue;
                    }
                }

                if (IsAsciiDigitForChatDigitCompatV2(current))
                {
                    result.Append((char)('０' + current - '0'));
                    outsideTagDigitReplacementCount++;
                }
                else
                {
                    result.Append(current);
                }
            }

            return result.ToString();
        }

        private static string RemoveLineHeightTagsForOfficialChatDigitCompatV2(
            string text,
            out int lineHeightTagRemovalCount)
        {
            lineHeightTagRemovalCount = 0;
            var result = new StringBuilder(text.Length);

            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '<')
                {
                    var tagEnd = text.IndexOf('>', index + 1);
                    if (tagEnd >= 0)
                    {
                        var tag = text.Substring(index, tagEnd - index + 1);
                        if (OfficialChatDigitCompatV2LineHeightTagRegex.IsMatch(tag))
                        {
                            lineHeightTagRemovalCount++;
                            index = tagEnd;
                            continue;
                        }

                        result.Append(tag);
                        index = tagEnd;
                        continue;
                    }
                }

                result.Append(text[index]);
            }

            return result.ToString();
        }

        private static string RemoveMatchedSize100TagsForOfficialChatDigitCompatV2(
            string text,
            out int size100OpeningTagRemovalCount,
            out int size100ClosingTagRemovalCount,
            out bool unsafeSize100Structure)
        {
            size100OpeningTagRemovalCount = 0;
            size100ClosingTagRemovalCount = 0;
            unsafeSize100Structure = false;
            var sizeStack = new Stack<(int Start, int Length, bool IsSize100)>();
            var removalRanges = new List<(int Start, int Length)>();

            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] != '<')
                    continue;

                var tagEnd = text.IndexOf('>', index + 1);
                if (tagEnd < 0)
                    break;

                var tag = text.Substring(index, tagEnd - index + 1);
                if (OfficialChatDigitCompatV2SizeOpeningTagRegex.IsMatch(tag))
                {
                    sizeStack.Push((
                        index,
                        tag.Length,
                        OfficialChatDigitCompatV2Size100OpeningTagRegex.IsMatch(tag)));
                }
                else if (OfficialChatDigitCompatV2SizeClosingTagRegex.IsMatch(tag) &&
                         sizeStack.Count > 0)
                {
                    var openingTag = sizeStack.Pop();
                    if (openingTag.IsSize100)
                    {
                        removalRanges.Add((openingTag.Start, openingTag.Length));
                        removalRanges.Add((index, tag.Length));
                        size100OpeningTagRemovalCount++;
                        size100ClosingTagRemovalCount++;
                    }
                }

                index = tagEnd;
            }

            unsafeSize100Structure = sizeStack.Any(tag => tag.IsSize100);
            if (unsafeSize100Structure)
            {
                size100OpeningTagRemovalCount = 0;
                size100ClosingTagRemovalCount = 0;
                return text;
            }

            if (removalRanges.Count == 0)
                return text;

            var result = new StringBuilder(text);
            foreach (var range in removalRanges.OrderByDescending(range => range.Start))
                result.Remove(range.Start, range.Length);

            return result.ToString();
        }

        private static string RemoveUnmatchedSize70OpeningTagsForOfficialChatDigitCompatV3(
            string text,
            out int size70OpeningTagRemovalCount)
        {
            size70OpeningTagRemovalCount = 0;
            var sizeStack = new Stack<(int Start, int Length, bool IsSize70)>();

            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] != '<')
                    continue;

                var tagEnd = text.IndexOf('>', index + 1);
                if (tagEnd < 0)
                    break;

                var tag = text.Substring(index, tagEnd - index + 1);
                if (OfficialChatDigitCompatV2SizeOpeningTagRegex.IsMatch(tag))
                {
                    sizeStack.Push((
                        index,
                        tag.Length,
                        OfficialChatDigitCompatV3Size70OpeningTagRegex.IsMatch(tag)));
                }
                else if (OfficialChatDigitCompatV2SizeClosingTagRegex.IsMatch(tag) &&
                         sizeStack.Count > 0)
                {
                    sizeStack.Pop();
                }

                index = tagEnd;
            }

            var removalRanges = sizeStack
                .Where(tag => tag.IsSize70)
                .Select(tag => (tag.Start, tag.Length))
                .ToList();

            if (removalRanges.Count == 0)
                return text;

            var result = new StringBuilder(text);
            foreach (var range in removalRanges.OrderByDescending(range => range.Start))
            {
                result.Remove(range.Start, range.Length);
                size70OpeningTagRemovalCount++;
            }

            return result.ToString();
        }

        private static bool TryReplaceOfficialChatDigitCompatV1ColorTag(string tag, out string safeTag)
        {
            var match = OfficialChatDigitCompatV1NamedColorTagRegex.Match(tag);
            if (!match.Success)
                match = OfficialChatDigitCompatV1ShortColorTagRegex.Match(tag);

            if (!match.Success)
            {
                safeTag = tag;
                return false;
            }

            var hex = match.Groups["hex"].Value;
            if (!hex.Any(IsAsciiDigitForChatDigitCompatV1))
            {
                safeTag = tag;
                return false;
            }

            var hasAlpha = hex.Length is 4 or 8;
            int red;
            int green;
            int blue;
            int alpha = 255;
            if (hex.Length is 3 or 4)
            {
                red = Convert.ToInt32(new string(hex[0], 2), 16);
                green = Convert.ToInt32(new string(hex[1], 2), 16);
                blue = Convert.ToInt32(new string(hex[2], 2), 16);
                if (hasAlpha)
                    alpha = Convert.ToInt32(new string(hex[3], 2), 16);
            }
            else
            {
                red = Convert.ToInt32(hex[..2], 16);
                green = Convert.ToInt32(hex.Substring(2, 2), 16);
                blue = Convert.ToInt32(hex.Substring(4, 2), 16);
                if (hasAlpha)
                    alpha = Convert.ToInt32(hex.Substring(6, 2), 16);
            }

            var safeHex = FindClosestOfficialChatDigitCompatV1SafeRgb(red, green, blue);
            if (hasAlpha)
                safeHex += FindClosestOfficialChatDigitCompatV1SafeAlpha(alpha);

            safeTag = match.Groups["prefix"].Value + safeHex + match.Groups["suffix"].Value;
            return true;
        }

        private static string FindClosestOfficialChatDigitCompatV1SafeRgb(int red, int green, int blue)
        {
            var key = (red, green, blue);
            if (OfficialChatDigitCompatV1ColorCache.TryGetValue(key, out var cached))
                return cached;

            var bestDistance = int.MaxValue;
            var best = "ffffff";
            foreach (var candidate in GenerateOfficialChatDigitCompatV1SafeRgbColors())
            {
                var candidateRed = Convert.ToInt32(candidate[..2], 16);
                var candidateGreen = Convert.ToInt32(candidate.Substring(2, 2), 16);
                var candidateBlue = Convert.ToInt32(candidate.Substring(4, 2), 16);
                var distance = OfficialChatDigitCompatV1ColorDistance(
                    red,
                    green,
                    blue,
                    candidateRed,
                    candidateGreen,
                    candidateBlue);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            OfficialChatDigitCompatV1ColorCache[key] = best;
            if (OfficialChatDigitCompatV1ColorCache.Count > 4096)
                OfficialChatDigitCompatV1ColorCache.Clear();
            return best;
        }

        private static string FindClosestOfficialChatDigitCompatV1SafeAlpha(int alpha)
        {
            if (OfficialChatDigitCompatV1AlphaCache.TryGetValue(alpha, out var cached))
                return cached;

            var bestDistance = int.MaxValue;
            var best = "ff";
            foreach (var high in OfficialChatDigitCompatV1HexLetters)
            {
                foreach (var low in OfficialChatDigitCompatV1HexLetters)
                {
                    var candidate = $"{high}{low}";
                    var candidateAlpha = Convert.ToInt32(candidate, 16);
                    var distance = (alpha - candidateAlpha) * (alpha - candidateAlpha);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                    }
                }
            }

            OfficialChatDigitCompatV1AlphaCache[alpha] = best;
            return best;
        }

        private static string[] GenerateOfficialChatDigitCompatV1SafeRgbColors()
        {
            if (OfficialChatDigitCompatV1SafeRgbColors.Length > 0)
                return OfficialChatDigitCompatV1SafeRgbColors;

            OfficialChatDigitCompatV1SafeRgbColors = new string[46656];
            var index = 0;
            foreach (var redHigh in OfficialChatDigitCompatV1HexLetters)
                foreach (var redLow in OfficialChatDigitCompatV1HexLetters)
                    foreach (var greenHigh in OfficialChatDigitCompatV1HexLetters)
                        foreach (var greenLow in OfficialChatDigitCompatV1HexLetters)
                            foreach (var blueHigh in OfficialChatDigitCompatV1HexLetters)
                                foreach (var blueLow in OfficialChatDigitCompatV1HexLetters)
                                    OfficialChatDigitCompatV1SafeRgbColors[index++] =
                                        $"{redHigh}{redLow}{greenHigh}{greenLow}{blueHigh}{blueLow}";

            return OfficialChatDigitCompatV1SafeRgbColors;
        }

        private static int OfficialChatDigitCompatV1ColorDistance(
            int red1,
            int green1,
            int blue1,
            int red2,
            int green2,
            int blue2)
        {
            var redDifference = red1 - red2;
            var greenDifference = green1 - green2;
            var blueDifference = blue1 - blue2;
            return redDifference * redDifference +
                   greenDifference * greenDifference +
                   blueDifference * blueDifference;
        }

        private static int CountAsciiDigitsForChatDigitCompatV2(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            var count = 0;
            foreach (var character in text)
            {
                if (IsAsciiDigitForChatDigitCompatV2(character))
                    count++;
            }
            return count;
        }

        private static bool IsAsciiDigitForChatDigitCompatV2(char character) =>
            character is >= '0' and <= '9';

        private static int CountAsciiDigitsForChatDigitCompatV1(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            var count = 0;
            foreach (var character in text)
            {
                if (IsAsciiDigitForChatDigitCompatV1(character))
                    count++;
            }
            return count;
        }

        private static bool IsAsciiDigitForChatDigitCompatV1(char character) =>
            character is >= '0' and <= '9';

        private static void LogOfficialChatDigitCompatV2(
            string payloadType,
            byte sendTo,
            bool isOnlineGame,
            bool isOfficialServer,
            string officialServerReason,
            OfficialChatDigitCompatV2Result result)
        {
            var networkText = result.NetworkText;
            var safeLength = networkText.Length;
            var safeAsciiDigitCount =
                CountAsciiDigitsForChatDigitCompatV2(networkText);
            var visibleNonempty =
                HasVisibleTextForOfficialChatSafePlainFallbackV1(networkText);
            var diagnostic =
                $"OFFICIAL_CHAT_DIGIT_COMPAT_PERMANENT_RECOVERY PayloadType={payloadType} sendTo={sendTo} " +
                $"IsOnlineGame={isOnlineGame} " +
                $"IsOfficialServer={isOfficialServer} OfficialServerReason={officialServerReason} " +
                $"OriginalAsciiDigitCount={result.OriginalAsciiDigitCount} " +
                $"AfterOutsideDigitAsciiDigitCount={result.AfterOutsideDigitAsciiDigitCount} " +
                $"AfterLineHeightAsciiDigitCount={result.AfterLineHeightAsciiDigitCount} " +
                $"AfterSize100AsciiDigitCount={result.AfterSize100AsciiDigitCount} " +
                $"OutsideTagDigitReplacementCount={result.OutsideTagDigitReplacementCount} " +
                $"LineHeightTagRemovalCount={result.LineHeightTagRemovalCount} " +
                $"Size100OpeningTagRemovalCount={result.Size100OpeningTagRemovalCount} " +
                $"Size100ClosingTagRemovalCount={result.Size100ClosingTagRemovalCount} " +
                $"ColorReplacementCount={result.ColorReplacementCount} " +
                $"OriginalLength={result.OriginalLength} " +
                $"AfterOutsideDigitLength={result.AfterOutsideDigitLength} " +
                $"AfterLineHeightLength={result.AfterLineHeightLength} " +
                $"AfterSize100Length={result.AfterSize100Length} " +
                $"SafeLength={safeLength} SafeAsciiDigitCount={safeAsciiDigitCount} " +
                $"UsedSafePlainFallback={result.UsedSafePlainFallback} " +
                $"VisibleNonempty={visibleNonempty} " +
                $"RecoveryApplied={result.RecoveryApplied} " +
                $"UsedFallbackOriginal={result.UsedFallbackOriginal} Reason={result.Reason}";

            if (result.UsedFallbackOriginal ||
                result.Reason.StartsWith(
                    "SafePlainFallbackInvariantFailure:",
                    StringComparison.Ordinal))
                Logger.Error(diagnostic, "ChatCommand");
        }

        public static bool DoBlockChat = false;
        public static void Postfix(ChatController __instance)
        {
            //if (!AmongUsClient.Instance.AmHost || Main.MessagesToSend.Count < 1 || (Main.MessagesToSend[0].Item2 == byte.MaxValue && Main.MessageWait.Value > __instance.TimeSinceLastMessage)) return;
            if (!AmongUsClient.Instance.AmHost) return;
            if (DoBlockChat) return;
            string msg;
            byte sendTo;
            string title;
            SystemMessageKind messageKind;
            RoleHelpSections? roleHelpSections;

            var player = Main.AllAlivePlayerControls.OrderBy(x => x.PlayerId).FirstOrDefault();

            if (!(Main.MessagesToSend.Count < 1 || (Main.MessagesToSend[0].Item2 == byte.MaxValue && Main.MessageWait.Value > __instance.timeSinceLastMessage)))
            {
                if (player == null) return;
                (msg, sendTo, title, messageKind, roleHelpSections) = Main.MessagesToSend[0];
                Main.MessagesToSend.RemoveAt(0);
            }
            else if (!(Main.SuffixMessagesToSend.Count < 1 || (Main.SuffixMessagesToSend[0].Item2 == byte.MaxValue && Main.MessageWait.Value > __instance.timeSinceLastMessage)))
            {
                (msg, sendTo, title, player) = Main.SuffixMessagesToSend[0];
                Main.SuffixMessagesToSend.RemoveAt(0);
                messageKind = SystemMessageKind.NormalMessage;
                roleHelpSections = null;
            }
            else
            {
                return;
            }
            int clientId = -1;
            if (sendTo != byte.MaxValue && !Utils.GetPlayerById(sendTo).TryGetClientId(out clientId)) return;
            var name = player.Data.PlayerName;
            if (clientId == -1)
            {
                player.SetName(title);
                DestroyableSingleton<HudManager>.Instance.Chat.AddChat(player, msg);
                player.SetName(name);
            }
            var isOnlineGame = AmongUsClient.Instance.NetworkMode == NetworkModes.OnlineGame;
            var isOfficialServer = IsOfficialAmongUsServerForChatDigitCompatV2(out var officialServerReason);
            var networkTitle = PrepareOfficialChatDigitCompatV2(title, isOfficialServer);
            var networkBody =
                messageKind == SystemMessageKind.JoinLobbyInfo
                    ? PrepareJoinLobbyInfoOfficialChatDigitCompatV317(
                        msg,
                        isOfficialServer)
                    : PrepareOfficialChatDigitCompatV2(
                        msg,
                        isOfficialServer);
            IReadOnlyList<string> networkBodies = new[] { networkBody.NetworkText };
            var roleHelpSafeHash = "";
            var roleHelpConcatHash = "";
            var roleHelpConcatEquals = true;
            var hasSemanticRoleHelp =
                messageKind == SystemMessageKind.RoleHelp &&
                roleHelpSections != null;
            if (hasSemanticRoleHelp)
            {
                var roleHelpBubbles = BuildRoleHelpNetworkBubblesV9(
                    roleHelpSections!,
                    out var canonicalRoleHelpPlain,
                    out _);
                networkBodies = roleHelpBubbles
                    .Select(bubble => bubble.RichText)
                    .ToArray();
                var concatenatedRoleHelpPlain = string.Concat(
                    networkBodies.Select(body => body.RemoveHtmlTags()));
                roleHelpSafeHash = ComputeRoleHelpSha256V8(canonicalRoleHelpPlain);
                roleHelpConcatHash = ComputeRoleHelpSha256V8(concatenatedRoleHelpPlain);
                roleHelpConcatEquals = string.Equals(
                    concatenatedRoleHelpPlain,
                    canonicalRoleHelpPlain,
                    StringComparison.Ordinal);
                var bubblesValid =
                    roleHelpBubbles.Count > 0 &&
                    roleHelpConcatEquals &&
                    roleHelpSafeHash == roleHelpConcatHash &&
                    roleHelpBubbles.All(bubble =>
                        ValidateRoleHelpRichTextV9(bubble.RichText, out _) &&
                        HasVisibleTextForOfficialChatSafePlainFallbackV1(
                            bubble.RichText.RemoveHtmlTags()) &&
                        CountAsciiDigitsForChatDigitCompatV2(bubble.RichText) <=
                            OfficialChatDigitCompatV2MaxAsciiDigits);

                if (!bubblesValid)
                {
                    Logger.Error(
                        $"ROLE_HELP_SEMANTIC_PERMANENT_FALLBACK sendTo={sendTo} " +
                        $"TargetClientId={clientId} BubbleCount={roleHelpBubbles.Count} " +
                        $"PlainConcatEquals={roleHelpConcatEquals} SafeHash={roleHelpSafeHash} " +
                        $"ConcatHash={roleHelpConcatHash} PerBubblePlainFallback=True",
                        "ChatCommand");
                    networkBodies = roleHelpBubbles
                        .Select(bubble => bubble.PlainText)
                        .ToArray();
                    concatenatedRoleHelpPlain = string.Concat(networkBodies);
                    roleHelpConcatHash = ComputeRoleHelpSha256V8(concatenatedRoleHelpPlain);
                    roleHelpConcatEquals = string.Equals(
                        concatenatedRoleHelpPlain,
                        canonicalRoleHelpPlain,
                        StringComparison.Ordinal);
                }


            }
            else if (messageKind == SystemMessageKind.RoleHelp)
            {
                var safeRoleHelpBody = PrepareRoleHelpSafeNetworkTextV8(networkBody.NetworkText);
                var roleHelpChunks = SplitRoleHelpNetworkTextV8(safeRoleHelpBody);
                var concatenatedRoleHelp = string.Concat(roleHelpChunks);
                roleHelpSafeHash = ComputeRoleHelpSha256V8(safeRoleHelpBody);
                roleHelpConcatHash = ComputeRoleHelpSha256V8(concatenatedRoleHelp);
                roleHelpConcatEquals = string.Equals(
                    concatenatedRoleHelp,
                    safeRoleHelpBody,
                    StringComparison.Ordinal);
                var chunksValid =
                    roleHelpChunks.Count > 0 &&
                    roleHelpConcatEquals &&
                    roleHelpSafeHash == roleHelpConcatHash &&
                    roleHelpChunks.All(chunk =>
                        HasVisibleTextForOfficialChatSafePlainFallbackV1(chunk) &&
                        CountAsciiDigitsForChatDigitCompatV2(chunk) <=
                            OfficialChatDigitCompatV2MaxAsciiDigits);

                if (!chunksValid)
                {
                    Logger.Error(
                        $"ROLE_HELP_SPLIT_PERMANENT_FALLBACK sendTo={sendTo} TargetClientId={clientId} " +
                        $"SafeLength={safeRoleHelpBody.Length} ChunkCount={roleHelpChunks.Count} " +
                        $"ConcatEquals={roleHelpConcatEquals} SafeHash={roleHelpSafeHash} " +
                        $"ConcatHash={roleHelpConcatHash} FallbackReason={networkBody.Reason}",
                        "ChatCommand");
                    roleHelpChunks = new List<string> { safeRoleHelpBody };
                    roleHelpConcatEquals = true;
                    roleHelpConcatHash = roleHelpSafeHash;
                }

                networkBodies = roleHelpChunks;

            }

            // ROLE_HELP_BOTTOM_PADDING_PERMANENT: 役職説明の最後の吹き出しへ見えない描画行を追加
            if (messageKind == SystemMessageKind.RoleHelp && networkBodies.Count > 0)
            {
                var paddedRoleHelpBodies = networkBodies.ToArray();
                paddedRoleHelpBodies[paddedRoleHelpBodies.Length - 1] =
                    Utils.AppendRoleHelpNetworkBottomPadding(
                        paddedRoleHelpBodies[paddedRoleHelpBodies.Length - 1]);
                networkBodies = paddedRoleHelpBodies;
            }

            var restoreName = player.Data.PlayerName;
            LogOfficialChatDigitCompatV2(
                "SetNameTitle",
                sendTo,
                isOnlineGame,
                isOfficialServer,
                officialServerReason,
                networkTitle);
            if (!hasSemanticRoleHelp)
            {
                LogOfficialChatDigitCompatV2(
                    "SendChatBody",
                    sendTo,
                    isOnlineGame,
                    isOfficialServer,
                    officialServerReason,
                    networkBody);
            }
            var writer = CustomRpcSender.Create("MessagesToSend", SendOption.Reliable);
            writer.StartMessage(clientId);
            writer.StartRpc(player.NetId, (byte)RpcCalls.SetName)
                .Write(player.Data.NetId)
                .Write(networkTitle.NetworkText)
                .EndRpc();
            foreach (var networkBodyChunk in networkBodies)
            {
                writer.StartRpc(player.NetId, (byte)RpcCalls.SendChat)
                    .Write(networkBodyChunk)
                    .EndRpc();
            }
            writer.StartRpc(player.NetId, (byte)RpcCalls.SetName)
                .Write(player.Data.NetId)
                .Write(restoreName)
                .EndRpc();
            writer.EndMessage();
            writer.SendMessage();

            // VANILLA_HOST_MOD_VERSION_DISPLAY_CHAT_RESTORE_PERMANENT:
            // TOHK同様、ホストがシステムメッセージ送信時に一時SetName→通常名へ復元した後、
            // ロビーのバニラ端末だけへMOD名+Version付きホスト名をforce再適用する。
            if (GameStates.IsLobby && player == PlayerControl.LocalPlayer)
            {
                _ = new LateTask(
                    () => Utils.ReapplyVanillaHostModVersionDisplayAfterSystemMessage(),
                    0.24f,
                    "VanillaHostModVersionDisplayAfterSystemMessage");
            }

            __instance.timeSinceLastMessage = 0f;
        }
    }

    [HarmonyPatch(typeof(ChatController), nameof(ChatController.AddChat))]
    class AddChatPatch
    {
        public static void Postfix(string chatText)
        {
            switch (chatText)
            {
                default:
                    break;
            }
            if (!AmongUsClient.Instance.AmHost) return;
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSendChat))]
    class RpcSendChatPatch
    {
        public static bool Prefix(PlayerControl __instance, string chatText, ref bool __result)
        {
            if (string.IsNullOrWhiteSpace(chatText))
            {
                __result = false;
                return false;
            }
            int return_count = PlayerControl.LocalPlayer.name.Count(x => x == '\n');
            chatText = new StringBuilder(chatText).Insert(0, "\n", return_count).ToString();
            if (AmongUsClient.Instance.AmClient && DestroyableSingleton<HudManager>.Instance)
                DestroyableSingleton<HudManager>.Instance.Chat.AddChat(__instance, chatText);
            if (chatText.Contains("who", StringComparison.OrdinalIgnoreCase))
                DestroyableSingleton<UnityTelemetry>.Instance.SendWho();
            MessageWriter messageWriter = AmongUsClient.Instance.StartRpcImmediately(__instance.NetId, (byte)RpcCalls.SendChat, SendOption.Reliable);
            messageWriter.Write(chatText);
            AmongUsClient.Instance.FinishRpcImmediately(messageWriter);
            __result = true;
            return false;
        }
    }
}
