#nullable disable

using System;
using System.Collections.Generic;
using Hazel;
using AmongUs.GameOptions;

using TownOfHostForE.Attributes;
using TownOfHostForE.Modules;
using TownOfHostForE.Roles.Core;

using static TownOfHostForE.Translator;

namespace TownOfHostForE.Roles.Crewmate;

// SCALES_PORT_PERMANENT: TOHKの天秤をTOH4E方式へ限定移植
public sealed class Scales : RoleBase
{
    public static readonly SimpleRoleInfo RoleInfo =
        SimpleRoleInfo.Create(
            typeof(Scales),
            player => new Scales(player),
            CustomRoles.Scales,
            () => RoleTypes.Crewmate,
            CustomRoleTypes.Crewmate,
            41500,
            SetupOptionItem,
            "bal",
            "#cff100"
        );

    private static OptionItem OptionMeetingTime;
    private static OptionItem OptionCanUseAllAlive;
    public static OptionItem OptionCanUseMeetingAbility;
    // SCALES_ABILITY_USE_COUNT_PERMANENT: 天秤能力の使用可能回数 1～15 / default 1
    private static OptionItem OptionAbilityUseCount;

    private enum OptionName
    {
        ScalesMeetingTime,
        ScalesCanUseAllAlive,
        ScalesCanUseMeetingAbility,
        ScalesAbilityUseCount,
    }

    private enum ScalesPhase : byte
    {
        None,
        Pending,
        Active,
        Finishing,
    }

    private static readonly HashSet<Scales> Instances = new(15);
    private static ScalesPhase Phase;
    private static byte OwnerId = byte.MaxValue;
    private static byte Target1Id = byte.MaxValue;
    private static byte Target2Id = byte.MaxValue;

    // SCALES_TIE_EXILE_DISPLAY_PERMANENT:
    // 同票2人追放の実死亡処理は変更せず、バニラ追放画面だけに表示用carrierを使用する。
    private static bool TieExileDisplayActivePermanent;
    private static byte TieExileDisplayCarrierIdPermanent = byte.MaxValue;
    private static string TieExileDisplayTextPermanent = string.Empty;
    private static string TieExileDisplayOriginalCarrierNamePermanent = string.Empty;
    private static bool TieExileDisplayNameAppliedPermanent;

    private byte selectedTarget1 = byte.MaxValue;
    private byte selectedTarget2 = byte.MaxValue;
    private bool selecting;
    // SCALES_ABILITY_USE_COUNT_PERMANENT: 0から数え、2人目確定時だけ1回消費する。
    private int usedCount;
    private bool IsAbilityUseLimitReached =>
        usedCount >= OptionAbilityUseCount.GetInt();
    private bool pendingStart;

    public Scales(PlayerControl player)
        : base(RoleInfo, player)
    {
        Instances.Add(this);
        if (Instances.Count == 1)
        {
            CustomRoleManager.MarkOthers.Add(GetMarkOthers);
        }
    }

    [GameModuleInitializer]
    public static void Init()
    {
        Instances.Clear();
        ResetGlobalState();
    }

    private static void SetupOptionItem()
    {
        OptionMeetingTime = IntegerOptionItem.Create(
                RoleInfo,
                10,
                OptionName.ScalesMeetingTime,
                new(15, 120, 1),
                30,
                false)
            .SetValueFormat(OptionFormat.Seconds);

        OptionCanUseAllAlive = BooleanOptionItem.Create(
            RoleInfo,
            11,
            OptionName.ScalesCanUseAllAlive,
            false,
            false);

        OptionCanUseMeetingAbility = BooleanOptionItem.Create(
            RoleInfo,
            12,
            OptionName.ScalesCanUseMeetingAbility,
            false,
            false);

        // SCALES_ABILITY_USE_COUNT_PERMANENT: TOH4E追加拡張。デフォルト1は従来挙動と同一。
        OptionAbilityUseCount = IntegerOptionItem.Create(
                RoleInfo,
                13,
                OptionName.ScalesAbilityUseCount,
                new(1, 15, 1),
                1,
                false)
            .SetValueFormat(OptionFormat.Times);
    }

