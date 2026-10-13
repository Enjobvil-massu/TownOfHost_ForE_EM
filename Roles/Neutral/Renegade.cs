using System.Collections.Generic;


using System.Linq;
using AmongUs.GameOptions;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Interfaces;

namespace TownOfHostForE.Roles.Neutral;

/// <summary>
/// レネゲイド。
///
/// R1 core:
/// ・1個ずつの実タスク
/// ・タスク完了後の奉納待機
/// ・緊急ボタン1回/2回入力
/// ・奉納回数
/// ・最終奉納後の覚醒トリガー
///
/// 覚醒中の完全な演出・勝利・時間切れ・全特殊キル無敵化はR2で接続する。
/// </summary>
public sealed class Renegade : RoleBase, IKiller
{
    private const string LogTag = "Renegade";
    private const double EmergencyWindowSeconds = 3.0d;

    public static readonly SimpleRoleInfo RoleInfo =
        SimpleRoleInfo.Create(
            typeof(Renegade),
            player => new Renegade(player),
            CustomRoles.Renegade,
            () => RoleTypes.Crewmate,
            CustomRoleTypes.Neutral,
            60900,
            SetupOptionItem,
            "レネゲイド",
            "#b00020",
            introSound: () => GetIntroSound(RoleTypes.Impostor)
        );

    private static OptionItem OptionRequiredOfferings = null!;
    private static OptionItem OptionAwakenDuration = null!;
    private static OptionItem OptionAwakenSpeed = null!;

    // RENEGADE_TASK_LOCATION_RANDOM_R1
    // Initial assignment can occur before the RoleBase instance exists,
    // so task-selection history is kept by PlayerId.
    private static readonly System.Collections.Generic.Dictionary<byte, SystemTypes>
        LastTaskStartAtByPlayer = new();

    private static readonly System.Collections.Generic.Dictionary<byte, int>
        LastTaskIndexByPlayer = new();

    // Explicit per-game lifecycle reset.
    // Do not infer a new game from RoleBase creation timing.
    [TownOfHostForE.Attributes.GameModuleInitializerAttribute]
    public static void InitTaskSelectionHistory()
    {
        LastTaskStartAtByPlayer.Clear();
        LastTaskIndexByPlayer.Clear();
    }

    private int completedOfferings;
    private bool taskReady;
    private bool awakeningTriggered;

    // RENEGADE_R2A_STATE
    private double awakeningDeadline;
    private int lastAwakeningSecond;
    private float awakeningSpeedBonus;
    private bool awakeningSpeedApplied;
    private bool awakeningExpired;

    // RENEGADE_R2B_STATE
    private bool awakeningKillUiApplied;
    private static readonly System.Collections.Generic.HashSet<byte> TimeoutDeathBypass = new();

    private const int PublicMarkRefreshIntervalSeconds = 5;

    private bool pendingEmergencyPress;
    private double emergencyDeadline;
    private int emergencyGeneration;

    // RENEGADE_R2D_CONTACT_KILL_STATE
    // UltraStar permanent implementation uses a 0.4f body-contact radius.
    // The tiny attempt interval is only a network/guard retry limiter; there is
    // no player-operated kill cooldown or kill button.
    private const float AwakeningContactKillRange = 0.4f;
    private const double AwakeningContactKillAttemptIntervalSeconds = 0.10d;
    private double nextAwakeningContactKillAttemptAt;

    private static double Now => Time.realtimeSinceStartupAsDouble;

    public Renegade(PlayerControl player)
        : base(
            RoleInfo,
            player,
            () => HasTask.ForRecompute
        )
    {
        completedOfferings = 0;
        taskReady = false;
        awakeningTriggered = false;

        // RENEGADE_R2A_INIT
        awakeningDeadline = 0d;
        lastAwakeningSecond = -1;
        awakeningSpeedBonus = 0f;
        awakeningSpeedApplied = false;
        awakeningExpired = false;

        // RENEGADE_R2B_INIT
        awakeningKillUiApplied = false;

        pendingEmergencyPress = false;
        emergencyDeadline = 0d;
        emergencyGeneration = 0;

        // RENEGADE_R2D_CONTACT_KILL_INIT
        nextAwakeningContactKillAttemptAt = 0d;

        // RENEGADE_R2D_WARNING_PIPELINE_REGISTER
        if (!CustomRoleManager.LowerOthers.Contains(GetAwakeningWarningLowerTextOthers))
        {
            CustomRoleManager.LowerOthers.Add(GetAwakeningWarningLowerTextOthers);
        }
    }

