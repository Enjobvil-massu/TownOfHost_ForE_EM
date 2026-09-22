using System;
using System.Linq;
using System.Collections.Generic;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.AddOns.Common;
using TownOfHostForE.Roles.Madmate;
using TownOfHostForE.Roles.Crewmate;

namespace TownOfHostForE.Modules;

public class MeetingVoteManager
{
    public IReadOnlyDictionary<byte, VoteData> AllVotes => allVotes;
    private Dictionary<byte, VoteData> allVotes = new(15);
    private readonly MeetingHud meetingHud;

    public static MeetingVoteManager Instance => _instance;
    private static MeetingVoteManager _instance;
    private static LogHandler logger = Logger.Handler(nameof(MeetingVoteManager));

    // VANILLA_ACTUAL_EXILE_ATOMIC_DEAD_GHOST_AB_V3_4:
    // Actual exileを既存の推測stateから再構成せず、実際に送信完了した
    // VotingCompleteだけをH4 causal A/Bの対象にするbehavior-neutral ledger。
    internal readonly struct H4ActualExileLedger
    {
        internal H4ActualExileLedger(
            uint meetingGeneration,
            byte actualExiledPlayerId,
            int targetClientId,
            bool realVotingCompleteDelivered,
            bool isTie,
            string path)
        {
            MeetingGeneration = meetingGeneration;
            ActualExiledPlayerId = actualExiledPlayerId;
            TargetClientId = targetClientId;
            RealVotingCompleteDelivered = realVotingCompleteDelivered;
            IsTie = isTie;
            Path = path;
        }

        internal uint MeetingGeneration { get; }
        internal byte ActualExiledPlayerId { get; }
        internal int TargetClientId { get; }
        internal bool RealVotingCompleteDelivered { get; }
        internal bool IsTie { get; }
        internal string Path { get; }
    }

    private static uint h4MeetingGeneration;
    private static H4ActualExileLedger h4ActualExileLedger = new(
        0,
        byte.MaxValue,
        -1,
        false,
        false,
        "NONE");

    internal static uint H4MeetingGeneration => h4MeetingGeneration;


    // VIEWER_NATIVE_TOPOLOGY_OUTCOME_BARRIER_AB_V28:
    private sealed class ViewerNativeEndMeetingSnapshotAbV28
    {
        internal ViewerNativeEndMeetingSnapshotAbV28(
            uint meetingGeneration,
            VoteResult result,
            NetworkedPlayerInfo? exiled,
            MeetingHud.VoterState[] states,
            byte antiCompId,
            int preparedViewerCount,
            bool finalTie,
            bool wasOverruled,
            ushort overruleNonce)
        {
            MeetingGeneration = meetingGeneration;
            Result = result;
            Exiled = exiled;
            States = states;
            AntiCompId = antiCompId;
            PreparedViewerCount = preparedViewerCount;
            FinalTie = finalTie;
            WasOverruled = wasOverruled;
            OverruleNonce = overruleNonce;
        }

        internal uint MeetingGeneration { get; }
        internal VoteResult Result { get; }
        internal NetworkedPlayerInfo? Exiled { get; }
        internal MeetingHud.VoterState[] States { get; }
        internal byte AntiCompId { get; }
        internal int PreparedViewerCount { get; }
        internal bool FinalTie { get; }
        internal bool WasOverruled { get; }
        internal ushort OverruleNonce { get; }
    }

    private bool viewerNativeEndMeetingBarrierPendingAbV28;
    private ViewerNativeEndMeetingSnapshotAbV28?
        viewerNativeEndMeetingResumeSnapshotAbV28;

    private bool TryConsumeViewerNativeEndMeetingSnapshotAbV28(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out ViewerNativeEndMeetingSnapshotAbV28? snapshot)
    {
        snapshot =
            viewerNativeEndMeetingResumeSnapshotAbV28;

        if (snapshot == null ||
            snapshot.MeetingGeneration != h4MeetingGeneration)
        {
            viewerNativeEndMeetingResumeSnapshotAbV28 = null;
            return false;
        }

        viewerNativeEndMeetingResumeSnapshotAbV28 = null;
        return true;
    }

    private void ArmViewerNativeEndMeetingBarrierAbV28(
        VoteResult result,
        NetworkedPlayerInfo? exiled,
        MeetingHud.VoterState[] states,
        byte antiCompId,
        int preparedViewerCount,
        bool finalTie,
        bool wasOverruled,
        ushort overruleNonce)
    {
        if (viewerNativeEndMeetingBarrierPendingAbV28)
            return;

        var snapshot =
            new ViewerNativeEndMeetingSnapshotAbV28(
                h4MeetingGeneration,
                result,
                exiled,
                states,
                antiCompId,
                preparedViewerCount,
                finalTie,
                wasOverruled,
                overruleNonce);

        viewerNativeEndMeetingBarrierPendingAbV28 = true;

        Logger.Info(
            $"Stage=END_BARRIER_ARMED | MeetingGeneration={h4MeetingGeneration} | " +
            $"ExiledId={(exiled == null ? byte.MaxValue : exiled.PlayerId)} | " +
            $"Tie={finalTie} | WasOverruled={wasOverruled} | OverruleNonce={overruleNonce} | PreparedViewers={preparedViewerCount} | " +
            "BarrierSeconds=0.5",
            "VIEWER_NATIVE_TOPOLOGY_OUTCOME_BARRIER_AB_V28");

        _ = new LateTask(
            () =>
            {
                if (!ReferenceEquals(_instance, this) ||
                    snapshot.MeetingGeneration != h4MeetingGeneration)
                {
                    viewerNativeEndMeetingBarrierPendingAbV28 = false;
                    Logger.Info(
                        $"Stage=END_BARRIER_ABORT_STALE | CapturedGeneration={snapshot.MeetingGeneration} | " +
                        $"CurrentGeneration={h4MeetingGeneration}",
                        "VIEWER_NATIVE_TOPOLOGY_OUTCOME_BARRIER_AB_V28");
                    return;
                }

                viewerNativeEndMeetingBarrierPendingAbV28 = false;
                viewerNativeEndMeetingResumeSnapshotAbV28 = snapshot;

                Logger.Info(
                    $"Stage=END_BARRIER_RESUME | MeetingGeneration={snapshot.MeetingGeneration} | " +
                    $"ExiledId={(snapshot.Exiled == null ? byte.MaxValue : snapshot.Exiled.PlayerId)} | " +
                    $"Tie={snapshot.FinalTie} | WasOverruled={snapshot.WasOverruled} | OverruleNonce={snapshot.OverruleNonce} | PreparedViewers={snapshot.PreparedViewerCount}",
                    "VIEWER_NATIVE_TOPOLOGY_OUTCOME_BARRIER_AB_V28");

                EndMeeting(
                    applyVoteMode: false,
                    antiCompId: snapshot.AntiCompId);
            },
            0.5f,
            "ViewerNativeEndMeetingBarrierABV28");
    }