    public override void OnDestroy()
    {
        Instances.Remove(this);
        if (Instances.Count == 0)
        {
            CustomRoleManager.MarkOthers.Remove(GetMarkOthers);
        }

        if (OwnerId == Player.PlayerId)
        {
            RestoreTieExileDisplayNamesPermanent();
            // SCALES_MULTI_SELECTION_RESET_PERMANENT: 所有者消滅時も他の天秤の途中選択を残さない
            ClearLocalSelectionStates();
            ResetGlobalState();
            SendState();
        }
    }

    public override bool CheckVoteAsVoter(PlayerControl votedFor)
    {
        if (!AmongUsClient.Instance.AmHost || !Player.IsAlive())
        {
            return true;
        }

        if (Phase == ScalesPhase.Pending)
        {
            return OwnerId != Player.PlayerId;
        }

        if (Phase == ScalesPhase.Active || Phase == ScalesPhase.Finishing)
        {
            if (OwnerId != Player.PlayerId)
            {
                return true;
            }

            return votedFor != null && IsTarget(votedFor.PlayerId);
        }

        if (IsAbilityUseLimitReached)
        {
            return true;
        }

        if (votedFor == null)
        {
            if (!selecting)
            {
                return true;
            }

            CancelSelection();
            return false;
        }

        if (!selecting)
        {
            if (votedFor.PlayerId != Player.PlayerId)
            {
                return true;
            }

            if (OptionCanUseAllAlive.GetBool() && !GameStates.AlreadyDied)
            {
                Utils.SendMessage(
                    GetString("ScalesAllAliveBlocked"),
                    Player.PlayerId,
                    Utils.ColorString(RoleInfo.RoleColor, GetString("Scales")));
                return false;
            }

            selecting = true;
            selectedTarget1 = byte.MaxValue;
            selectedTarget2 = byte.MaxValue;
            Utils.SendMessage(
                GetString("ScalesSelectionStarted"),
                Player.PlayerId,
                Utils.ColorString(RoleInfo.RoleColor, GetString("Scales")));
            return false;
        }

        if (votedFor.PlayerId == Player.PlayerId)
        {
            CancelSelection();
            return false;
        }

        if (!IsSelectableTarget(votedFor))
        {
            Utils.SendMessage(
                GetString("ScalesInvalidTarget"),
                Player.PlayerId,
                Utils.ColorString(RoleInfo.RoleColor, GetString("Scales")));
            return false;
        }

        if (selectedTarget1 == byte.MaxValue)
        {
            selectedTarget1 = votedFor.PlayerId;
            Utils.SendMessage(
                string.Format(
                    GetString("ScalesFirstTarget"),
                    votedFor.GetRealName()),
                Player.PlayerId,
                Utils.ColorString(RoleInfo.RoleColor, GetString("Scales")));
            return false;
        }

        if (selectedTarget1 == votedFor.PlayerId)
        {
            Utils.SendMessage(
                GetString("ScalesDuplicateTarget"),
                Player.PlayerId,
                Utils.ColorString(RoleInfo.RoleColor, GetString("Scales")));
            return false;
        }

        selectedTarget2 = votedFor.PlayerId;
        // SCALES_ABILITY_USE_COUNT_PERMANENT: 2人目を確定した時点でのみ1回消費する。
        usedCount++;
        Logger.Info(
            $"Ability use consumed | Player={Player.PlayerId} | Used={usedCount}/{OptionAbilityUseCount.GetInt()}",
            "ScalesAbilityUseCountPermanent");
        selecting = false;
        pendingStart = true;

        // SCALES_MULTI_SELECTION_RESET_PERMANENT: 他の天秤の未完了選択だけを破棄する
        ClearLocalSelectionStates(Player.PlayerId);

        OwnerId = Player.PlayerId;
        Target1Id = selectedTarget1;
        Target2Id = selectedTarget2;
        Phase = ScalesPhase.Pending;
        SendState();

        Utils.SendMessage(
            string.Format(
                GetString("ScalesTargetsSelected"),
                GetPlayerName(Target1Id),
                GetPlayerName(Target2Id)),
            title: Utils.ColorString(RoleInfo.RoleColor, GetString("Scales")));

        ExileControllerWrapUpPatch.AntiBlackout_LastExiled = null;
        _ = new LateTask(
            () =>
            {
                if (Phase == ScalesPhase.Pending
                    && OwnerId == Player.PlayerId
                    && MeetingVoteManager.Instance != null)
                {
                    MeetingVoteManager.Instance.ClearAndExile(
                        Player.PlayerId,
                        MeetingVoteManager.Skip);
                }
            },
            0.3f,
            "ScalesEndSelectionMeeting");

        return false;
    }