    private static void SetupOptionItem()
    {
        OptionRequiredOfferings = IntegerOptionItem.Create(
                60910,
                "RenegadeRequiredOfferings",
                new(1, 10, 1),
                5,
                TabGroup.NeutralRoles,
                false
            )
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Renegade])
            .SetValueFormat(OptionFormat.Pieces);

        OptionAwakenDuration = IntegerOptionItem.Create(
                60920,
                "RenegadeAwakenDuration",
                new(10, 180, 5),
                60,
                TabGroup.NeutralRoles,
                false
            )
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Renegade])
            .SetValueFormat(OptionFormat.Seconds);

        OptionAwakenSpeed = FloatOptionItem.Create(
                60930,
                "RenegadeAwakenSpeed",
                new(1.0f, 3.0f, 0.1f),
                1.3f,
                TabGroup.NeutralRoles,
                false
            )
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Renegade])
            .SetValueFormat(OptionFormat.Multiplier);
    }

    public static int RequiredOfferingsSetting =>
        OptionRequiredOfferings?.GetInt() ?? 5;

    public static int AwakenDurationSetting =>
        OptionAwakenDuration?.GetInt() ?? 60;

    public static float AwakenSpeedSetting =>
        OptionAwakenSpeed?.GetFloat() ?? 1.3f;

    public int CompletedOfferings => completedOfferings;

    public bool IsOfferingReady => taskReady;

    public bool IsAwakeningTriggered => awakeningTriggered;

    /// <summary>
    /// 初回タスク配布時はRoleBase生成前でも1個配布する。
    /// 最終奉納後はfalseとなり、新しいタスクを配布しない。
    /// </summary>
    public static bool ShouldReceiveTask(PlayerControl player)
    {
        if (player == null)
            return false;

        return player.GetRoleClass() is not Renegade renegade ||
               !renegade.awakeningTriggered;
    }

    /// <summary>
    /// 現在マップの使用可能な具体的Vanillaタスクを開始場所(StartAt)ごとに分け、
    /// まず場所を均等ランダム、その場所の中からタスクをランダム選択する。
    ///
    /// 直前と異なる場所が存在する場合は直前の場所を避ける。
    /// 場所候補が1つしかない場合は同じ場所を許可するが、
    /// 可能なら直前と同じtask Indexを避ける。
    ///
    /// 初回配布はRoleBase生成前に来るため、そこでPlayerIdの古い履歴を破棄する。
    /// </summary>
    public static bool TryBuildSingleTaskArray(
        PlayerControl player,
        out Il2CppStructArray<byte> taskTypeIds)
    {
        taskTypeIds = new Il2CppStructArray<byte>(0);

        if (player == null)
        {
            Logger.Warn(
                "Player is null; cannot build Renegade task.",
                LogTag);

            return false;
        }

        if (ShipStatus.Instance == null)
        {
            Logger.Warn(
                "ShipStatus.Instance is null; cannot build Renegade task.",
                LogTag);

            return false;
        }

        byte playerId = player.PlayerId;

        List<NormalPlayerTask> pool = new();

        foreach (var task in ShipStatus.Instance.CommonTasks)
        {
            if (IsTaskAllowed(task))
                pool.Add(task);
        }

        foreach (var task in ShipStatus.Instance.LongTasks)
        {
            if (IsTaskAllowed(task))
                pool.Add(task);
        }

        foreach (var task in ShipStatus.Instance.ShortTasks)
        {
            if (IsTaskAllowed(task))
                pool.Add(task);
        }

        if (pool.Count <= 0)
        {
            Logger.Warn(
                "Current map has no valid concrete task candidate for Renegade.",
                LogTag);

            return false;
        }

        var roomBuckets =
            new System.Collections.Generic.Dictionary<
                SystemTypes,
                List<NormalPlayerTask>>();

        var allRooms =
            new List<SystemTypes>();

        foreach (var task in pool)
        {
            SystemTypes startAt = task.StartAt;

            if (!roomBuckets.TryGetValue(
                    startAt,
                    out var bucket))
            {
                bucket = new List<NormalPlayerTask>();
                roomBuckets.Add(startAt, bucket);
                allRooms.Add(startAt);
            }

            bucket.Add(task);
        }

        bool hasLastRoom =
            LastTaskStartAtByPlayer.TryGetValue(
                playerId,
                out var lastRoom);

        bool hasLastTask =
            LastTaskIndexByPlayer.TryGetValue(
                playerId,
                out var lastTaskIndex);

        var candidateRooms =
            new List<SystemTypes>();

        // 原則として直前の部屋を除外。
        foreach (var room in allRooms)
        {
            if (!hasLastRoom || room != lastRoom)
            {
                candidateRooms.Add(room);
            }
        }

        bool avoidedLastRoom =
            hasLastRoom &&
            candidateRooms.Count > 0;

        // マップ/DisableTasks設定の結果、
        // 直前と同じ部屋しか残らない場合のみ同室を許可。
        if (candidateRooms.Count <= 0)
        {
            foreach (var room in allRooms)
            {
                candidateRooms.Add(room);
            }

            avoidedLastRoom = false;
        }

        if (candidateRooms.Count <= 0)
        {
            Logger.Warn(
                $"No Renegade task room candidate playerId={playerId} pool={pool.Count}",
                LogTag);

            return false;
        }

        var random = IRandom.Instance;

        SystemTypes selectedRoom =
            candidateRooms[
                random.Next(
                    0,
                    candidateRooms.Count)];

        if (!roomBuckets.TryGetValue(
                selectedRoom,
                out var selectedRoomTasks) ||
            selectedRoomTasks.Count <= 0)
        {
            Logger.Warn(
                $"Selected Renegade room has no task playerId={playerId} room={selectedRoom}",
                LogTag);

            return false;
        }

        var taskCandidates =
            new List<NormalPlayerTask>();

        // 同じ部屋へfallbackした場合でも、
        // 同じ具体的task Indexの連続だけは可能な限り避ける。
        foreach (var task in selectedRoomTasks)
        {
            if (!hasLastTask ||
                task.Index != lastTaskIndex)
            {
                taskCandidates.Add(task);
            }
        }

        bool avoidedLastTask =
            hasLastTask &&
            taskCandidates.Count > 0;

        if (taskCandidates.Count <= 0)
        {
            foreach (var task in selectedRoomTasks)
            {
                taskCandidates.Add(task);
            }

            avoidedLastTask = false;
        }

        NormalPlayerTask selected =
            taskCandidates[
                random.Next(
                    0,
                    taskCandidates.Count)];

        taskTypeIds =
            new Il2CppStructArray<byte>(1);

        taskTypeIds[0] =
            (byte)selected.Index;

        LastTaskStartAtByPlayer[playerId] =
            selected.StartAt;

        LastTaskIndexByPlayer[playerId] =
            selected.Index;

        Logger.Info(
            $"TaskSelected " +
            $"playerId={playerId} " +
            $"taskType={selected.TaskType} " +
            $"taskIndex={selected.Index} " +
            $"startAt={selected.StartAt} " +
            $"pool={pool.Count} " +
            $"rooms={roomBuckets.Count} " +
            $"roomCandidates={candidateRooms.Count} " +
            $"roomTasks={selectedRoomTasks.Count} " +
            $"taskCandidates={taskCandidates.Count} " +
            $"previousRoom={(hasLastRoom ? lastRoom.ToString() : "NONE")} " +
            $"previousTaskIndex={(hasLastTask ? lastTaskIndex.ToString() : "NONE")} " +
            $"avoidedLastRoom={avoidedLastRoom} " +
            $"avoidedLastTask={avoidedLastTask}",
            LogTag);

        return true;
    }

    private static bool IsTaskAllowed(NormalPlayerTask task)
    {
        if (!Options.DisableTasks.GetBool())
            return true;

        if (task.TaskType == TaskTypes.SwipeCard &&
            Options.DisableSwipeCard.GetBool())
            return false;

        if (task.TaskType == TaskTypes.SubmitScan &&
            Options.DisableSubmitScan.GetBool())
            return false;

        if (task.TaskType == TaskTypes.UnlockSafe &&
            Options.DisableUnlockSafe.GetBool())
            return false;

        if (task.TaskType == TaskTypes.UploadData &&
            Options.DisableUploadData.GetBool())
            return false;

        if (task.TaskType == TaskTypes.StartReactor &&
            Options.DisableStartReactor.GetBool())
            return false;

        if (task.TaskType == TaskTypes.ResetBreakers &&
            Options.DisableResetBreaker.GetBool())
            return false;

        if (task.TaskType == TaskTypes.FixWeatherNode &&
            Options.DisableFixWeatherNode.GetBool())
            return false;

        return true;
    }

    /// <summary>
    /// ReportDeadBody Prefixの緊急ボタン入力から呼ぶ。
    ///
    /// true  : この入力をUsedButtonCountより前でブロック
    /// false : 既存の通常会議処理へそのまま通す
    /// </summary>
    public static bool ShouldBlockEmergency(PlayerControl player)
    {
        if (!AmongUsClient.Instance.AmHost)
            return false;

        if (player == null || player.Data == null)
            return false;

        if (player.GetRoleClass() is not Renegade renegade)
            return false;

        return renegade.HandleEmergencyPress();
    }

    private bool HandleEmergencyPress()
    {
        double now = Now;

        if (awakeningTriggered)
        {
            LogEmergency(
                "AwakenedBlocked",
                now,
                emergencyDeadline);

            return true;
        }

        if (pendingEmergencyPress)
        {
            double oldDeadline = emergencyDeadline;

            if (now <= oldDeadline)
            {
                pendingEmergencyPress = false;
                emergencyDeadline = 0d;

                LogEmergency(
                    "DoubleAllowedAfterImmediateOffering",
                    now,
                    oldDeadline);

                // RENEGADE_R2D_IMMEDIATE_OFFERING:
                // 1回目で成立済みの奉納は巻き戻さず、2回目だけ通常会議へ通す。
                return false;
            }

            pendingEmergencyPress = false;
            emergencyDeadline = 0d;

            LogEmergency(
                "LateSecondDiscarded",
                now,
                oldDeadline);

            return true;
        }

        emergencyGeneration++;
        pendingEmergencyPress = true;
        emergencyDeadline = now + EmergencyWindowSeconds;

        bool offeringWasReady = taskReady;

        // RENEGADE_R2D_IMMEDIATE_OFFERING
        if (offeringWasReady)
        {
            PerformOffering();
        }

        LogEmergency(
            offeringWasReady
                ? "FirstImmediateOffering"
                : "FirstBlockedNoOffering",
            now,
            emergencyDeadline);

        return true;
    }

    public override void OnFixedUpdate(PlayerControl player)
    {
        if (!AmongUsClient.Instance.AmHost)
            return;

        var rolePlayer = Player;

        if (player == null ||
            rolePlayer == null ||
            player.PlayerId != rolePlayer.PlayerId ||
            player.Data == null)
        {
            return;
        }

        if (player.Data.Disconnected || !player.IsAlive())
        {
            CancelPendingEmergency("Unavailable");

            if (awakeningTriggered && !awakeningExpired)
            {
                EndAwakeningR2A(
                    player,
                    "Unavailable");
            }

            return;
        }

        // RENEGADE_R2A_FIXED_UPDATE
        if (awakeningTriggered)
        {
            if (!awakeningExpired)
            {
                UpdateAwakeningR2A(player);

                // RENEGADE_R2D_CONTACT_KILL_TICK
                if (!awakeningExpired && player.IsAlive())
                {
                    TryAwakeningContactKillR2D(player);
                }
            }

            return;
        }

        if (!pendingEmergencyPress)
            return;

        double now = Now;

        if (MeetingHud.Instance != null || !GameStates.IsInTask)
        {
            CancelPendingEmergency("MeetingOrNotInTask");
            return;
        }

        // Deadlineちょうどの2回目入力は有効。
        // 単押し確定は strictly now > deadline。
        if (now <= emergencyDeadline)
            return;

        double oldDeadline = emergencyDeadline;

        pendingEmergencyPress = false;
        emergencyDeadline = 0d;

        // RENEGADE_R2D_IMMEDIATE_OFFERING_WINDOW_EXPIRE
        LogEmergency(
            "DoubleWindowExpired",
            now,
            oldDeadline);
    }

    private void TryAwakeningContactKillR2D(PlayerControl player)
    {
        if (!AmongUsClient.Instance.AmHost ||
            !GameStates.IsInTask ||
            MeetingHud.Instance != null ||
            !IsAwakeningActiveInternal() ||
            !player.CanMove)
        {
            return;
        }

        double now = Now;

        if (now < nextAwakeningContactKillAttemptAt)
            return;

        PlayerControl? target = null;
        float nearestDistance = float.MaxValue;

        foreach (var candidate in Main.AllAlivePlayerControls)
        {
            if (candidate == null ||
                candidate.Data == null ||
                candidate.Data.Disconnected ||
                candidate.PlayerId == player.PlayerId ||
                !candidate.IsAlive() ||
                !candidate.CanMove)
            {
                continue;
            }

            float distance = UnityEngine.Vector2.Distance(
                player.transform.position,
                candidate.transform.position);

            if (distance > AwakeningContactKillRange ||
                distance >= nearestDistance)
            {
                continue;
            }

            nearestDistance = distance;
            target = candidate;
        }

        if (target == null)
            return;

        nextAwakeningContactKillAttemptAt =
            now + AwakeningContactKillAttemptIntervalSeconds;

        Logger.Info(
            $"R2D contact kill attempt killer={player.PlayerId} target={target.PlayerId} distance={nearestDistance:0.000}",
            LogTag);

        CustomRoleManager.OnCheckMurder(
            player,
            target,
            player,
            target);
    }

    // RENEGADE_R2D_CONTACT_KILL_GATE_API
    public static bool CanUseAwakeningContactKill(PlayerControl player)
    {
        if (player == null || player.Data == null)
            return false;

        return player.GetRoleClass() is Renegade renegade &&
               renegade.IsAwakeningActiveInternal();
    }

    public override bool OnCompleteTask()
    {
        if (awakeningTriggered)
            return true;

        if (!taskReady)
        {
            taskReady = true;

            Logger.Info(
                $"TaskReady playerId={Player.PlayerId} offerings={completedOfferings}/{RequiredOfferingsSetting}",
                LogTag);

            Utils.NotifyRoles();
        }

        return true;
    }

    private void PerformOffering()
    {
        if (!AmongUsClient.Instance.AmHost ||
            awakeningTriggered ||
            !taskReady)
        {
            return;
        }

        taskReady = false;
        completedOfferings++;

        Logger.Info(
            $"OfferingCompleted playerId={Player.PlayerId} offerings={completedOfferings}/{RequiredOfferingsSetting}",
            LogTag);

        if (completedOfferings >= RequiredOfferingsSetting)
        {
            TriggerAwakeningCore();
            return;
        }

        AssignNextSingleTask();

        // RENEGADE_R2C_NO_PRIVATE_SYSTEM_MESSAGE
        // Progress is announced only after a real meeting via normal chat.
        Utils.NotifyRoles();
    }

    private void AssignNextSingleTask()
    {
        if (Player?.Data == null)
            return;

        // R1-BのRpcSetTasksPatchがこの空配列を検出し、
        // 新しいランダム実タスク1個へ置換する。
        Player.Data.RpcSetTasks(
            new Il2CppStructArray<byte>(0));

        MyTaskState.AllTasksCount = 1;
        MyTaskState.CompletedTasksCount = 0;
        MyTaskState.hasTasks = true;

        Logger.Info(
            $"NextTaskRequested playerId={Player.PlayerId} offerings={completedOfferings}/{RequiredOfferingsSetting}",
            LogTag);
    }

    /// <summary>
    /// R1では権威状態として覚醒到達だけを確定する。
    /// 覚醒演出・速度・Kill UI・タイマー・勝敗はR2で接続する。
    /// </summary>
    private void TriggerAwakeningCore()
    {
        var player = Player;

        if (player == null || player.Data == null)
        {
            Logger.Warn(
                "Awakening aborted because Player/Data is null.",
                LogTag);

            return;
        }

        awakeningTriggered = true;

        // RENEGADE_R2A_AWAKEN_START
        awakeningExpired = false;
        awakeningDeadline =
            Now + AwakenDurationSetting;
        lastAwakeningSecond = -1;

        pendingEmergencyPress = false;
        emergencyDeadline = 0d;
        taskReady = false;

        // R1-BのRpcSetTasksPatchではawakeningTriggered=trueなら
        // この空配列を空のまま通し、次タスクを与えない。
        player.Data.RpcSetTasks(
            new Il2CppStructArray<byte>(0));

        MyTaskState.AllTasksCount = 0;
        MyTaskState.CompletedTasksCount = 0;
        MyTaskState.hasTasks = false;

        ApplyAwakeningSpeed(player);

        // RENEGADE_R2D_NO_NATIVE_KILL_UI
        // Killing is host-authoritative contact logic. Keep vanilla role untouched.
        nextAwakeningContactKillAttemptAt = 0d;

        player.MarkDirtySettings();

        Logger.Info(
            $"AwakeningTriggered playerId={player.PlayerId} offerings={completedOfferings}/{RequiredOfferingsSetting} duration={AwakenDurationSetting} speed={AwakenSpeedSetting:0.0}",
            LogTag);

        // RENEGADE_R2C_NO_AWAKEN_SYSTEM_MESSAGE
        // Awakening is communicated through the unified in-world display.

        // 既存表示を一度確定してから、R2-A公開表示を最後に上書きする。
        Utils.NotifyRoles(NoCache: true);

        lastAwakeningSecond =
            GetRemainingAwakeningSeconds();

        PushUnifiedAwakeningDisplay(
            player,
            force: true,
            refreshPublicMark: true);
    }

    private void CancelPendingEmergency(string reason)
    {
        if (!pendingEmergencyPress)
            return;

        double oldDeadline = emergencyDeadline;

        pendingEmergencyPress = false;
        emergencyDeadline = 0d;

        LogEmergency(
            "Cancelled:" + reason,
            Now,
            oldDeadline);
    }

    // RENEGADE_R2A_AFTER_MEETING_PROGRESS