    private static void H4BeginMeeting()
    {
        h4MeetingGeneration = h4MeetingGeneration == uint.MaxValue
            ? 1u
            : h4MeetingGeneration + 1u;
        h4ActualExileLedger = new(
            h4MeetingGeneration,
            byte.MaxValue,
            -1,
            false,
            false,
            "MEETING_STARTED");
        ExileControllerWrapUpPatch.VanillaActualExileAtomicDeadGhostAbV34.ResetForMeeting(
            h4MeetingGeneration);
    }

    private static void H4RecordActualExile(
        NetworkedPlayerInfo? exiled,
        bool isTie,
        string path)
    {
        var playerId = exiled?.PlayerId ?? byte.MaxValue;
        var clientId = exiled?.Object == null ? -1 : exiled.Object.GetClientId();
        h4ActualExileLedger = new(
            h4MeetingGeneration,
            playerId,
            clientId,
            false,
            isTie,
            path);

        Logger.Info(
            $"Stage=LEDGER_CAPTURE | MeetingGeneration={h4MeetingGeneration} | PlayerId={playerId} | ClientId={clientId} | Delivered=False | Tie={isTie} | Path={path}",
            "VANILLA_ACTUAL_EXILE_ATOMIC_DEAD_GHOST_AB_V3_4");
    }

    private static void H4MarkRealVotingCompleteDelivered(byte playerId, int clientId)
    {
        if (h4ActualExileLedger.MeetingGeneration != h4MeetingGeneration ||
            h4ActualExileLedger.ActualExiledPlayerId != playerId ||
            h4ActualExileLedger.TargetClientId != clientId ||
            h4ActualExileLedger.IsTie)
            return;

        h4ActualExileLedger = new(
            h4ActualExileLedger.MeetingGeneration,
            h4ActualExileLedger.ActualExiledPlayerId,
            h4ActualExileLedger.TargetClientId,
            true,
            h4ActualExileLedger.IsTie,
            h4ActualExileLedger.Path);

        Logger.Info(
            $"Stage=LEDGER_CAPTURE | MeetingGeneration={h4MeetingGeneration} | PlayerId={playerId} | ClientId={clientId} | Delivered=True | Tie=False | Path={h4ActualExileLedger.Path}",
            "VANILLA_ACTUAL_EXILE_ATOMIC_DEAD_GHOST_AB_V3_4");
    }

    internal static bool H4TryGetActualExileLedger(out H4ActualExileLedger ledger)
    {
        ledger = h4ActualExileLedger;
        return ledger.MeetingGeneration == h4MeetingGeneration;
    }

    private MeetingVoteManager()
    {
        meetingHud = MeetingHud.Instance;
        ClearVotes();
    }