    public override void OnStartMeeting()
    {
        if (!AmongUsClient.Instance.AmHost
            || Phase != ScalesPhase.Active
            || OwnerId != Player.PlayerId)
        {
            return;
        }

        Utils.SendMessage(
            string.Format(
                GetString("ScalesMeetingInfo"),
                GetPlayerName(Target1Id),
                GetPlayerName(Target2Id)),
            title: Utils.ColorString(
                RoleInfo.RoleColor,
                GetString("ScalesMeetingTitle")));
    }

    public override void AfterMeetingTasks()
    {
        if (!AmongUsClient.Instance.AmHost || OwnerId != Player.PlayerId)
        {
            return;
        }

        if (Phase == ScalesPhase.Pending && pendingStart)
        {
            pendingStart = false;
            _ = new LateTask(
                () => TryStartSpecialMeeting(0),
                1f,
                "ScalesStartSpecialMeeting");
            return;
        }

        if (Phase == ScalesPhase.Finishing)
        {
            ResetAfterSpecialMeeting();
        }
    }

    public override string GetLowerText(
        PlayerControl seer,
        PlayerControl seen = null,
        bool isForMeeting = false,
        bool isForHud = false)
    {
        seen ??= seer;
        if (!isForMeeting
            || Phase != ScalesPhase.None
            || seer.PlayerId != Player.PlayerId
            || seen.PlayerId != Player.PlayerId
            || !Player.IsAlive()
            || IsAbilityUseLimitReached)
        {
            return string.Empty;
        }

        if (OptionCanUseAllAlive.GetBool() && !GameStates.AlreadyDied)
        {
            return string.Empty;
        }

        var text = Utils.ColorString(
            RoleInfo.RoleColor,
            GetString("ScalesSelfVoteHint"));
        return isForHud ? text : $"<size=40%>{text}</size>";
    }

    public override void ReceiveRPC(MessageReader reader)
    {
        Phase = (ScalesPhase)reader.ReadByte();
        OwnerId = reader.ReadByte();
        Target1Id = reader.ReadByte();
        Target2Id = reader.ReadByte();
        // SCALES_ABILITY_USE_COUNT_PERMANENT: 同一MOD参加者にも使用回数を正確に同期する。
        usedCount = reader.ReadInt32();
    }

    private void CancelSelection()
    {
        selecting = false;
        selectedTarget1 = byte.MaxValue;
        selectedTarget2 = byte.MaxValue;
        Utils.SendMessage(
            GetString("ScalesSelectionCanceled"),
            Player.PlayerId,
            Utils.ColorString(RoleInfo.RoleColor, GetString("Scales")));
    }

    private static void ClearLocalSelectionStates(byte preservePlayerId = byte.MaxValue)
    {
        foreach (var instance in Instances)
        {
            if (instance == null)
            {
                continue;
            }

            if (instance.Player != null
                && instance.Player.PlayerId == preservePlayerId)
            {
                continue;
            }

            instance.selectedTarget1 = byte.MaxValue;
            instance.selectedTarget2 = byte.MaxValue;
            instance.selecting = false;
            instance.pendingStart = false;
        }
    }