// RENEGADE_R2E_AFTER_MEETING_STALE_QUEUE_REMOVED
    private void QueueProgressNormalChatTail()
    {
        if (!AmongUsClient.Instance.AmHost)
            return;

        var carrier =
            Main.AllPlayerControls
                .Where(pc =>
                    pc != null &&
                    pc.Data != null &&
                    !pc.Data.Disconnected &&
                    !pc.Is(CustomRoles.Renegade))
                .OrderBy(pc => pc.PlayerId)
                .FirstOrDefault()
            ?? PlayerControl.LocalPlayer;

        if (carrier == null || carrier.Data == null)
            return;

        string text =
            $"奉納完了：{completedOfferings} / {RequiredOfferingsSetting}";

        const string title = "レネゲイド 進捗";

        foreach (var sendTo in Main.AllPlayerControls)
        {
            if (sendTo == null ||
                sendTo.Data == null ||
                sendTo.Data.Disconnected)
            {
                continue;
            }

            // RENEGADE_R2E_PROGRESS_MEETING_TAIL:
            // This helper is called at +5s of the CURRENT meeting. At that time
            // role-info/system queues have already been given priority and
            // SuffixMessagesToSend preserves NormalMessage transport.
            Main.SuffixMessagesToSend.Add((
                text,
                sendTo.PlayerId,
                title,
                carrier));
        }

        Logger.Info(
            $"R2E progress meeting-tail queued offerings={completedOfferings}/{RequiredOfferingsSetting} carrier={carrier.PlayerId} meeting={MeetingHud.Instance != null}",
            LogTag);
    }

    public override bool OnReportDeadBody(
        PlayerControl reporter,
        NetworkedPlayerInfo target)
    {
        CancelPendingEmergency(
            "AcceptedReportOrMeeting");

        return true;
    }

    public override void OnStartMeeting()
    {
        CancelPendingEmergency(
            "MeetingStarted");

        var player = Player;

        if (!AmongUsClient.Instance.AmHost ||
            player == null ||
            player.Data == null ||
            player.Data.Disconnected ||
            !player.IsAlive() ||
            awakeningTriggered)
        {
            return;
        }

        // RENEGADE_R2E_PROGRESS_MEETING_TAIL
        // SetName To Chat releases ChatUpdatePatch.DoBlockChat at 3 seconds.
        // EvilHacker Admin Message finishes at 4 seconds.
        // Queue at 5 seconds while the current MeetingHud/ChatController is
        // still active, so the current offering count is sent in THIS meeting
        // and cannot leak forward as a stale message into the next meeting.
        _ = new LateTask(
            () =>
            {
                var currentPlayer = Player;

                if (!AmongUsClient.Instance.AmHost ||
                    MeetingHud.Instance == null ||
                    currentPlayer == null ||
                    currentPlayer.Data == null ||
                    currentPlayer.Data.Disconnected ||
                    !currentPlayer.IsAlive() ||
                    awakeningTriggered)
                {
                    Logger.Info(
                        $"R2E progress meeting-tail skipped offerings={completedOfferings}/{RequiredOfferingsSetting} meeting={MeetingHud.Instance != null}",
                        LogTag);

                    return;
                }

                QueueProgressNormalChatTail();
            },
            5.0f,
            "Renegade Progress Normal Chat Meeting Tail R2E");
    }

    public override bool OnEnterVent(
        PlayerPhysics physics,
        int ventId)
        => false;

    public override bool OnInvokeSabotage(
        SystemTypes systemType)
        => false;

    public override bool OnCheckMurderAsTarget(
        MurderInfo info)
    {
        // R1はCustomRoleManager通常キル経路のみ保護。
        // 特殊/強制キルを含む完全無敵化はR2で監査して追加する。
        if (IsAwakeningActiveInternal() &&
            info.AttemptKiller != null &&
            info.AttemptKiller.PlayerId != Player.PlayerId)
        {
            info.CanKill = false;

            Logger.Info(
                $"NormalKillBlocked killer={info.AttemptKiller.PlayerId} target={Player.PlayerId}",
                LogTag);
        }

        return true;
    }

    // RENEGADE_R2D_WARNING_PIPELINE
    public static string GetAwakeningWarningLowerTextOthers(
        PlayerControl seer,
        PlayerControl? seen = null,
        bool isForMeeting = false,
        bool isForHud = false)
    {
        if (isForMeeting || seer == null)
            return string.Empty;

        seen ??= seer;

        if (seen == null || seen.PlayerId != seer.PlayerId)
            return string.Empty;

        if (!IsAwakeningActive())
            return string.Empty;

        // RENEGADE_R2D_SELF_DISPLAY_NO_STACK
        // Other viewers keep the persistent warning. The awakened player's
        // own name is reserved for BuildSelfAwakeningName so its countdown
        // stays readable.
        if (CustomRoleManager.AllActiveRoles.TryGetValue(
                seer.PlayerId,
                out var seerRole) &&
            seerRole is Renegade selfRenegade &&
            selfRenegade.IsAwakeningActiveInternal())
        {
            return string.Empty;
        }

        return Utils.ColorString(
            RoleInfo.RoleColor,
            "レネゲイド覚醒中　逃げろ！\r\n緊急会議・死体通報不可");
    }

    public override string GetProgressText(
        bool comms = false)
    {
        if (IsAwakeningActiveInternal())
        {
            int remaining =
                GetRemainingAwakeningSeconds();

            return Utils.ColorString(
                RoleInfo.RoleColor,
                $" [! 覚醒・残り {remaining}秒]");
        }

        if (awakeningTriggered && awakeningExpired)
        {
            return Utils.ColorString(
                RoleInfo.RoleColor,
                " [! 覚醒終了]");
        }

        string ready =
            taskReady
                ? "・奉納可能"
                : string.Empty;

        return Utils.ColorString(
            RoleInfo.RoleColor,
            $" [奉納 {completedOfferings}/{RequiredOfferingsSetting}{ready}]");
    }

    // ============================================================
    // RENEGADE_R2A_RUNTIME
    // ============================================================

    // ============================================================
    // RENEGADE_R2B_IMMUNITY_AND_KILL_UI
    // ============================================================

    private static bool IsTimeoutDeathBypass(byte playerId)
        => TimeoutDeathBypass.Contains(playerId);

    public static bool ShouldBlockPlayerCausedMurder(
        PlayerControl killer,
        PlayerControl target,
        string stage)
    {
        if (!AmongUsClient.Instance.AmHost || target == null)
            return false;

        if (IsTimeoutDeathBypass(target.PlayerId))
            return false;

        if (!CustomRoleManager.AllActiveRoles.TryGetValue(
                target.PlayerId,
                out var role) ||
            role is not Renegade renegade ||
            !renegade.IsAwakeningActiveInternal())
        {
            return false;
        }

        Logger.Info(
            $"R2B awakened immunity blocked stage={stage} " +
            $"killer={(killer == null ? byte.MaxValue : killer.PlayerId)} " +
            $"target={target.PlayerId}",
            LogTag);

        return true;
    }

    public static bool ShouldBlockForcedExile(
        PlayerControl target,
        string stage)
    {
        if (!AmongUsClient.Instance.AmHost || target == null)
            return false;

        if (IsTimeoutDeathBypass(target.PlayerId))
            return false;

        if (!CustomRoleManager.AllActiveRoles.TryGetValue(
                target.PlayerId,
                out var role) ||
            role is not Renegade renegade ||
            !renegade.IsAwakeningActiveInternal())
        {
            return false;
        }

        Logger.Info(
            $"R2B awakened exile blocked stage={stage} target={target.PlayerId}",
            LogTag);

        return true;
    }

    public static bool ShouldBlockDeathStateCommit(
        PlayerControl target,
        CustomDeathReason reason,
        string stage)
    {
        if (!AmongUsClient.Instance.AmHost || target == null)
            return false;

        if (IsTimeoutDeathBypass(target.PlayerId))
            return false;

        if (!CustomRoleManager.AllActiveRoles.TryGetValue(
                target.PlayerId,
                out var role) ||
            role is not Renegade renegade ||
            !renegade.IsAwakeningActiveInternal())
        {
            return false;
        }

        bool playerCaused = reason is
            CustomDeathReason.Kill or
            CustomDeathReason.Suicide or
            CustomDeathReason.Spell or
            CustomDeathReason.FollowingSuicide or
            CustomDeathReason.Bite or
            CustomDeathReason.Bombed or
            CustomDeathReason.Misfire or
            CustomDeathReason.Torched or
            CustomDeathReason.Sniped or
            CustomDeathReason.Revenge or
            CustomDeathReason.Execution or
            CustomDeathReason.Poisoning or
            CustomDeathReason.Gambled or
            CustomDeathReason.Infected or
            CustomDeathReason.Beam or
            CustomDeathReason.Vote;

        if (!playerCaused)
            return false;

        Logger.Info(
            $"R2B death-state commit blocked stage={stage} " +
            $"target={target.PlayerId} reason={reason}",
            LogTag);

        return true;
    }

    private static RoleTypes GetViewerRestoreRole(
        PlayerControl viewer,
        PlayerControl target)
    {
        if (viewer != null &&
            target != null &&
            global::TownOfHostForE.SelectRolesPatch.TryGetExileDisplayRoleViewPermanent(
                viewer.PlayerId,
                target.PlayerId,
                out var mappedRole))
        {
            return mappedRole;
        }

        return target?.Data?.Role?.Role ?? RoleTypes.Crewmate;
    }

    private void ApplyAwakeningKillUi(PlayerControl player)
    {
        if (awakeningKillUiApplied ||
            player == null ||
            player.Data == null ||
            player.Data.Disconnected)
        {
            return;
        }

        int clientId = player.GetClientId();

        if (clientId < 0)
        {
            Logger.Warn(
                $"R2B kill UI clientId missing playerId={player.PlayerId}",
                LogTag);
            return;
        }

        if (player.AmOwner)
        {
            // Owner path: do not mutate every other authoritative native role.
            // RENEGADE_R2C_KILL_UI_CANOVERRIDE
            player.RpcSetRoleDesync(RoleTypes.Impostor, clientId, true);
        }
        else
        {
            // Existing TOH4E desync-role principle:
            // self = Impostor, every other target = Scientist for this viewer.
            // This prevents a Vanilla/remote MOD Renegade from learning real
            // Impostor teammates through native red-name/team UI.
            foreach (var target in Main.AllPlayerControls)
            {
                if (target == null ||
                    target.Data == null ||
                    target.Data.Disconnected)
                {
                    continue;
                }

                var viewerRole =
                    target.PlayerId == player.PlayerId
                        ? RoleTypes.Impostor
                        : RoleTypes.Scientist;

                target.RpcSetRoleDesync(viewerRole, clientId, true);
            }
        }

        awakeningKillUiApplied = true;

        Logger.Info(
            $"R2B kill UI applied playerId={player.PlayerId} clientId={clientId}",
            LogTag);
    }

    private void RestoreAwakeningKillUi(PlayerControl player)
    {
        if (!awakeningKillUiApplied ||
            player == null ||
            player.Data == null)
        {
            return;
        }

        int clientId = player.GetClientId();

        if (clientId >= 0 && !player.Data.Disconnected)
        {
            if (player.AmOwner)
            {
                var selfRestoreRole = GetViewerRestoreRole(player, player);
                player.RpcSetRoleDesync(selfRestoreRole, clientId, true);
            }
            else
            {
                foreach (var target in Main.AllPlayerControls)
                {
                    if (target == null ||
                        target.Data == null ||
                        target.Data.Disconnected)
                    {
                        continue;
                    }

                    var restoreRole = GetViewerRestoreRole(player, target);
                    target.RpcSetRoleDesync(restoreRole, clientId, true);
                }
            }
        }

        awakeningKillUiApplied = false;

        Logger.Info(
            $"R2B kill UI restored playerId={player.PlayerId} clientId={clientId}",
            LogTag);
    }

    private void ExecuteTimeoutDeathR2B(PlayerControl player)
    {
        // RENEGADE_R2B_NULL_GUARD_FIX
        if (player == null)
        {
            awakeningExpired = true;

            Logger.Warn(
                "R2B timeout death aborted because player is null.",
                LogTag);

            return;
        }

        if (player.Data == null ||
            player.Data.Disconnected ||
            !player.IsAlive())
        {
            EndAwakeningR2A(player, "TimeoutUnavailable");
            return;
        }

        byte playerId = player.PlayerId;
        TimeoutDeathBypass.Add(playerId);

        try
        {
            Logger.Info(
                $"R2B timeout death begin playerId={playerId}",
                LogTag);

            RestoreAwakeningSpeed(player);
            RestoreAwakeningKillUi(player);

            var state = PlayerState.GetByPlayerId(playerId);
            if (state != null)
                state.DeathReason = CustomDeathReason.Suicide;

            player.RpcMurderPlayer(player, true);
        }
        finally
        {
            TimeoutDeathBypass.Remove(playerId);
            awakeningExpired = true;
            RestoreUnifiedAwakeningDisplay(player);

            Logger.Info(
                $"R2B timeout death end playerId={playerId} alive={player.IsAlive()}",
                LogTag);
        }
    }

    private bool IsAwakeningActiveInternal()
    {
        var player = Player;

        return
            awakeningTriggered &&
            !awakeningExpired &&
            player != null &&
            player.Data != null &&
            !player.Data.Disconnected &&
            player.IsAlive();
    }

    public static bool IsAwakeningActive()
    {
        foreach (var role in CustomRoleManager.AllActiveRoles.Values)
        {
            if (role is Renegade renegade &&
                renegade.IsAwakeningActiveInternal())
            {
                return true;
            }
        }

        return false;
    }

    // RENEGADE_R2G_AWAKENING_PLAYER_AUTHORITY
    public static bool IsAwakeningPlayer(PlayerControl player)
    {
        if (player == null)
            return false;

        return
            CustomRoleManager.AllActiveRoles.TryGetValue(
                player.PlayerId,
                out var role) &&
            role is Renegade renegade &&
            renegade.IsAwakeningActiveInternal();
    }

    private int GetRemainingAwakeningSeconds()
    {
        if (!awakeningTriggered || awakeningExpired)
            return 0;

        return Mathf.Max(
            0,
            Mathf.CeilToInt(
                (float)(awakeningDeadline - Now)));
    }

    private void ApplyAwakeningSpeed(
        PlayerControl player)
    {
        if (awakeningSpeedApplied)
            return;

        if (!Main.AllPlayerSpeed.TryGetValue(
                player.PlayerId,
                out var currentSpeed))
        {
            Logger.Warn(
                $"R2A speed base missing playerId={player.PlayerId}",
                LogTag);

            return;
        }

        float multiplier =
            Mathf.Max(
                0.01f,
                AwakenSpeedSetting);

        // 既存速度補正を上書きせず、覚醒開始時点速度に対する差分だけを加算。
        awakeningSpeedBonus =
            currentSpeed * (multiplier - 1f);

        Main.AllPlayerSpeed[player.PlayerId] +=
            awakeningSpeedBonus;

        awakeningSpeedApplied = true;

        player.MarkDirtySettings();

        Logger.Info(
            $"R2A speed applied playerId={player.PlayerId} " +
            $"base={currentSpeed:0.###} " +
            $"multiplier={multiplier:0.###} " +
            $"bonus={awakeningSpeedBonus:0.###}",
            LogTag);
    }

    private void RestoreAwakeningSpeed(
        PlayerControl player)
    {
        if (!awakeningSpeedApplied)
            return;

        if (Main.AllPlayerSpeed.ContainsKey(player.PlayerId))
        {
            Main.AllPlayerSpeed[player.PlayerId] -=
                awakeningSpeedBonus;

            player.MarkDirtySettings();
        }

        awakeningSpeedApplied = false;
        awakeningSpeedBonus = 0f;
    }

    private static string GetAwakeningPulseColor(
        int remaining)
    {
        return (remaining & 1) == 0
            ? "#ff3030"
            : "#ffffff";
    }

    // RENEGADE_R2C_PERSISTENT_PUBLIC_WARNING
    // RENEGADE_R2F_FACE_KAOMOJI_MARKER
    // A/B v1: render a face on the player-name TextMeshPro near the visor.
    // Its precise height and glyph support must be verified on real Vanilla.
    // Reuse the existing countdown SetName RPC; do not add a separate RPC.
    // Do not assume Vanilla glyph support until screenshots confirm it.
    private static string BuildPublicAwakeningName(
        string playerName,
        int remaining)
    {
        string pulseColor =
            GetAwakeningPulseColor(remaining);

        string markerSize =
            remaining % 2 == 0
                ? "220%"
                : "185%";

        return
            "<color=#b00020><size=115%>!! レネゲイド【覚醒】 !!</size></color>\r\n" +
            $"<color={pulseColor}><size=70%>!! 残り {remaining}秒　逃げろ！\r\n" +
            "!! 緊急会議・死体通報不可</size></color>\r\n" +
            playerName +
            // R2-F face-position A/B v1: place the face underneath the name
            // using TextMeshPro line height, without altering the world object.
            $"<line-height=80%>\r\n<color=#b00020><size={markerSize}><b>(▼皿▼)</b></size></color></line-height>";
    }
    // RENEGADE_R2D_SELF_DISPLAY_CLEAR_NAME
    // The awakened player gets a compact self-only payload with no normal
    // player-name/color line, so the remaining time cannot be covered by it.
    private static string BuildSelfAwakeningName(
        int remaining)
    {
        string pulseColor =
            GetAwakeningPulseColor(remaining);

        return
            "<color=#b00020><size=115%>レネゲイド【覚醒】</size></color>\r\n" +
            $"<color={pulseColor}><size=100%>残り {remaining}秒</size></color>";
    }

    private void PushUnifiedAwakeningDisplay(
        PlayerControl player,
        bool force,
        bool refreshPublicMark)
    {
        if (player == null ||
            player.Data == null ||
            !IsAwakeningActiveInternal())
        {
            return;
        }

        int remaining =
            GetRemainingAwakeningSeconds();

        string publicName =
            BuildPublicAwakeningName(
                player.Data.PlayerName,
                remaining);

        string selfName =
            BuildSelfAwakeningName(
                remaining);

        foreach (var viewer in Main.AllPlayerControls)
        {
            if (viewer == null ||
                viewer.Data == null ||
                viewer.Data.Disconnected)
            {
                continue;
            }

            bool viewerIsAwakenedRenegade =
                viewer.PlayerId == player.PlayerId;

            string viewerPayload =
                viewerIsAwakenedRenegade
                    ? selfName
                    : publicName;

            // RENEGADE_R2D_SELF_DISPLAY_CLEAR_NAME:
            // Host-local and remote Vanilla/MOD viewers receive the same
            // viewer-specific payload. Only the awakened player's own view
            // suppresses the normal name/color line.
            if (viewer.AmOwner)
            {
                player.cosmetics.nameText.text =
                    viewerPayload;

                continue;
            }

            TownOfHostForE.ExtendedPlayerControl.RpcSetNamePrivate(
                player,
                viewerPayload,
                false,
                viewer,
                true);
        }
    }

    private void RestoreUnifiedAwakeningDisplay(
        PlayerControl player)
    {
        foreach (var viewer in Main.AllPlayerControls)
        {
            if (viewer == null ||
                viewer.Data == null ||
                viewer.Data.Disconnected)
            {
                continue;
            }

            string viewerBaseName =
                viewer.Data.PlayerName;

            if (viewer.AmOwner)
            {
                viewer.cosmetics.nameText.text =
                    viewerBaseName;

                if (player != null &&
                    player.Data != null &&
                    viewer.PlayerId != player.PlayerId)
                {
                    player.cosmetics.nameText.text =
                        player.Data.PlayerName;
                }

                continue;
            }

            TownOfHostForE.ExtendedPlayerControl.RpcSetNamePrivate(
                viewer,
                viewerBaseName,
                false,
                viewer,
                true);

            if (player != null &&
                player.Data != null &&
                viewer.PlayerId != player.PlayerId)
            {
                TownOfHostForE.ExtendedPlayerControl.RpcSetNamePrivate(
                    player,
                    player.Data.PlayerName,
                    false,
                    viewer,
                    true);
            }
        }

        // Vanilla側の通常役職表示を復元。
        // MODクライアントは自身の通常MOD表示へ戻る。
        Utils.NotifyRoles(NoCache: true);
    }

    private void UpdateAwakeningR2A(
        PlayerControl player)
    {
        double now = Now;

        if (now >= awakeningDeadline)
        {
            // RENEGADE_R2B_TIMEOUT_DEATH
            ExecuteTimeoutDeathR2B(player);
            return;
        }

        int remaining =
            GetRemainingAwakeningSeconds();

        if (remaining == lastAwakeningSecond)
            return;

        lastAwakeningSecond = remaining;

        bool refreshPublicMark =
            remaining % PublicMarkRefreshIntervalSeconds == 0;

        PushUnifiedAwakeningDisplay(
            player,
            force: false,
            refreshPublicMark: refreshPublicMark);
    }

    private void EndAwakeningR2A(
        PlayerControl player,
        string reason)
    {
        if (awakeningExpired)
            return;

        awakeningExpired = true;

        RestoreAwakeningSpeed(player);
        RestoreAwakeningKillUi(player);
        RestoreUnifiedAwakeningDisplay(player);

        Logger.Info(
            $"R2A awakening ended playerId={player.PlayerId} reason={reason}",
            LogTag);
    }

    // RENEGADE_R2D_NO_KILL_BUTTON
    bool IKiller.CanKill =>
        false;

    bool IKiller.IsKiller =>
        IsAwakeningActiveInternal();

    public bool CanUseKillButton()
        => false;

    public float CalculateKillCooldown()
        => IsAwakeningActiveInternal() ? 0f : 300f;

    public bool CanUseSabotageButton()
        => false;

    bool IKiller.CanUseImpostorVentButton()
        => false;

    private void LogEmergency(
        string eventName,
        double now,
        double eventDeadline)
    {
        Logger.Info(
            $"event={eventName} " +
            $"playerId={Player.PlayerId} " +
            $"generation={emergencyGeneration} " +
            $"hostTime={now:F3} " +
            $"deadline={eventDeadline:F3} " +
            $"ready={taskReady} " +
            $"offerings={completedOfferings}/{RequiredOfferingsSetting} " +
            $"usedButtonCount={Options.UsedButtonCount} " +
            $"inTask={GameStates.IsInTask} " +
            $"meeting={(MeetingHud.Instance != null)}",
            LogTag);
    }

    /// <summary>
    /// /m・/myrole用。
    /// R1-Bでホスト/非ホスト両経路から直接使用する。
    /// </summary>
    public static string GetRoleDescription(
        bool infoLong)
    {
        int offerings =
            RequiredOfferingsSetting;

        int duration =
            AwakenDurationSetting;

        float speed =
            AwakenSpeedSetting;

        if (!infoLong)
        {
            return
                $"タスクを{offerings}回奉納して覚醒せよ";
        }

        return
            "[第三陣営]\n\n" +
            "1つずつ与えられるタスクを完了し、緊急会議ボタンを1回押すことで「奉納」を行います。\n" +
            "奉納が完了すると新しいタスクが1つ与えられます。\n\n" +
            $"奉納を{offerings}回完了すると覚醒します。\n\n" +
            "■ 緊急会議ボタン\n" +
            "・1回押し：タスク完了済みなら奉納\n" +
            "・3秒以内に2回押し：通常の緊急会議\n\n" +
            "■ 覚醒後\n" +
            $"・覚醒時間：{duration}秒\n" +
            $"・覚醒中の移動速度：{speed:0.0}倍\n" +
            "・至近距離（ウルトラスター同等の接触範囲）に入った他プレイヤーを自動でキルします（キルボタン不要）\n" +
            "・プレイヤーから受けるキルでは死亡しません\n" +
            "・緊急会議、死体通報、ベント、サボタージュは使用できません\n" +
            "・制限時間内に勝利条件を達成できなかった場合は死亡します\n\n" +
            "覚醒すると全員にレネゲイドの存在が公開され、警告表示と画面点滅が発生します。\n\n" +
            "※レネゲイドに与えられるタスクはクルーのタスクゲージやタスク勝利には影響しません。";
    }
}