    public static void Start()
    {
        _instance = new();
        H4BeginMeeting();
        TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.Meeting());
    }

    /// <summary>
    /// 投票を初期状態にします
    /// </summary>
    public void ClearVotes()
    {
        foreach (var voteArea in meetingHud.playerStates)
        {
            allVotes[voteArea.PlayerId] = new(voteArea.PlayerId);
        }
    }
    /// <summary>
    /// 今までに行われた投票をすべて削除し，特定の投票先に1票投じられた状態で会議を強制終了します
    /// </summary>
    /// <param name="voter">投票を行う人</param>
    /// <param name="exiled">追放先</param>
    public void ClearAndExile(byte voter, byte exiled, bool isAntiComp = false)
    {
        logger.Info($"{Utils.GetPlayerById(voter).GetNameWithRole()} によって {GetVoteName(exiled)} が追放されます");
        ClearVotes();
        var vote = new VoteData(voter);
        vote.DoVote(exiled, 1);
        allVotes[voter] = vote;
        byte antiCompId = isAntiComp ? voter : byte.MaxValue;
        EndMeeting(false, antiCompId);
    }
    /// <summary>
    /// 投票を行います．投票者が既に投票している場合は票を上書きします
    /// </summary>
    /// <param name="voter">投票者</param>
    /// <param name="voteFor">投票先</param>
    /// <param name="numVotes">票数</param>
    /// <param name="isIntentional">投票者自身の投票操作による自発的な投票かどうか</param>
    public void SetVote(byte voter, byte voteFor, int numVotes = 1, bool isIntentional = true, bool applyRoleEffects = true)
    {
        if (!allVotes.TryGetValue(voter, out var vote))
        {
            logger.Warn($"ID: {voter}の投票データがありません。新規作成します");
            vote = new(voter);
        }
        if (vote.HasVoted)
        {
            logger.Info($"ID: {voter}の投票を上書きします");
        }
        if (!applyRoleEffects)
        {
            logger.Info($"ID: {voter}の投票を役職能力なしで{GetVoteName(voteFor)}として記録します");
            vote.DoVote(voteFor, numVotes);
            // ROLEBUGTRACE_STAGE1B_PERMANENT
            RoleBugTrace.Vote(voter, voteFor, numVotes, true, "NoRoleEffects");
            return;
        }

        var voterPc = Utils.GetPlayerById(voter);

        bool doVote = true;
        foreach (var role in CustomRoleManager.AllActiveRoles.Values)
        {
            var (roleVoteFor, roleNumVotes, roleDoVote) = role.ModifyVote(voter, voteFor, isIntentional);
            
            if (Chu2Byo.Ch2PowerWatch(voterPc))
            {
                (roleVoteFor, roleNumVotes, roleDoVote) = Chu2Byo.Ch2OnVote(voter, voteFor, voterPc);
            }

            if (roleVoteFor.HasValue)
            {
                logger.Info($"{role.Player.GetNameWithRole()} が {voterPc.GetNameWithRole()} の投票先を {GetVoteName(roleVoteFor.Value)} に変更します");
                voteFor = roleVoteFor.Value;
            }
            if (roleNumVotes.HasValue)
            {
                logger.Info($"{role.Player.GetNameWithRole()} が {voterPc.GetNameWithRole()} の投票数を {roleNumVotes.Value} に変更します");
                numVotes = roleNumVotes.Value;
            }
            if (!roleDoVote)
            {
                logger.Info($"{role.Player.GetNameWithRole()} によって投票は取り消されます");
                doVote = roleDoVote;
            }

        }
        numVotes = PlusVote.OnVote(voter, numVotes);
        TieBreaker.OnVote(voter, voteFor);

        if (doVote)
        {
            vote.DoVote(voteFor, numVotes);
            // ROLEBUGTRACE_STAGE1B_PERMANENT
            RoleBugTrace.Vote(voter, voteFor, numVotes, true, "RoleEffectsApplied");
        }
        else
        {
            RoleBugTrace.Vote(voter, voteFor, numVotes, false, "CanceledByRole");
        }
    }
    /// <summary>
    /// 議論時間が終わってる or 全員が投票を終えていれば会議を終了します
    /// </summary>
    public void CheckAndEndMeeting()
    {
        // SCALES_PORT_PERMANENT: 天秤会議だけ専用投票時間で終了判定する
        if (Scales.TryGetSpecialMeetingTime(out var scalesVotingTime))
        {
            if (meetingHud.discussionTimer >= scalesVotingTime
                || AllVotes.Values.All(vote => vote.HasVoted))
            {
                EndMeeting();
            }
            return;
        }

        if (meetingHud.discussionTimer - (float)Main.NormalOptions.DiscussionTime >= Main.NormalOptions.VotingTime || AllVotes.Values.All(vote => vote.HasVoted))
        {
            EndMeeting();
        }
    }
    /// <summary>
    /// 無条件で会議を終了します
    /// </summary>
    /// <param name="applyVoteMode">スキップと同数投票の設定を適用するかどうか</param>
    public void EndMeeting(bool applyVoteMode = true, byte antiCompId = byte.MaxValue)
    {
        if (viewerNativeEndMeetingBarrierPendingAbV28)
        {
            Logger.Info(
                $"Stage=END_BARRIER_PENDING_REENTRY_BLOCKED | MeetingGeneration={h4MeetingGeneration}",
                "VIEWER_NATIVE_TOPOLOGY_OUTCOME_BARRIER_AB_V28");
            return;
        }

        VoteResult result;
        NetworkedPlayerInfo? exiled;
        List<MeetingHud.VoterState> states;
        bool finalTie;
        bool wasOverruled;
        ushort overruleNonce;

        if (TryConsumeViewerNativeEndMeetingSnapshotAbV28(
                out var v28Snapshot))
        {
            result = v28Snapshot.Result;
            exiled = v28Snapshot.Exiled;
            states =
                new List<MeetingHud.VoterState>(
                    v28Snapshot.States);
            finalTie = v28Snapshot.FinalTie;
            wasOverruled = v28Snapshot.WasOverruled;
            overruleNonce = v28Snapshot.OverruleNonce;
        }
        else
        {

            // SCALES_PORT_PERMANENT: 天秤専用会議は通常の同数・勝敗上書きを通さず専用集計する
            if (Scales.TryEndSpecialMeeting(this, meetingHud))
            {
                Destroy();
                return;
            }

            result = CountVotes(applyVoteMode);
            //Y-anti
            exiled = (antiCompId == byte.MaxValue) ? result.Exiled : Utils.GetPlayerInfoById(antiCompId);

            finalTie = result.IsTie;
            wasOverruled = false;
            overruleNonce = 0;

            var judgeOverruleAllowed =
                antiCompId == byte.MaxValue &&
                !(MeetingStates.FirstMeeting &&
                  Options.IsFirstTurnMeetingAbilityBlocked);

            if (judgeOverruleAllowed &&
                meetingHud.TryGetWinningOverrule(
                    out var winningOverrule,
                    out var judgePlayerInfo,
                    out var overruledPlayerInfo))
            {
                var judgePlayer = judgePlayerInfo?.Object;
                var overruledPlayer = overruledPlayerInfo?.Object;

                if (judgePlayer != null &&
                    overruledPlayer != null)
                {
                    var overruleCorrect =
                        overruledPlayer.Is(CustomRoleTypes.Impostor);

                    exiled = overruleCorrect
                        ? overruledPlayerInfo
                        : judgePlayerInfo;
                    finalTie = false;
                    wasOverruled = true;
                    overruleNonce = winningOverrule.OverruleNonce;

                    var finalExiledPlayerId =
                        overruleCorrect
                            ? overruledPlayer.PlayerId
                            : judgePlayer.PlayerId;

                    logger.Info(
                        $"[JUDGE_NATIVE_OVERRULE_AB_V2] Judge Overrule | Judge={judgePlayer.PlayerId} | Target={overruledPlayer.PlayerId} | Correct={overruleCorrect} | Exiled={finalExiledPlayerId} | Nonce={overruleNonce}");
                }
                else
                {
                    logger.Warn(
                        "[JUDGE_NATIVE_OVERRULE_AB_V2] Judge Overrule native winner had null PlayerControl; preserve normal vote result");
                }
            }

            var logName = exiled == null ? (finalTie ? "同数" : "スキップ") : exiled.Object.GetNameWithRole();
            logger.Info($"追放者: {logName} で会議を終了します");

            states = new List<MeetingHud.VoterState>();
            foreach (var voteArea in meetingHud.playerStates)
            {
                var voteData = AllVotes.TryGetValue(voteArea.PlayerId, out var value) ? value : null;
                if (voteData == null)
                {
                    logger.Warn($"{Utils.GetPlayerById(voteArea.PlayerId).GetNameWithRole()} の投票データがありません");
                    continue;
                }
                for (var i = 0; i < voteData.NumVotes; i++)
                {
                    states.Add(new()
                    {
                        VoterId = voteArea.PlayerId,
                        VotedForId = voteData.VotedFor,
                    });
                }
            }

            if (AntiBlackout.TryPrepareViewerNativeEndMeetingBarrierAbV28(
                    exiled,
                    finalTie,
                    out var v28PreparedViewerCount))
            {
                ArmViewerNativeEndMeetingBarrierAbV28(
                    result,
                    exiled,
                    states.ToArray(),
                    antiCompId,
                    v28PreparedViewerCount,
                    finalTie,
                    wasOverruled,
                    overruleNonce);
                return;
            }
        }

        if (AntiBlackout.OverrideExiledPlayer)
        {
            // AntiBlackout_LastExiled is a legacy null-capable field: existing Scales
            // paths assign null and downstream exile paths explicitly test for null.
            // Suppress only the annotation mismatch here; the runtime value is unchanged.
            ExileControllerWrapUpPatch.AntiBlackout_LastExiled = exiled!;

            // ExileDisplayRealPermanent:
            // 4-15人かつ実追放あり、さらに全viewer x targetの正確な
            // RoleView復元値が揃った場合だけ非ホストへreal VotingCompleteを送る。
            if (exiled != null &&
                !finalTie &&
                AntiBlackout.TryPrepareRealExileDisplayPermanent(exiled))
            {
                H4RecordActualExile(exiled, false, "PREPARED_REAL_TARGETED");
                TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.PreVote(exiled));
                var stateArray = states.ToArray();
                var targetedClientCount = 0;

                foreach (var player in Main.AllPlayerControls)
                {
                    if (player == null ||
                        player.Data == null ||
                        player.Data.Disconnected ||
                        player.AmOwner)
                        continue;

                    if (!player.TryGetClientId(out var clientId)) continue;

                    // TOHKのv17.4系送信形式と同じく、
                    // VoterState.SerializeをCustomRpcSender.streamへ直接書く。
                    var sender = CustomRpcSender.Create(
                        "ExileDisplayRealPermanent VotingComplete",
                        Hazel.SendOption.Reliable
                    );

                    sender.StartMessage(clientId);

                    // HOST_EXILE_PRE_VOTING_DUMMY_AB_V4:
                    // real VotingCompleteより先に、同一targeted GameDataTo内で
                    // 必要viewerだけSafety DummyをImpostorへする。
                    // Reliable順序だけでなく同一message内順序まで固定する。
                    var preVotingExiled = Utils.GetPlayerById(exiled.PlayerId);
                    if (!AntiBlackout.IsViewerNativeEndMeetingBarrierAppliedAbV28(
                            player.PlayerId) &&
                        preVotingExiled != null &&
                        ExileControllerWrapUpPatch.VanillaActualExileCriticalLeaseAbV14A
                            .TryGetModExilePreVotingSafetyDummyAbV5(
                                preVotingExiled,
                                player,
                                out var preVotingDummy) &&
                        preVotingDummy != null)
                    {
                        if (preVotingExiled.AmOwner)
                        {
                            // HOST_EXILE_PRE_VOTING_DUMMY_AB_V4:
                            // Preserve the exact v4 condition/dummy selection.
                            // HOST_EXILE_PRE_VOTING_DUMMY_PACKET_BARRIER_AB_V18:
                            // Only transport changes: send Dummy=Impostor in its own
                            // Reliable targeted packet before the existing VotingComplete.
                            // No artificial delay and no role-map/death-state mutation.
                            var hostPreVotingSafetySender = CustomRpcSender.Create(
                                "HostExile PreVoting Dummy Packet AB V18",
                                Hazel.SendOption.Reliable
                            );
                            hostPreVotingSafetySender.StartMessage(clientId);
                            hostPreVotingSafetySender
                                .StartRpc(preVotingDummy.NetId, RpcCalls.SetRole)
                                .Write((ushort)AmongUs.GameOptions.RoleTypes.Impostor)
                                .Write(true)
                                .EndRpc();
                            hostPreVotingSafetySender.SendMessage();

                            Logger.Info(
                                $"Stage=PRE_VOTING_DUMMY_PACKET_SENT | ViewerId={player.PlayerId} | ViewerClientId={player.GetClientId()} | DummyId={preVotingDummy.PlayerId} | ExiledId={exiled.PlayerId} | SeparateReliableMessage=True | ArtificialDelayMs=0",
                                "HOST_EXILE_PRE_VOTING_DUMMY_PACKET_BARRIER_AB_V18");
                        }
                        else
                        {
                            // REMOTE_MOD_EXILE_PRE_VOTING_FULL_SAFE_BUNDLE_AB_V16:
                            // Preserve v16's exact 1-Impostor + rest-Crew topology.
                            // REMOTE_MOD_EXILE_PRE_VOTING_SAFE_PACKET_BARRIER_AB_V17:
                            // Only the transport boundary changes: send the full safe
                            // topology in its own Reliable targeted message first.
                            // The existing voting sender emits VotingComplete after it.
                            // No artificial time delay is introduced.
                            var preVotingSafetySender = CustomRpcSender.Create(
                                "RemoteModExile PreVoting Safe Packet AB V17",
                                Hazel.SendOption.Reliable
                            );
                            preVotingSafetySender.StartMessage(clientId);

                            preVotingSafetySender
                                .StartRpc(preVotingDummy.NetId, RpcCalls.SetRole)
                                .Write((ushort)AmongUs.GameOptions.RoleTypes.Impostor)
                                .Write(true)
                                .EndRpc();

                            var preVotingFullSafeTargetCount = 1;

                            foreach (var preVotingTarget in Main.AllPlayerControls)
                            {
                                if (preVotingTarget == null ||
                                    preVotingTarget.Data == null ||
                                    preVotingTarget.Data.Disconnected ||
                                    preVotingTarget.PlayerId == preVotingDummy.PlayerId)
                                {
                                    continue;
                                }

                                preVotingSafetySender
                                    .StartRpc(preVotingTarget.NetId, RpcCalls.SetRole)
                                    .Write((ushort)AmongUs.GameOptions.RoleTypes.Crewmate)
                                    .Write(true)
                                    .EndRpc();

                                preVotingFullSafeTargetCount++;
                            }

                            preVotingSafetySender.SendMessage();

                            Logger.Info(
                                $"Stage=PRE_VOTING_SAFE_PACKET_SENT | ViewerId={player.PlayerId} | ViewerClientId={player.GetClientId()} | DummyId={preVotingDummy.PlayerId} | ExiledId={exiled.PlayerId} | Targets={preVotingFullSafeTargetCount} | SeparateReliableMessage=True | ArtificialDelayMs=0",
                                "REMOTE_MOD_EXILE_PRE_VOTING_SAFE_PACKET_BARRIER_AB_V17");

                            // REMOTE_MOD_EXILE_PRE_VOTING_TARGETED_FULL_SAFE_GAMEDATA_AB_V19:
                            // v17 proved that SetRole alone is insufficient in the exact
                            // dangerous viewer case. Add the matching NetworkedPlayerInfo
                            // wire state before VotingComplete without mutating host state.
                            var v19PreparedSenders = new List<CustomRpcSender>();
                            CustomRpcSender? v19BuildingSender = null;
                            var v19PreparedTargetCount = 0;
                            var v19PatchedUInt16Count = 0;
                            var v19MaxPacketBytes = 0;
                            var v19BuildOk = true;
                            var v19AbortReason = "";
                            var v19SentPacketCount = 0;

                            try
                            {
                                foreach (var v19Source in GameData.Instance.AllPlayers)
                                {
                                    if (v19Source == null || v19Source.Disconnected)
                                    {
                                        continue;
                                    }

                                    v19BuildingSender = new CustomRpcSender(
                                        $"RemoteModExile FullSafe GameData AB V19 P{v19Source.PlayerId}",
                                        Hazel.SendOption.Reliable,
                                        false);
                                    v19BuildingSender.StartMessage(clientId);

                                    var v19WireRole =
                                        v19Source.PlayerId == preVotingDummy.PlayerId
                                            ? AmongUs.GameOptions.RoleTypes.Impostor
                                            : AmongUs.GameOptions.RoleTypes.Crewmate;

                                    if (!SelectRolesPatch.TryAppendPreVotingFullSafeNetworkedPlayerInfoAbV19(
                                            v19BuildingSender,
                                            v19Source,
                                            v19WireRole,
                                            out var v19CapturedWrites) ||
                                        v19CapturedWrites != 2)
                                    {
                                        v19AbortReason = $"CAPTURE_COUNT PlayerId={v19Source.PlayerId} Count={v19CapturedWrites}";
                                        v19BuildingSender.stream.Recycle();
                                        v19BuildingSender = null;
                                        v19BuildOk = false;
                                        break;
                                    }

                                    v19BuildingSender.EndMessage();
                                    var v19PacketBytes = v19BuildingSender.stream.Length;
                                    if (v19PacketBytes > 400)
                                    {
                                        v19AbortReason = $"PACKET_TOO_LARGE PlayerId={v19Source.PlayerId} Bytes={v19PacketBytes} Max=400";
                                        v19BuildingSender.stream.Recycle();
                                        v19BuildingSender = null;
                                        v19BuildOk = false;
                                        break;
                                    }

                                    v19MaxPacketBytes = Math.Max(
                                        v19MaxPacketBytes,
                                        v19PacketBytes);
                                    v19PreparedSenders.Add(v19BuildingSender);
                                    v19BuildingSender = null;
                                    v19PreparedTargetCount++;
                                    v19PatchedUInt16Count += v19CapturedWrites;
                                }

                                if (v19BuildOk && v19PreparedTargetCount > 0)
                                {
                                    Logger.Info(
                                        $"Stage=PRE_VOTING_FULL_SAFE_GAMEDATA_PREPARED | ViewerId={player.PlayerId} | ViewerClientId={player.GetClientId()} | DummyId={preVotingDummy.PlayerId} | ExiledId={exiled.PlayerId} | Targets={v19PreparedTargetCount} | Packets={v19PreparedSenders.Count} | PatchedUInt16={v19PatchedUInt16Count} | MaxPacketBytes={v19MaxPacketBytes} | OneNpiPerPacket=True | HostStateMutated=False",
                                        "REMOTE_MOD_EXILE_PRE_VOTING_TARGETED_FULL_SAFE_GAMEDATA_AB_V19");

                                    foreach (var v19PreparedSender in v19PreparedSenders)
                                    {
                                        v19PreparedSender.SendMessage();
                                        v19SentPacketCount++;
                                    }

                                    Logger.Info(
                                        $"Stage=PRE_VOTING_FULL_SAFE_GAMEDATA_SENT | ViewerId={player.PlayerId} | ViewerClientId={player.GetClientId()} | DummyId={preVotingDummy.PlayerId} | ExiledId={exiled.PlayerId} | Targets={v19PreparedTargetCount} | Packets={v19SentPacketCount} | PatchedUInt16={v19PatchedUInt16Count} | MaxPacketBytes={v19MaxPacketBytes} | BeforeVotingComplete=True",
                                        "REMOTE_MOD_EXILE_PRE_VOTING_TARGETED_FULL_SAFE_GAMEDATA_AB_V19");
                                }
                                else
                                {
                                    foreach (var v19PreparedSender in v19PreparedSenders)
                                    {
                                        v19PreparedSender.stream.Recycle();
                                    }

                                    Logger.Warn(
                                        $"Stage=PRE_VOTING_FULL_SAFE_GAMEDATA_ABORT | ViewerId={player.PlayerId} | ViewerClientId={player.GetClientId()} | DummyId={preVotingDummy.PlayerId} | ExiledId={exiled.PlayerId} | Reason={v19AbortReason} | PreparedTargets={v19PreparedTargetCount} | SentPackets=0",
                                        "REMOTE_MOD_EXILE_PRE_VOTING_TARGETED_FULL_SAFE_GAMEDATA_AB_V19");
                                }
                            }
                            catch (Exception ex)
                            {
                                if (v19BuildingSender != null)
                                {
                                    v19BuildingSender.stream.Recycle();
                                }

                                for (var v19Index = v19SentPacketCount;
                                     v19Index < v19PreparedSenders.Count;
                                     v19Index++)
                                {
                                    v19PreparedSenders[v19Index].stream.Recycle();
                                }

                                Logger.Exception(
                                    ex,
                                    "REMOTE_MOD_EXILE_PRE_VOTING_TARGETED_FULL_SAFE_GAMEDATA_AB_V19");
                                Logger.Warn(
                                    $"Stage=PRE_VOTING_FULL_SAFE_GAMEDATA_ABORT | ViewerId={player.PlayerId} | ViewerClientId={player.GetClientId()} | DummyId={preVotingDummy.PlayerId} | ExiledId={exiled.PlayerId} | Reason=EXCEPTION | PreparedTargets={v19PreparedTargetCount} | SentPackets={v19SentPacketCount}",
                                    "REMOTE_MOD_EXILE_PRE_VOTING_TARGETED_FULL_SAFE_GAMEDATA_AB_V19");
                            }
                        }
                    }
                    sender.StartRpc(meetingHud.NetId, RpcCalls.VotingComplete)
                        .WritePacked(stateArray.Length);

                    foreach (var voterState in stateArray)
                    {
                        voterState.Serialize(sender.stream);
                    }

                    sender.Write(exiled.PlayerId);
                    sender.Write(false);
                    sender.Write(wasOverruled);
                    sender.Write(overruleNonce);
                    sender.EndRpc();
                    sender.SendMessage();
                    TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.VotingReturned(player.PlayerId));
                    if (player.PlayerId == exiled.PlayerId && player.TryGetClientId(out var deliveredClientId))
                    {
                        H4MarkRealVotingCompleteDelivered(
                            player.PlayerId,
                            deliveredClientId);
                    }
                    targetedClientCount++;
                }

                // ホスト自身は従来AntiBlackoutのnull/tie状態を維持する。
                // ホストの表示だけは既存ExileControllerBeginDisplayRestorePermanentPatchが
                // AntiBlackout_LastExiledから実追放者へ差し戻す。
                meetingHud.VotingComplete(
                    stateArray,
                    null,
                    true,
                    wasOverruled,
                    overruleNonce);

                Logger.Info(
                    $"Real VotingComplete targeted | Exiled={exiled.PlayerId} | Clients={targetedClientCount}",
                    "ExileDisplayRealPermanent"
                );
            }
            else
            {
                // VANILLA_ACTUAL_EXILE_3P_FALLBACK_SAFE_ROLE_AB_V6
                // 3人・実追放・remote Vanilla本人だけsafe RoleTypeを準備する。
                // VotingCompleteは従来どおり null/tie のまま。PREPARED_REAL_TARGETEDへは入れない。
                if (exiled != null &&
                    !finalTie &&
                    exiled.Object is { } exiledObject &&
                    !exiledObject.AmOwner &&
                    !exiledObject.IsModClient() &&
                    AntiBlackout.TryPrepareThreePlayerFallbackExileSafetyAbV6(exiled))
                {
                    Logger.Info(
                        $"Stage=FALLBACK_SAFETY_PREPARED | Exiled={exiled.PlayerId} | ClientId={exiledObject.GetClientId()} | " +
                        "VotingComplete=null/tie",
                        "VANILLA_ACTUAL_EXILE_3P_FALLBACK_SAFE_ROLE_AB_V6");
                }

                H4RecordActualExile(exiled, finalTie, "ANTIBLACKOUT_FALLBACK");
                TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.PreVote(exiled));

                // FIRST_TURN_SKIP_DISPLAY_AB_V1:
                // 初手強制会議では実投票を全てSkipへ変換しているため、
                // AntiBlackout fallbackのcarrierはnullのまま維持しつつ、
                // tieだけfalseとしてVanilla標準のSkip表示を使用する。
                // 通常追放・本当の同票・通常Skip・H4/V14Aには影響させない。
                var firstTurnForcedSkipDisplay =
                    MeetingStates.FirstMeeting &&
                    Options.IsFirstTurnMeetingAbilityBlocked &&
                    exiled == null &&
                    !finalTie;

                // OFFICIAL_SKIP_DISPLAY_AB_V1:
                // AntiBlackout fallbackでも、追放者なし・真の同票ではない場合は
                // Vanilla公式のSkip結果としてtie=falseを送る。
                // 実追放fallbackと本当の同票は従来どおりtie=trueを維持する。
                var officialSkipDisplay =
                    exiled == null &&
                    !finalTie;

                meetingHud.RpcVotingComplete(
                    states.ToArray(),
                    null,
                    !officialSkipDisplay,
                    wasOverruled,
                    overruleNonce);

                if (firstTurnForcedSkipDisplay)
                {
                    Logger.Info(
                        "VotingComplete=null/false | Native skip display selected",
                        "FirstTurnSkipDisplayABV1");
                }

                if (officialSkipDisplay && !firstTurnForcedSkipDisplay)
                {
                    Logger.Info(
                        "VotingComplete=null/false | Native normal-skip display selected",
                        "OfficialSkipDisplayABV1");
                }
            }
        }
        else
        {
            H4RecordActualExile(exiled, finalTie, "NORMAL_BROADCAST");
            TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.PreVote(exiled));
            meetingHud.RpcVotingComplete(
                states.ToArray(),
                exiled,
                finalTie,
                wasOverruled,
                overruleNonce);
            if (exiled?.Object != null &&
                !exiled.Object.AmOwner &&
                !finalTie)
            {
                H4MarkRealVotingCompleteDelivered(
                    exiled.PlayerId,
                    exiled.Object.GetClientId());
            }
        }
        if (exiled != null)
        {
            MeetingHudPatch.CheckForDeathOnExile(CustomDeathReason.Vote, exiled.PlayerId);
        }

        Destroy();
    }
    /// <summary>
    /// <see cref="AllVotes"/>から投票をカウントします
    /// </summary>
    /// <param name="applyVoteMode">スキップと同数投票の設定を適用するかどうか</param>
    /// <returns>([Key: 投票先,Value: 票数]の辞書, 追放される人, 同数投票かどうか)</returns>
    public VoteResult CountVotes(bool applyVoteMode)
    {
        // 投票モードに従って投票を変更
        if (applyVoteMode && Options.VoteMode.GetBool())
        {
            ApplySkipAndNoVoteMode();
        }

        //死んでる人の票を無効にする。
        deadCheckVote();

        // Key: 投票された人
        // Value: 票数
        Dictionary<byte, int> votes = new();
        foreach (var voteArea in meetingHud.playerStates)
        {
            votes[voteArea.PlayerId] = 0;
        }
        votes[Skip] = 0;
        foreach (var vote in AllVotes.Values)
        {
            //無投票若しくは投票先が死んでいたらカウントしない。
            if (vote.VotedFor == NoVote || (vote.VotedFor != Skip && !Utils.GetPlayerById(vote.VotedFor).IsAlive()))
            {
                continue;
            }
            votes[vote.VotedFor] += vote.NumVotes;
        }

        return new VoteResult(votes);
    }
    /// <summary>
    /// スキップモードと無投票モードに応じて，投票を上書きしたりプレイヤーを死亡させたりします
    /// </summary>
    private void ApplySkipAndNoVoteMode()
    {
        if (Options.FirstTurnMeetingInProgress)
        {
            logger.Info("ゲーム開始直後の強制会議のためスキップ・無投票モードを適用しません");
            return;
        }

        var ignoreSkipModeDueToFirstMeeting = MeetingStates.FirstMeeting && Options.WhenSkipVoteIgnoreFirstMeeting.GetBool();
        var ignoreSkipModeDueToNoDeadBody = !MeetingStates.IsExistDeadBody && Options.WhenSkipVoteIgnoreNoDeadBody.GetBool();
        var ignoreSkipModeDueToEmergency = MeetingStates.IsEmergencyMeeting && Options.WhenSkipVoteIgnoreEmergency.GetBool();
        var ignoreSkipMode = ignoreSkipModeDueToFirstMeeting || ignoreSkipModeDueToNoDeadBody || ignoreSkipModeDueToEmergency;

        var skipMode = Options.GetWhenSkipVote();
        var noVoteMode = Options.GetWhenNonVote();
        foreach (var voteData in AllVotes)
        {
            var vote = voteData.Value;
            if (!vote.HasVoted)
            {
                var voterName = Utils.GetPlayerById(vote.Voter).GetNameWithRole();
                switch (noVoteMode)
                {
                    case VoteMode.Suicide:
                        MeetingHudPatch.TryAddAfterMeetingDeathPlayers(CustomDeathReason.Suicide, vote.Voter);
                        logger.Info($"無投票のため {voterName} を自殺させます");
                        break;
                    case VoteMode.SelfVote:
                        SetVote(vote.Voter, vote.Voter, isIntentional: false);
                        logger.Info($"無投票のため {voterName} に自投票させます");
                        break;
                    case VoteMode.Skip:
                        SetVote(vote.Voter, Skip, isIntentional: false);
                        logger.Info($"無投票のため {voterName} にスキップさせます");
                        break;
                }
            }
            else if (!ignoreSkipMode && vote.IsSkip)
            {
                var voterName = Utils.GetPlayerById(vote.Voter).GetNameWithRole();
                switch (skipMode)
                {
                    case VoteMode.Suicide:
                        MeetingHudPatch.TryAddAfterMeetingDeathPlayers(CustomDeathReason.Suicide, vote.Voter);
                        logger.Info($"スキップしたため {voterName} を自殺させます");
                        break;
                    case VoteMode.SelfVote:
                        SetVote(vote.Voter, vote.Voter, isIntentional: false);
                        logger.Info($"スキップしたため {voterName} に自投票させます");
                        break;
                }
            }
        }
    }
    /// <summary>
    /// ゲッサーなどで議論中に死んだ奴の対応。
    /// 死者の票を無効にする。
    /// </summary>
    private void deadCheckVote()
    {
        foreach (var voteData in AllVotes)
        {
            var vote = voteData.Value;
            if (vote == null) continue;
            //無投票は対象外
            if (!vote.HasVoted) continue;
            //既に無投票なら対象外
            if (vote.VotedFor == NoVote) continue;

            //切断対策
            var voteInfo = Utils.GetPlayerInfoById(vote.Voter);
            if (voteInfo.Disconnected)
            {
                SetVote(vote.Voter, Skip, isIntentional: false);
                logger.Info($" {voteInfo.PlayerName} が切断しているため無投票にします");
                continue;
            }

            var votePc = Utils.GetPlayerById(vote.Voter);
            if (votePc.IsAlive()) continue;
            SetVote(vote.Voter, Skip, isIntentional: false);
            logger.Info($" {votePc.name} が死亡しているため無投票にします");
        }
    }
    public void Destroy()
    {
        viewerNativeEndMeetingBarrierPendingAbV28 = false;
        viewerNativeEndMeetingResumeSnapshotAbV28 = null;
        _instance = null;
    }

    public static string GetVoteName(byte num)
    {
        string name = "invalid";
        var player = Utils.GetPlayerById(num);
        if (num < 15 && player != null) name = player?.GetNameWithRole();
        else if (num == Skip) name = "Skip";
        else if (num == NoVote) name = "None";
        else if (num == 255) name = "Dead";
        return name;
    }

    public class VoteData
    {
        public byte Voter { get; private set; } = byte.MaxValue;
        public byte VotedFor { get; private set; } = NoVote;
        public int NumVotes { get; private set; } = 1;
        public bool IsSkip => VotedFor == Skip && !PlayerState.GetByPlayerId(Voter).IsDead;
        public bool HasVoted => VotedFor != NoVote || PlayerState.GetByPlayerId(Voter).IsDead;

        public VoteData(byte voter) => Voter = voter;

        public void DoVote(byte voteTo, int numVotes)
        {
            logger.Info($"投票: {Utils.GetPlayerById(Voter).GetNameWithRole()} => {GetVoteName(voteTo)} x {numVotes}");
            VotedFor = voteTo;
            NumVotes = numVotes;
        }
    }

    public readonly struct VoteResult
    {
        /// <summary>
        /// Key: 投票された人<br/>
        /// Value: 得票数
        /// </summary>
        public IReadOnlyDictionary<byte, int> VotedCounts => votedCounts;
        private readonly Dictionary<byte, int> votedCounts;
        /// <summary>
        /// 追放されるプレイヤー
        /// </summary>
        public readonly NetworkedPlayerInfo Exiled;
        /// <summary>
        /// 同数投票かどうか
        /// </summary>
        public readonly bool IsTie;

        public VoteResult(Dictionary<byte, int> votedCounts)
        {
            this.votedCounts = votedCounts;

            // 票数順に整列された投票
            var orderedVotes = votedCounts.OrderByDescending(vote => vote.Value);
            // 最も票を得た人の票数
            var maxVoteNum = orderedVotes.FirstOrDefault().Value;
            // 最多票数のプレイヤー全員
            var mostVotedPlayers = votedCounts.Where(vote => vote.Value == maxVoteNum).Select(vote => vote.Key).ToArray();

            // 最多票数のプレイヤーが複数人いる場合
            if (mostVotedPlayers.Length > 1)
            {
                IsTie = true;
                Exiled = null;
                logger.Info($"{string.Join(',', mostVotedPlayers.Select(id => GetVoteName(id)))} が同数");
            }
            else
            {
                IsTie = false;
                Exiled = GameData.Instance.GetPlayerById(mostVotedPlayers[0]);
                logger.Info($"最多得票者: {GetVoteName(mostVotedPlayers[0])}");
            }

            (IsTie, Exiled) = TieBreaker.BreakingVote(IsTie, Exiled, votedCounts, maxVoteNum);
            Exiled = Refusing.VoteChange(Exiled);
            Exiled = Nimrod.VoteChange(Exiled);
            Exiled = MadNimrod.VoteChange(Exiled);

            // 同数投票時の特殊モード
            if (IsTie && Options.VoteMode.GetBool())
            {
                var tieMode = (TieMode)Options.WhenTie.GetValue();
                switch (tieMode)
                {
                    case TieMode.All:
                        var toExile = mostVotedPlayers.Where(id => id != Skip).ToArray();
                        foreach (var playerId in toExile)
                        {
                            Utils.GetPlayerById(playerId)?.SetRealKiller(null);
                        }
                        MeetingHudPatch.TryAddAfterMeetingDeathPlayers(CustomDeathReason.Vote, toExile);
                        Exiled = null;
                        logger.Info("全員追放します");
                        break;
                    case TieMode.Random:
                        var exileId = mostVotedPlayers.OrderBy(_ => Guid.NewGuid()).FirstOrDefault();
                        Exiled = GameData.Instance.GetPlayerById(exileId);
                        IsTie = false;
                        logger.Info($"ランダム追放: {GetVoteName(exileId)}");
                        break;
                }
            }
        }
    }

    public const byte Skip = 253;
    public const byte NoVote = 254;
}