    private void TryStartSpecialMeeting(int retryCount)
    {
        if (!AmongUsClient.Instance.AmHost
            || Phase != ScalesPhase.Pending
            || OwnerId != Player.PlayerId)
        {
            return;
        }

        if (MeetingHud.Instance != null
            || !GameStates.IsInTask
            || !AreConnectedPlayersSpawned())
        {
            if (retryCount < 20)
            {
                _ = new LateTask(
                    () => TryStartSpecialMeeting(retryCount + 1),
                    0.5f,
                    "ScalesWaitSpecialMeeting");
            }
            else
            {
                AbortSpecialMeeting("ScalesStartFailed");
            }
            return;
        }

        var target1 = GetAlivePlayer(Target1Id);
        var target2 = GetAlivePlayer(Target2Id);
        if (!Player.IsAlive() || target1 == null || target2 == null)
        {
            AbortSpecialMeeting("ScalesTargetUnavailable");
            return;
        }

        var meetingTarget = target1;
        Phase = ScalesPhase.Active;
        SendState();

        GameStates.InTask = false;
        MeetingDisplay.CreateButtonInfo(Player, meetingTarget.Data);
        MeetingTimeManager.OnReportDeadBody();
        Utils.NotifyRoles(isForMeeting: true, NoCache: true);
        Utils.SyncAllSettings();

        Logger.Info(
            $"{Player.GetNameWithRole()} が {GetPlayerName(Target1Id)} と {GetPlayerName(Target2Id)} の天秤会議を開始します",
            "Scales");
        Player.ReportDeadBodyForced(meetingTarget.Data);
    }

    private void AbortSpecialMeeting(string messageKey)
    {
        Utils.SendMessage(
            GetString(messageKey),
            Player.PlayerId,
            Utils.ColorString(RoleInfo.RoleColor, GetString("Scales")));
        ResetAfterSpecialMeeting();
    }

    private void ResetAfterSpecialMeeting()
    {
        RestoreTieExileDisplayNamesPermanent();
        // SCALES_MULTI_SELECTION_RESET_PERMANENT: 全天秤の途中選択を消す（usedは維持）
        ClearLocalSelectionStates();
        ResetGlobalState();
        SendState();
        _ = new LateTask(
            () => Utils.NotifyRoles(ForceLoop: true, NoCache: true),
            0.5f,
            "ScalesRefreshNames");
    }

    private void SendState()
    {
        if (!AmongUsClient.Instance.AmHost)
        {
            return;
        }

        using var sender = CreateSender();
        sender.Writer.Write((byte)Phase);
        sender.Writer.Write(OwnerId);
        sender.Writer.Write(Target1Id);
        sender.Writer.Write(Target2Id);
        sender.Writer.Write(usedCount);
    }

    private static void ResetGlobalState()
    {
        Phase = ScalesPhase.None;
        OwnerId = byte.MaxValue;
        Target1Id = byte.MaxValue;
        Target2Id = byte.MaxValue;

        TieExileDisplayActivePermanent = false;
        TieExileDisplayCarrierIdPermanent = byte.MaxValue;
        TieExileDisplayTextPermanent = string.Empty;
        TieExileDisplayOriginalCarrierNamePermanent = string.Empty;
        TieExileDisplayNameAppliedPermanent = false;
    }

    private static bool AreConnectedPlayersSpawned()
    {
        foreach (var player in Main.AllPlayerControls)
        {
            if (player == null
                || player.Data == null
                || player.Data.Disconnected)
            {
                continue;
            }

            if (PlayerState.GetByPlayerId(player.PlayerId)?.HasSpawned != true)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSelectableTarget(PlayerControl player)
    {
        return player != null
            && player.Data != null
            && !player.Data.Disconnected
            && player.IsAlive();
    }

    private static PlayerControl GetAlivePlayer(byte playerId)
    {
        var player = Utils.GetPlayerById(playerId);
        return IsSelectableTarget(player) ? player : null;
    }

    private static string GetPlayerName(byte playerId)
    {
        return Utils.GetPlayerById(playerId)?.GetRealName()
            ?? GetString("ScalesUnknownPlayer");
    }

    private static bool IsTarget(byte playerId)
    {
        return playerId == Target1Id || playerId == Target2Id;
    }

    private static string GetMarkOthers(
        PlayerControl seer,
        PlayerControl seen,
        bool isForMeeting = false)
    {
        if (!isForMeeting
            || (Phase != ScalesPhase.Active && Phase != ScalesPhase.Finishing)
            || seen == null
            || !IsTarget(seen.PlayerId))
        {
            return string.Empty;
        }

        return Utils.ColorString(RoleInfo.RoleColor, "Ω");
    }

    public static bool TryGetSpecialMeetingTime(out int votingTime)
    {
        votingTime = 0;
        if (Phase != ScalesPhase.Active)
        {
            return false;
        }

        votingTime = OptionMeetingTime.GetInt();
        return true;
    }

    public static bool ShouldBlockMeetingAbilityVote(
        PlayerControl voter,
        byte targetId)
    {
        return Phase == ScalesPhase.Active
            && OptionCanUseMeetingAbility.GetBool()
            && voter != null
            && !voter.Is(CustomRoles.Scales)
            && !IsTarget(targetId);
    }

    public static bool IsMeetingAbilityTargetBlocked(byte targetId)
    {
        return Phase == ScalesPhase.Active
            && OptionCanUseMeetingAbility.GetBool()
            && !IsTarget(targetId);
    }

    public static bool IsAllowedSpecialVote(byte targetId)
    {
        return Phase != ScalesPhase.Active || IsTarget(targetId);
    }

    public static string GetInvalidVoteMessage()
    {
        return GetString("ScalesInvalidVote");
    }

    public static string GetAbilityBlockedMessage()
    {
        return GetString("ScalesMeetingAbilityBlocked");
    }

    public static bool TryEndSpecialMeeting(
        MeetingVoteManager voteManager,
        MeetingHud meetingHud)
    {
        if (!AmongUsClient.Instance.AmHost
            || Phase != ScalesPhase.Active)
        {
            return false;
        }

        var target1 = GetAlivePlayer(Target1Id);
        var target2 = GetAlivePlayer(Target2Id);

        foreach (var entry in voteManager.AllVotes)
        {
            var vote = entry.Value;
            var voter = GetAlivePlayer(vote.Voter);
            if (voter == null)
            {
                continue;
            }

            if (vote.HasVoted && IsTarget(vote.VotedFor))
            {
                continue;
            }

            byte automaticTarget;
            if (target1 == null && target2 == null)
            {
                continue;
            }
            else if (target1 == null)
            {
                automaticTarget = Target2Id;
            }
            else if (target2 == null)
            {
                automaticTarget = Target1Id;
            }
            else
            {
                automaticTarget = IRandom.Instance.Next(0, 2) == 0
                    ? Target1Id
                    : Target2Id;
            }

            voteManager.SetVote(
                vote.Voter,
                automaticTarget,
                isIntentional: false,
                applyRoleEffects: false);
        }

        var target1Votes = 0;
        var target2Votes = 0;
        var states = new List<MeetingHud.VoterState>();

        foreach (var entry in voteManager.AllVotes)
        {
            var vote = entry.Value;
            for (var index = 0; index < vote.NumVotes; index++)
            {
                states.Add(
                    new MeetingHud.VoterState
                    {
                        VoterId = vote.Voter,
                        VotedForId = vote.VotedFor,
                    });
            }

            if (GetAlivePlayer(vote.Voter) == null)
            {
                continue;
            }

            if (vote.VotedFor == Target1Id)
            {
                target1Votes += vote.NumVotes;
            }
            else if (vote.VotedFor == Target2Id)
            {
                target2Votes += vote.NumVotes;
            }
        }

        NetworkedPlayerInfo exiled = null;
        var isTie = false;
        PlayerControl tieDisplayCarrierPermanent = null;

        if (target1 == null && target2 == null)
        {
            isTie = true;
        }
        else if (target1 == null)
        {
            exiled = target2.Data;
        }
        else if (target2 == null)
        {
            exiled = target1.Data;
        }
        else if (target1Votes == target2Votes)
        {
            isTie = true;
            // TOHK原作と同様、表示carrierは可能なら非ホスト側を使う。
            tieDisplayCarrierPermanent = target1.AmOwner ? target2 : target1;
            target1.SetRealKiller(null);
            target2.SetRealKiller(null);
            MeetingHudPatch.TryAddAfterMeetingDeathPlayers(
                CustomDeathReason.Vote,
                Target1Id,
                Target2Id);
            Utils.SendMessage(
                GetString("ScalesBothExiled"),
                title: Utils.ColorString(
                    RoleInfo.RoleColor,
                    GetString("ScalesMeetingTitle")));
        }
        else
        {
            var exileId = target1Votes > target2Votes
                ? Target1Id
                : Target2Id;
            exiled = Utils.GetPlayerInfoById(exileId);
        }

        Logger.Info(
            $"天秤会議結果: {GetPlayerName(Target1Id)}={target1Votes}, {GetPlayerName(Target2Id)}={target2Votes}, Tie={isTie}",
            "Scales");

        ExileControllerWrapUpPatch.AntiBlackout_LastExiled = null;
        Phase = ScalesPhase.Finishing;
        GetOwnerInstance()?.SendState();

        // SCALES_TIE_EXILE_DISPLAY_PERMANENT:
        // 同票時だけ表示経路を分離。失敗時は既存null/tie表示へ完全フォールバックする。
        if (tieDisplayCarrierPermanent != null
            && TrySendTieExileDisplayPermanent(
                meetingHud,
                states.ToArray(),
                tieDisplayCarrierPermanent))
        {
            return true;
        }

        if (AntiBlackout.OverrideExiledPlayer)
        {
            meetingHud.RpcVotingComplete(
                states.ToArray(),
                null,
                true);
            ExileControllerWrapUpPatch.AntiBlackout_LastExiled = exiled;
        }
        else
        {
            meetingHud.RpcVotingComplete(
                states.ToArray(),
                exiled,
                isTie);
        }

        if (exiled != null)
        {
            MeetingHudPatch.CheckForDeathOnExile(
                CustomDeathReason.Vote,
                exiled.PlayerId);
        }

        return true;
    }


    // SCALES_TIE_EXILE_DISPLAY_PERMANENT:
    // ホストのExileController用。バニラremoteはprivate name + targeted VotingCompleteを使用する。
    public static bool TryGetTieExileDisplayPermanent(out string displayText)
    {
        displayText = TieExileDisplayTextPermanent;
        return TieExileDisplayActivePermanent
            && !string.IsNullOrEmpty(displayText);
    }



    private static bool TrySendTieExileDisplayPermanent(
        MeetingHud meetingHud,
        MeetingHud.VoterState[] states,
        PlayerControl carrier)
    {
        if (!AmongUsClient.Instance.AmHost
            || meetingHud == null
            || carrier == null
            || carrier.Data == null)
        {
            return false;
        }

        var connectedCount = 0;
        foreach (var player in Main.AllPlayerControls)
        {
            if (player == null
                || player.Data == null
                || player.Data.Disconnected)
            {
                continue;
            }

            connectedCount++;
        }

        var safetyPrepared = false;

        if (connectedCount >= 4 && connectedCount <= 15)
        {
            // 既存ExileDisplayRealPermanentを再利用し、
            // RoleType安全化→SetIsDead→viewer×target復元を維持する。
            safetyPrepared = AntiBlackout.TryPrepareRealExileDisplayPermanent(carrier.Data);
            if (!safetyPrepared)
            {
                Logger.Warn(
                    "Scales tie display safety preparation failed. Falling back to null/tie.",
                    "ScalesTieExileDisplayPermanent");
                return false;
            }
        }
        else if (AntiBlackout.OverrideExiledPlayer)
        {
            Logger.Warn(
                $"Scales tie display fallback | Connected={connectedCount} | OverrideExiledPlayer=True",
                "ScalesTieExileDisplayPermanent");
            return false;
        }

        var displayText =
            $"{GetPlayerName(Target1Id)} と {GetPlayerName(Target2Id)} が天秤により追放されました";

        TieExileDisplayActivePermanent = true;
        TieExileDisplayCarrierIdPermanent = carrier.PlayerId;
        TieExileDisplayTextPermanent = displayText;
        TieExileDisplayOriginalCarrierNamePermanent = carrier.GetRealName();
        TieExileDisplayNameAppliedPermanent = false;

        var targetedClientCount = 0;

        foreach (var player in Main.AllPlayerControls)
        {
            if (player == null
                || player.Data == null
                || player.Data.Disconnected
                || player.AmOwner)
            {
                continue;
            }

            var sender = CustomRpcSender.Create(
                "ScalesTieExileDisplayPermanent VotingComplete",
                Hazel.SendOption.Reliable);

            sender.StartMessage(player.GetClientId());
            sender.StartRpc(meetingHud.NetId, RpcCalls.VotingComplete)
                .WritePacked(states.Length);

            foreach (var voterState in states)
            {
                voterState.Serialize(sender.stream);
            }

            sender.Write(carrier.PlayerId);
            sender.Write(false);
            sender.EndRpc();
            sender.SendMessage();
            targetedClientCount++;
        }

        // ホスト自身は従来どおりnull/tieを維持する。
        meetingHud.VotingComplete(states, null, true);

        // Among Us v17.4 native解析:
        // ExileController.BeginForGameplayは追放演出開始時に
        // NetworkedPlayerInfo.DefaultOutfitをInitProperties.outfitへ取得し、
        // 直後にExileController.Beginへ渡す。
        // v2の4秒後RpcSetNameは実機でBeginより後になったため遅すぎた。
        // 恒久版ではVotingComplete後1.0秒へ前倒しし、
        // BeginForGameplayがDefaultOutfitを読む前にvanilla側へ反映させる。
        _ = new LateTask(
            () =>
            {
                if (!TieExileDisplayActivePermanent
                    || TieExileDisplayCarrierIdPermanent != carrier.PlayerId)
                {
                    return;
                }

                carrier.RpcSetName(displayText + "<size=0>");
                TieExileDisplayNameAppliedPermanent = true;

                Logger.Info(
                    $"Pre-exile carrier RpcSetName applied | Carrier={carrier.PlayerId} | Delay=1.0s | Text={displayText}",
                    "ScalesTieExileDisplayPermanent");
            },
            1f,
            "ScalesTieExileDisplayPermanentSetName");

        Logger.Info(
            $"Scales tie display sent | Carrier={carrier.PlayerId} | Clients={targetedClientCount} | Safety={safetyPrepared} | PreExileSetName=1.0s | Text={displayText}",
            "ScalesTieExileDisplayPermanent");

        return true;
    }

    private static void RestoreTieExileDisplayNamesPermanent()
    {
        if (!AmongUsClient.Instance.AmHost
            || !TieExileDisplayActivePermanent)
        {
            return;
        }

        var carrier = Utils.GetPlayerById(TieExileDisplayCarrierIdPermanent);
        if (carrier != null
            && TieExileDisplayNameAppliedPermanent
            && !string.IsNullOrEmpty(TieExileDisplayOriginalCarrierNamePermanent))
        {
            carrier.RpcSetName(TieExileDisplayOriginalCarrierNamePermanent);

            Logger.Info(
                $"Carrier name restored | Carrier={carrier.PlayerId} | Name={TieExileDisplayOriginalCarrierNamePermanent}",
                "ScalesTieExileDisplayPermanent");
        }

        TieExileDisplayActivePermanent = false;
        TieExileDisplayCarrierIdPermanent = byte.MaxValue;
        TieExileDisplayTextPermanent = string.Empty;
        TieExileDisplayOriginalCarrierNamePermanent = string.Empty;
        TieExileDisplayNameAppliedPermanent = false;
    }

    private static Scales GetOwnerInstance()
    {
        foreach (var instance in Instances)
        {
            if (instance.Player != null
                && instance.Player.PlayerId == OwnerId)
            {
                return instance;
            }
        }

        return null;
    }
}
