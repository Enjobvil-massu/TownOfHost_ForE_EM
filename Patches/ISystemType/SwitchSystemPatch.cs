using HarmonyLib;
using Hazel;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Interfaces;
using TownOfHostForE.Roles.Neutral;
using UnityEngine;

namespace TownOfHostForE.Patches.ISystemType;

[HarmonyPatch(typeof(SwitchSystem), nameof(SwitchSystem.UpdateSystem))]
public static class SwitchSystemUpdateSystemPatch
{
    public static bool Prefix(SwitchSystem __instance, [HarmonyArgument(0)] PlayerControl player, [HarmonyArgument(1)] MessageReader msgReader)
    {
        byte amount;
        {
            var newReader = MessageReader.Get(msgReader);
            amount = newReader.ReadByte();
            newReader.Recycle();
        }

        if (!AmongUsClient.Instance.AmHost)
        {
            return true;
        }
        // 停電サボタージュが鳴らされた場合は関係なし(ホスト名義で飛んでくるため誤爆注意)
        if (amount.HasBit(SwitchSystem.DamageSystem))
        {
            return true;
        }
        var isMadmate =
            player.Is(CustomRoleTypes.Madmate) ||
            // マッド属性化時に削除
            (player.GetRoleClass() is SchrodingerCat schrodingerCat && schrodingerCat.AmMadmate);
        if (isMadmate && !Options.MadmateCanFixLightsOut.GetBool())
        {
            // 直せないならキャンセル
            return false;
        }
        //Airshipの特定の停電を直せないならキャンセル
        if ((MapNames)Main.NormalOptions.MapId == MapNames.Airship)
        {
            var truePosition = player.GetTruePosition();
            if (Options.DisableAirshipViewingDeckLightsPanel.GetBool() && Vector2.Distance(truePosition, new(-12.93f, -11.28f)) <= 2f) return false;
            if (Options.DisableAirshipGapRoomLightsPanel.GetBool() && Vector2.Distance(truePosition, new(13.92f, 6.43f)) <= 2f) return false;
            if (Options.DisableAirshipCargoLightsPanel.GetBool() && Vector2.Distance(truePosition, new(30.56f, 2.12f)) <= 2f) return false;
        }

        // サボタージュによる破壊ではない && 配電盤を下げられなくするオプションがオン
        if (Options.BlockDisturbancesToSwitches.GetBool())
        {
            // amount分だけ1を左にずらす
            // 各桁が各ツマミに対応する
            // 一番左のツマミが操作されたら(amount: 0) 00001
            // 一番右のツマミが操作されたら(amount: 4) 10000
            // ref: SwitchSystem.RepairDamage, SwitchMinigame.FixedUpdate
            var switchedKnob = (byte)(0b_00001 << amount);
            // ExpectedSwitches: すべてONになっているときのスイッチの上下状態
            // ActualSwitches: 実際のスイッチの上下状態
            // 操作されたツマミについて，ExpectedとActualで同じならそのツマミは既に直ってる
            if ((__instance.ActualSwitches & switchedKnob) == (__instance.ExpectedSwitches & switchedKnob))
            {
                return false;
            }
        }

        // ロールの処理
        if (player.GetRoleClass() is ISystemTypeUpdateHook systemTypeUpdateHook && !systemTypeUpdateHook.UpdateSwitchSystem(__instance, amount))
        {
            return false;
        }
        return true;
    }
}

// SHARED_MOVING_DECOY_LOCAL_READY_STRONG_PRIVACY_AB_V1_FAILCLOSED_CALLBACK_OWNERSHIP:
// Keep retry10's targeted high-bit Electrical UpdateSystem transport and one shared moving
// fake trajectory. Initial and moving fakes use the same Expected-based invariant, while each
// visible move retains both repair-index ambiguity and both authoritative toggle directions.
// No role spoof, task mutation, death-state spoof, global sabotage mutation, or new packet.
[HarmonyPatch(typeof(SwitchSystem), nameof(SwitchSystem.UpdateSystem))]
public static class ForbiddenSabotageElectricalInfoMaskABV1Retry10FastRepairReassert
{
    private const string LogTag =
        "FORBIDDEN_SABOTAGE_ELECTRICAL_INFO_MASK_AB_V1_RETRY10_FAST_REPAIR_REASSERT";

    private const byte SwitchMask = 0b_11111;
    private const int SwitchCount = 5;
    private const float SabotageStartReassertDelaySeconds = 0.25f;

    private const string ReceiveProbeTag =
        "FORBIDDEN_SABOTAGE_ELECTRICAL_RECEIVE_PROBE_AB_V1";

    private const string TimelineProbeTag =
        "FORBIDDEN_SABOTAGE_ELECTRICAL_TIMELINE_PROBE_AB_V1";

    private static int ReassertGeneration;
    private static int SharedTrajectoryGeneration;
    private static int RemotePublishedGeneration = -1;
    private static int LocalFakeReadyGeneration = -1;
    private static bool SharedTrajectoryInitialized;
    private static bool RemoteFakePublished;
    private static bool LocalFakeReady;
    private static byte SharedFakeActual;
    private static byte SharedExpected;
    private static byte SharedRealPrevious;
    private static SwitchSystem? SharedSwitchSystem;
    private static int RepairOuterBarrierGeneration;
    private static int RepairOuterBarrierArmedGeneration = -1;
    private static int RepairOuterBarrierTrajectoryGeneration = -1;
    private static bool RepairOuterBarrierArmed;
    private static bool RepairOuterBarrierSerializeObserved;
    private const long NoOuterInvocationToken = 0;
    private static long OuterInvocationSequence;
    [System.ThreadStatic]
    private static long ActiveOuterInvocationToken;
    [System.ThreadStatic]
    private static bool ActiveOuterInvocationHadArmedBarrier;
    [System.ThreadStatic]
    private static int ActiveOuterInvocationBarrierGeneration;
    [System.ThreadStatic]
    private static int ActiveOuterInvocationTrajectoryGeneration;
    private static long RepairOuterBarrierObservedInvocationToken;
    private static int RepairOuterBarrierObservedGeneration = -1;
    private static int RepairOuterBarrierObservedTrajectoryGeneration = -1;
    private static byte RepairOuterBarrierExpected;
    private static byte RepairOuterBarrierAuthoritativeActual;
    private static SwitchSystem? RepairOuterBarrierSwitchSystem;

    public readonly struct UpdateState
    {
        public UpdateState(byte amount, byte actualBefore)
        {
            Amount = amount;
            ActualBefore = actualBefore;
        }

        public byte Amount { get; }
        public byte ActualBefore { get; }
    }

    private sealed class ViewerMaskState
    {
        public ViewerMaskState(PlayerControl target)
        {
            Target = target;
        }

        public PlayerControl Target { get; }
    }

    private static readonly System.Collections.Generic.Dictionary<byte, ViewerMaskState>
        MaskedViewers = new();

    [HarmonyPriority(Priority.First)]
    public static void Prefix(
        SwitchSystem __instance,
        [HarmonyArgument(1)] MessageReader msgReader,
        out UpdateState __state)
    {
        byte amount = 0;
        var reader = MessageReader.Get(msgReader);
        try
        {
            amount = reader.ReadByte();
        }
        finally
        {
            reader.Recycle();
        }

        __state = new UpdateState(amount, __instance.ActualSwitches);

        if (
            AmongUsClient.Instance != null &&
            !AmongUsClient.Instance.AmHost &&
            amount.HasBit(SwitchSystem.DamageSystem))
        {
            var localPlayer = PlayerControl.LocalPlayer;
            Logger.Info(
                $"{ReceiveProbeTag} Stage=BEFORE Amount={amount} " +
                $"LocalPlayerId={(localPlayer == null ? -1 : localPlayer.PlayerId)} " +
                $"Expected={__instance.ExpectedSwitches & SwitchMask} " +
                $"Actual={__instance.ActualSwitches & SwitchMask} " +
                $"Active={__instance.IsActive}",
                ReceiveProbeTag);
        }
    }

    [HarmonyPriority(Priority.Last)]
    public static void Postfix(
        SwitchSystem __instance,
        [HarmonyArgument(0)] PlayerControl player,
        UpdateState __state)
    {
        if (AmongUsClient.Instance == null)
        {
            return;
        }

        if (!AmongUsClient.Instance.AmHost)
        {
            if (__state.Amount.HasBit(SwitchSystem.DamageSystem))
            {
                var localPlayer = PlayerControl.LocalPlayer;
                Logger.Info(
                    $"{ReceiveProbeTag} Stage=AFTER Amount={__state.Amount} " +
                    $"RpcPlayerId={(player == null ? -1 : player.PlayerId)} " +
                    $"LocalPlayerId={(localPlayer == null ? -1 : localPlayer.PlayerId)} " +
                    $"Expected={__instance.ExpectedSwitches & SwitchMask} " +
                    $"Actual={__instance.ActualSwitches & SwitchMask} " +
                    $"Active={__instance.IsActive}",
                    ReceiveProbeTag);

                var timelineDelays =
                    new float[]
                    {
                        0.10f,
                        0.25f,
                        0.50f,
                        1.00f,
                        2.00f
                    };

                foreach (var timelineDelay in timelineDelays)
                {
                    var capturedDelay = timelineDelay;

                    new LateTask(
                        () =>
                        {
                            var delayedLocalPlayer =
                                PlayerControl.LocalPlayer;

                            Logger.Info(
                                $"{TimelineProbeTag} Stage=SAMPLE " +
                                $"Delay={capturedDelay} Amount={__state.Amount} " +
                                $"LocalPlayerId={(delayedLocalPlayer == null ? -1 : delayedLocalPlayer.PlayerId)} " +
                                $"Expected={__instance.ExpectedSwitches & SwitchMask} " +
                                $"Actual={__instance.ActualSwitches & SwitchMask} " +
                                $"Active={__instance.IsActive}",
                                TimelineProbeTag);
                        },
                        capturedDelay,
                        TimelineProbeTag);
                }
            }

            return;
        }

        var amount = __state.Amount;
        var actualChanged = __instance.ActualSwitches != __state.ActualBefore;

        if (amount.HasBit(SwitchSystem.DamageSystem))
        {
            if (!actualChanged || !__instance.IsActive)
            {
                return;
            }

            StartMaskForForbiddenRemoteViewers(__instance, amount);
            return;
        }

        if (!actualChanged || !SharedTrajectoryInitialized)
        {
            return;
        }

        if (!IsStoredSwitchSystem(__instance))
        {
            FailCurrentTrajectoryInvariant(
                "UPDATE_SYSTEM",
                "STORED_SYSTEM_MISMATCH",
                __instance);
            return;
        }

        if (amount >= 5)
        {
            Logger.Warn(
                $"{LogTag} Stage=UNEXPECTED_REPAIR_AMOUNT Amount={amount} Active={__instance.IsActive}",
                LogTag);
            return;
        }

        if (__instance.IsActive)
        {
            var expectedBarrierGeneration =
                RepairOuterBarrierGeneration;
            var expectedTrajectoryGeneration =
                SharedTrajectoryGeneration;

            if (!TryAdvanceSharedTrajectory(__instance, amount))
            {
                return;
            }

            ArmRepairOuterBarrier(
                __instance,
                expectedBarrierGeneration,
                expectedTrajectoryGeneration);
            return;
        }

        var maskedViewerCount = MaskedViewers.Count;
        InvalidateAllMaskState();
        Logger.Info(
            $"{LogTag} Stage=RESTORE_ON_COMPLETE " +
            $"FinalRepairIndex={amount} PendingGeneration={ReassertGeneration} " +
            $"MaskedViewerCount={maskedViewerCount}",
            LogTag);
    }

    private static void StartMaskForForbiddenRemoteViewers(
        SwitchSystem switchSystem,
        byte damageAmount)
    {
        var staleViewerCount = MaskedViewers.Count;
        InvalidateAllMaskState();

        if (staleViewerCount > 0)
        {
            Logger.Warn(
                $"{LogTag} Stage=STALE_MASK_CLEARED Count={staleViewerCount}",
                LogTag);
        }

        var expected = (byte)(switchSystem.ExpectedSwitches & SwitchMask);
        var realDifference = (byte)(damageAmount & SwitchMask);
        var realActual = (byte)(switchSystem.ActualSwitches & SwitchMask);
        var hasForbiddenViewer = false;

        foreach (var target in Main.AllPlayerControls)
        {
            if (!ShouldMaskViewer(target))
            {
                continue;
            }

            hasForbiddenViewer = true;

            if (target.AmOwner)
            {
                Logger.Info(
                    $"{LogTag} Stage=LOCAL_OWNER_SKIPPED Viewer={target.PlayerId} " +
                    $"Role={target.GetCustomRole()}",
                    LogTag);
                continue;
            }

            MaskedViewers[target.PlayerId] = new ViewerMaskState(target);

            Logger.Info(
                $"{LogTag} Stage=MASK_PLAN Viewer={target.PlayerId} " +
                $"ClientId={target.GetClientId()} Role={target.GetCustomRole()} " +
                $"Expected={expected} RealActual={realActual} RealDifference={realDifference}",
                LogTag);
        }

        if (!hasForbiddenViewer)
        {
            return;
        }

        if (!TrySelectInitialSharedFake(
            realActual,
            expected,
            out var fakeActual,
            out var expectedDistance,
            out var candidateCount))
        {
            FailSharedTrajectory(
                switchSystem,
                -1,
                realActual,
                realActual,
                expected,
                0,
                candidateCount,
                candidateCount == 0
                    ? "INITIAL_SAFE_CANDIDATE_MISSING"
                    : "INITIAL_RANDOM_SELECTION_FAILED");
            return;
        }

        SharedTrajectoryInitialized = true;
        RemoteFakePublished = false;
        RemotePublishedGeneration = -1;
        SharedFakeActual = fakeActual;
        SharedExpected = expected;
        SharedRealPrevious = realActual;
        SharedSwitchSystem = switchSystem;
        LocalFakeReady = true;
        LocalFakeReadyGeneration = SharedTrajectoryGeneration;

        Logger.Info(
            $"{LogTag} Stage=SHARED_DECOY_INIT " +
            $"Generation={SharedTrajectoryGeneration} Real={realActual} " +
            $"Expected={expected} Fake={fakeActual} " +
            $"ExpectedDistance={expectedDistance} " +
            $"CandidateCount={candidateCount}",
            LogTag);

        if (MaskedViewers.Count > 0 || ShouldMaskViewer(PlayerControl.LocalPlayer))
        {
            ScheduleAbsoluteMaskReassert(
                switchSystem,
                "SABOTAGE_START");
        }
    }

    private static void ScheduleAbsoluteMaskReassert(
        SwitchSystem switchSystem,
        string reason)
    {
        var generation = ++ReassertGeneration;
        var trajectoryGeneration = SharedTrajectoryGeneration;

        var reassertDelaySeconds =
            SabotageStartReassertDelaySeconds;

        Logger.Info(
            $"{LogTag} Stage=REASSERT_SCHEDULED Reason={reason} " +
            $"Generation={generation} Delay={reassertDelaySeconds} " +
            $"HostActual={switchSystem.ActualSwitches & SwitchMask} " +
            $"ViewerCount={MaskedViewers.Count} " +
            $"TrajectoryGeneration={trajectoryGeneration}",
            LogTag);

        new LateTask(
            () =>
            {
                if (
                    generation != ReassertGeneration ||
                    trajectoryGeneration != SharedTrajectoryGeneration)
                {
                    Logger.Info(
                        $"{LogTag} Stage=REASSERT_SKIPPED Reason={reason} " +
                        $"Generation={generation} CurrentGeneration={ReassertGeneration} " +
                        $"TrajectoryGeneration={trajectoryGeneration} " +
                        $"CurrentTrajectoryGeneration={SharedTrajectoryGeneration} " +
                        $"Cause=Superseded",
                        LogTag);
                    return;
                }

                if (
                    !SharedTrajectoryInitialized ||
                    !IsStoredSwitchSystem(switchSystem) ||
                    !switchSystem.IsActive ||
                    !HasPublicationTarget())
                {
                    Logger.Info(
                        $"{LogTag} Stage=REASSERT_SKIPPED Reason={reason} " +
                        $"Generation={generation} Cause=InactiveOrNoViewer " +
                        $"Active={switchSystem.IsActive} ViewerCount={MaskedViewers.Count}",
                        LogTag);
                    return;
                }

                var authoritativeActual =
                    (byte)(switchSystem.ActualSwitches & SwitchMask);
                var currentExpected =
                    (byte)(switchSystem.ExpectedSwitches & SwitchMask);
                var fakeActual = SharedFakeActual;

                if (!IsExpectedPrivateFake(
                    fakeActual,
                    authoritativeActual,
                    currentExpected))
                {
                    FailSharedTrajectory(
                        switchSystem,
                        -1,
                        SharedRealPrevious,
                        authoritativeActual,
                        currentExpected,
                        fakeActual,
                        0,
                        "PUBLICATION_STATE_UNSAFE");
                    return;
                }

                foreach (var pair in SnapshotMaskedViewers())
                {
                    var entry = pair.Value;

                    if (!CanStillSend(entry.Target))
                    {
                        MaskedViewers.Remove(pair.Key);
                        continue;
                    }

                    var transform =
                        (byte)(authoritativeActual ^ fakeActual);

                    if (transform != 0)
                    {
                        SendTargetedElectricalUpdate(
                            entry.Target,
                            (byte)(SwitchSystem.DamageSystem | transform));
                    }

                    Logger.Info(
                        $"{LogTag} Stage=REASSERT_SENT Reason={reason} " +
                        $"Generation={generation} Viewer={entry.Target.PlayerId} " +
                        $"AuthoritativeActual={authoritativeActual} " +
                        $"Expected={currentExpected} FakeActual={fakeActual} Transform={transform}",
                        LogTag);
                }

                if (SharedTrajectoryInitialized)
                {
                    RemoteFakePublished = true;
                    RemotePublishedGeneration = trajectoryGeneration;
                }
            },
            reassertDelaySeconds,
            LogTag + "_" + reason);
    }

    private static void ArmRepairOuterBarrier(
        SwitchSystem switchSystem,
        int expectedBarrierGeneration,
        int expectedTrajectoryGeneration)
    {
        if (
            expectedBarrierGeneration >= 0 &&
            expectedTrajectoryGeneration >= 0 &&
            expectedBarrierGeneration <= RepairOuterBarrierGeneration &&
            expectedTrajectoryGeneration <= SharedTrajectoryGeneration &&
            (expectedBarrierGeneration < RepairOuterBarrierGeneration ||
                expectedTrajectoryGeneration < SharedTrajectoryGeneration))
        {
            return;
        }

        if (
            expectedBarrierGeneration != RepairOuterBarrierGeneration ||
            expectedTrajectoryGeneration != SharedTrajectoryGeneration)
        {
            FailCurrentTrajectoryInvariant(
                "ARM_REPAIR_OUTER_BARRIER",
                "ARM_GENERATION_MISMATCH",
                switchSystem);
            return;
        }

        if (
            AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost)
        {
            FailCurrentTrajectoryInvariant(
                "ARM_REPAIR_OUTER_BARRIER",
                "ARM_HOST_CONTEXT_MISMATCH",
                switchSystem);
            return;
        }

        if (!SharedTrajectoryInitialized)
        {
            FailCurrentTrajectoryInvariant(
                "ARM_REPAIR_OUTER_BARRIER",
                "ARM_TRAJECTORY_INACTIVE",
                switchSystem);
            return;
        }

        if (!IsStoredSwitchSystem(switchSystem))
        {
            FailCurrentTrajectoryInvariant(
                "ARM_REPAIR_OUTER_BARRIER",
                "ARM_SYSTEM_MISMATCH",
                switchSystem);
            return;
        }

        if (!switchSystem.IsActive)
        {
            FailCurrentTrajectoryInvariant(
                "ARM_REPAIR_OUTER_BARRIER",
                "ARM_ACTIVE_STATE_MISMATCH",
                switchSystem);
            return;
        }

        var authoritativeActual =
            (byte)(switchSystem.ActualSwitches & SwitchMask);
        var expected =
            (byte)(switchSystem.ExpectedSwitches & SwitchMask);

        if (authoritativeActual == expected)
        {
            FailCurrentTrajectoryInvariant(
                "ARM_REPAIR_OUTER_BARRIER",
                "ARM_AUTHORITATIVE_COMPLETED",
                switchSystem);
            return;
        }

        if (authoritativeActual != SharedRealPrevious)
        {
            FailCurrentTrajectoryInvariant(
                "ARM_REPAIR_OUTER_BARRIER",
                "ARM_AUTHORITATIVE_MISMATCH",
                switchSystem);
            return;
        }

        if (expected != SharedExpected)
        {
            FailCurrentTrajectoryInvariant(
                "ARM_REPAIR_OUTER_BARRIER",
                "ARM_EXPECTED_MISMATCH",
                switchSystem);
            return;
        }

        if (!IsExpectedPrivateFake(
            SharedFakeActual,
            authoritativeActual,
            expected))
        {
            FailCurrentTrajectoryInvariant(
                "ARM_REPAIR_OUTER_BARRIER",
                "ARM_FAKE_INVARIANT_MISMATCH",
                switchSystem);
            return;
        }

        var hadPending = RepairOuterBarrierArmed;
        var oldGeneration = RepairOuterBarrierArmedGeneration;

        ReassertGeneration++;
        ClearRepairOuterBarrierState(advanceGeneration: true);

        var generation = RepairOuterBarrierGeneration;

        if (hadPending)
        {
            Logger.Info(
                $"{LogTag} Stage=OUTER_BARRIER_SUPERSEDED " +
                $"OldGeneration={oldGeneration} NewGeneration={generation}",
                LogTag);
        }

        if (!HasPublicationTarget())
        {
            return;
        }

        RepairOuterBarrierArmed = true;
        RepairOuterBarrierArmedGeneration = generation;
        RepairOuterBarrierTrajectoryGeneration =
            SharedTrajectoryGeneration;
        RepairOuterBarrierSerializeObserved = false;
        RepairOuterBarrierSwitchSystem = switchSystem;
        RepairOuterBarrierExpected = expected;
        RepairOuterBarrierAuthoritativeActual =
            authoritativeActual;

        Logger.Info(
            $"{LogTag} Stage=ARM_REPAIR_OUTER_BARRIER " +
            $"Generation={generation} " +
            $"TrajectoryGeneration={SharedTrajectoryGeneration} " +
            $"Actual={authoritativeActual} Expected={expected} " +
            $"FakeActual={SharedFakeActual} " +
            $"ViewerCount={MaskedViewers.Count}",
            LogTag);
    }

    public static long BeginRepairOuterStreamInvocation()
    {
        if (
            AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost)
        {
            return NoOuterInvocationToken;
        }

        long token;

        do
        {
            token = System.Threading.Interlocked.Increment(
                ref OuterInvocationSequence);
        }
        while (token == NoOuterInvocationToken);

        var hadArmedBarrier = RepairOuterBarrierArmed;

        ActiveOuterInvocationHadArmedBarrier =
            hadArmedBarrier;
        ActiveOuterInvocationBarrierGeneration =
            hadArmedBarrier
                ? RepairOuterBarrierArmedGeneration
                : -1;
        ActiveOuterInvocationTrajectoryGeneration =
            hadArmedBarrier
                ? RepairOuterBarrierTrajectoryGeneration
                : -1;
        ActiveOuterInvocationToken = token;
        return token;
    }

    public static void EndRepairOuterStreamInvocation(
        long outerInvocationToken)
    {
        if (outerInvocationToken == NoOuterInvocationToken)
        {
            return;
        }

        var matchesActiveInvocation =
            ActiveOuterInvocationToken == outerInvocationToken;
        var endingHadArmedBarrier =
            matchesActiveInvocation &&
            ActiveOuterInvocationHadArmedBarrier;
        var endingBarrierGeneration =
            ActiveOuterInvocationBarrierGeneration;
        var endingTrajectoryGeneration =
            ActiveOuterInvocationTrajectoryGeneration;

        if (matchesActiveInvocation)
        {
            ActiveOuterInvocationToken = NoOuterInvocationToken;
            ActiveOuterInvocationHadArmedBarrier = false;
            ActiveOuterInvocationBarrierGeneration = -1;
            ActiveOuterInvocationTrajectoryGeneration = -1;
        }

        if (
            matchesActiveInvocation &&
            endingHadArmedBarrier &&
            RepairOuterBarrierObservedInvocationToken ==
                outerInvocationToken &&
            RepairOuterBarrierObservedGeneration ==
                endingBarrierGeneration &&
            RepairOuterBarrierObservedTrajectoryGeneration ==
                endingTrajectoryGeneration)
        {
            RepairOuterBarrierSerializeObserved = false;
            RepairOuterBarrierObservedInvocationToken =
                NoOuterInvocationToken;
            RepairOuterBarrierObservedGeneration = -1;
            RepairOuterBarrierObservedTrajectoryGeneration = -1;
        }
    }

    public static void ObserveNativeRepairSerialization(
        SwitchSystem switchSystem,
        bool initialState,
        bool isDirtyBefore,
        int serializedLength)
    {
        var activeOuterInvocationToken =
            ActiveOuterInvocationToken;
        var invocationHadArmedBarrier =
            ActiveOuterInvocationHadArmedBarrier;
        var invocationBarrierGeneration =
            ActiveOuterInvocationBarrierGeneration;
        var invocationTrajectoryGeneration =
            ActiveOuterInvocationTrajectoryGeneration;

        if (
            AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost ||
            activeOuterInvocationToken == NoOuterInvocationToken ||
            initialState ||
            !isDirtyBefore ||
            serializedLength <= 0)
        {
            return;
        }

        if (!invocationHadArmedBarrier)
        {
            return;
        }

        if (IsSupersededOuterInvocationSnapshot(
            invocationBarrierGeneration,
            invocationTrajectoryGeneration))
        {
            return;
        }

        if (
            invocationBarrierGeneration != RepairOuterBarrierGeneration ||
            invocationTrajectoryGeneration != SharedTrajectoryGeneration)
        {
            FailCurrentTrajectoryInvariant(
                "SERIALIZE_OBSERVATION",
                "SERIALIZE_CALLBACK_GENERATION_MISMATCH",
                switchSystem);
            return;
        }

        if (!RepairOuterBarrierArmed)
        {
            return;
        }

        if (
            RepairOuterBarrierArmedGeneration !=
                invocationBarrierGeneration ||
            RepairOuterBarrierTrajectoryGeneration !=
                invocationTrajectoryGeneration)
        {
            FailCurrentTrajectoryInvariant(
                "SERIALIZE_OBSERVATION",
                "SERIALIZE_CALLBACK_OWNERSHIP_MISMATCH",
                switchSystem);
            return;
        }

        if (RepairOuterBarrierSerializeObserved)
        {
            return;
        }

        if (!SharedTrajectoryInitialized)
        {
            FailCurrentTrajectoryInvariant(
                "SERIALIZE_OBSERVATION",
                "SERIALIZE_TRAJECTORY_INACTIVE",
                switchSystem);
            return;
        }

        if (
            !IsSameSwitchSystem(
                RepairOuterBarrierSwitchSystem,
                switchSystem) ||
            !IsStoredSwitchSystem(switchSystem))
        {
            FailCurrentTrajectoryInvariant(
                "SERIALIZE_OBSERVATION",
                "SERIALIZE_SYSTEM_MISMATCH",
                switchSystem);
            return;
        }

        if (!switchSystem.IsActive)
        {
            FailCurrentTrajectoryInvariant(
                "SERIALIZE_OBSERVATION",
                "SERIALIZE_ACTIVE_STATE_MISMATCH",
                switchSystem);
            return;
        }

        var authoritativeActual =
            (byte)(switchSystem.ActualSwitches & SwitchMask);
        var expected =
            (byte)(switchSystem.ExpectedSwitches & SwitchMask);

        if (authoritativeActual == expected)
        {
            FailCurrentTrajectoryInvariant(
                "SERIALIZE_OBSERVATION",
                "SERIALIZE_AUTHORITATIVE_COMPLETED",
                switchSystem);
            return;
        }

        if (
            authoritativeActual !=
                RepairOuterBarrierAuthoritativeActual)
        {
            FailCurrentTrajectoryInvariant(
                "SERIALIZE_OBSERVATION",
                "SERIALIZE_AUTHORITATIVE_MISMATCH",
                switchSystem);
            return;
        }

        if (
            expected != RepairOuterBarrierExpected ||
            expected != SharedExpected)
        {
            FailCurrentTrajectoryInvariant(
                "SERIALIZE_OBSERVATION",
                "SERIALIZE_EXPECTED_MISMATCH",
                switchSystem);
            return;
        }

        if (!IsExpectedPrivateFake(
            SharedFakeActual,
            authoritativeActual,
            expected))
        {
            FailCurrentTrajectoryInvariant(
                "SERIALIZE_OBSERVATION",
                "SERIALIZE_FAKE_INVARIANT_MISMATCH",
                switchSystem);
            return;
        }

        RepairOuterBarrierObservedInvocationToken =
            activeOuterInvocationToken;
        RepairOuterBarrierObservedGeneration =
            invocationBarrierGeneration;
        RepairOuterBarrierObservedTrajectoryGeneration =
            invocationTrajectoryGeneration;
        RepairOuterBarrierSerializeObserved = true;

        Logger.Info(
            $"{LogTag} Stage=OUTER_BARRIER_SERIALIZE_OBSERVED " +
            $"OuterToken={activeOuterInvocationToken} " +
            $"BarrierGeneration={invocationBarrierGeneration} " +
            $"TrajectoryGeneration={invocationTrajectoryGeneration} " +
            $"Actual={authoritativeActual} Expected={expected}",
            LogTag);
    }

    public static void TryPublishPendingRepairAfterOuterStream(
        long outerInvocationToken)
    {
        if (
            AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost ||
            outerInvocationToken == NoOuterInvocationToken)
        {
            return;
        }

        if (outerInvocationToken != ActiveOuterInvocationToken)
        {
            return;
        }

        var invocationHadArmedBarrier =
            ActiveOuterInvocationHadArmedBarrier;
        var invocationBarrierGeneration =
            ActiveOuterInvocationBarrierGeneration;
        var invocationTrajectoryGeneration =
            ActiveOuterInvocationTrajectoryGeneration;

        if (!invocationHadArmedBarrier)
        {
            return;
        }

        if (IsSupersededOuterInvocationSnapshot(
            invocationBarrierGeneration,
            invocationTrajectoryGeneration))
        {
            return;
        }

        if (
            invocationBarrierGeneration != RepairOuterBarrierGeneration ||
            invocationTrajectoryGeneration != SharedTrajectoryGeneration)
        {
            FailCurrentTrajectoryInvariant(
                "OUTER_PUBLISH",
                "PUBLISH_CALLBACK_GENERATION_MISMATCH",
                RepairOuterBarrierSwitchSystem);
            return;
        }

        if (!RepairOuterBarrierArmed)
        {
            return;
        }

        var generation = RepairOuterBarrierArmedGeneration;
        var trajectoryGeneration =
            RepairOuterBarrierTrajectoryGeneration;
        var switchSystem = RepairOuterBarrierSwitchSystem;

        if (
            generation != invocationBarrierGeneration ||
            trajectoryGeneration != invocationTrajectoryGeneration)
        {
            FailCurrentTrajectoryInvariant(
                "OUTER_PUBLISH",
                "PUBLISH_CALLBACK_OWNERSHIP_MISMATCH",
                switchSystem);
            return;
        }

        if (!SharedTrajectoryInitialized)
        {
            FailCurrentTrajectoryInvariant(
                "OUTER_PUBLISH",
                "PUBLISH_TRAJECTORY_INACTIVE",
                switchSystem);
            return;
        }

        if (!RepairOuterBarrierSerializeObserved)
        {
            FailCurrentTrajectoryInvariant(
                "OUTER_PUBLISH",
                "PUBLISH_WITHOUT_SERIALIZE_OBSERVATION",
                switchSystem);
            return;
        }

        if (
            outerInvocationToken !=
                RepairOuterBarrierObservedInvocationToken)
        {
            return;
        }

        if (
            RepairOuterBarrierObservedGeneration !=
                invocationBarrierGeneration ||
            RepairOuterBarrierObservedTrajectoryGeneration !=
                invocationTrajectoryGeneration)
        {
            FailCurrentTrajectoryInvariant(
                "OUTER_PUBLISH",
                "PUBLISH_OBSERVED_OWNERSHIP_MISMATCH",
                switchSystem);
            return;
        }

        if (
            switchSystem == null ||
            !IsStoredSwitchSystem(switchSystem))
        {
            FailCurrentTrajectoryInvariant(
                "OUTER_PUBLISH",
                "PUBLISH_SYSTEM_MISMATCH",
                switchSystem);
            return;
        }

        if (!switchSystem.IsActive)
        {
            FailCurrentTrajectoryInvariant(
                "OUTER_PUBLISH",
                "PUBLISH_ACTIVE_STATE_MISMATCH",
                switchSystem);
            return;
        }

        var authoritativeActual =
            (byte)(switchSystem.ActualSwitches & SwitchMask);
        var expected =
            (byte)(switchSystem.ExpectedSwitches & SwitchMask);
        var fakeActual = SharedFakeActual;

        if (authoritativeActual == expected)
        {
            FailCurrentTrajectoryInvariant(
                "OUTER_PUBLISH",
                "PUBLISH_AUTHORITATIVE_COMPLETED",
                switchSystem);
            return;
        }

        if (
            authoritativeActual !=
                RepairOuterBarrierAuthoritativeActual)
        {
            FailCurrentTrajectoryInvariant(
                "OUTER_PUBLISH",
                "PUBLISH_AUTHORITATIVE_MISMATCH",
                switchSystem);
            return;
        }

        if (
            expected != RepairOuterBarrierExpected ||
            expected != SharedExpected)
        {
            FailCurrentTrajectoryInvariant(
                "OUTER_PUBLISH",
                "PUBLISH_EXPECTED_MISMATCH",
                switchSystem);
            return;
        }

        if (!IsExpectedPrivateFake(
            fakeActual,
            authoritativeActual,
            expected))
        {
            FailCurrentTrajectoryInvariant(
                "OUTER_PUBLISH",
                "PUBLISH_FAKE_INVARIANT_MISMATCH",
                switchSystem);
            return;
        }

        var validTargets =
            new System.Collections.Generic.List<ViewerMaskState>();

        foreach (var pair in SnapshotMaskedViewers())
        {
            if (
                !CanStillSend(pair.Value.Target) ||
                !ShouldMaskViewer(pair.Value.Target))
            {
                MaskedViewers.Remove(pair.Key);
                continue;
            }

            validTargets.Add(pair.Value);
        }

        var hasHostPresentationTarget =
            ShouldMaskViewer(PlayerControl.LocalPlayer);

        if (
            validTargets.Count == 0 &&
            !hasHostPresentationTarget)
        {
            ClearRepairOuterBarrierState(advanceGeneration: false);
            return;
        }

        var transform =
            (byte)(authoritativeActual ^ fakeActual);

        foreach (var entry in validTargets)
        {
            SendTargetedElectricalUpdate(
                entry.Target,
                (byte)(SwitchSystem.DamageSystem | transform));

            Logger.Info(
                $"{LogTag} Stage=OUTER_BARRIER_REASSERT_SENT " +
                $"Generation={generation} " +
                $"TrajectoryGeneration={trajectoryGeneration} " +
                $"Viewer={entry.Target.PlayerId} " +
                $"AuthoritativeActual={authoritativeActual} " +
                $"Expected={expected} FakeActual={fakeActual} " +
                $"Transform={transform}",
                LogTag);
        }

        RemoteFakePublished = true;
        RemotePublishedGeneration = trajectoryGeneration;
        ClearRepairOuterBarrierState(advanceGeneration: false);

        Logger.Info(
            $"{LogTag} Stage=OUTER_BARRIER_CONSUMED " +
            $"Generation={generation} " +
            $"TrajectoryGeneration={trajectoryGeneration} " +
            $"TargetCount={validTargets.Count}",
            LogTag);
    }

    private static void ClearRepairOuterBarrierState(
        bool advanceGeneration)
    {
        if (advanceGeneration)
        {
            RepairOuterBarrierGeneration++;
        }

        RepairOuterBarrierArmed = false;
        RepairOuterBarrierArmedGeneration = -1;
        RepairOuterBarrierTrajectoryGeneration = -1;
        RepairOuterBarrierSerializeObserved = false;
        RepairOuterBarrierObservedInvocationToken =
            NoOuterInvocationToken;
        RepairOuterBarrierObservedGeneration = -1;
        RepairOuterBarrierObservedTrajectoryGeneration = -1;
        RepairOuterBarrierExpected = 0;
        RepairOuterBarrierAuthoritativeActual = 0;
        RepairOuterBarrierSwitchSystem = null;
    }

    private static bool IsSupersededOuterInvocationSnapshot(
        int invocationBarrierGeneration,
        int invocationTrajectoryGeneration)
    {
        return
            invocationBarrierGeneration >= 0 &&
            invocationTrajectoryGeneration >= 0 &&
            invocationBarrierGeneration <= RepairOuterBarrierGeneration &&
            invocationTrajectoryGeneration <= SharedTrajectoryGeneration &&
            (invocationBarrierGeneration < RepairOuterBarrierGeneration ||
                invocationTrajectoryGeneration < SharedTrajectoryGeneration);
    }

    private static bool IsSameSwitchSystem(
        SwitchSystem? left,
        SwitchSystem? right)
    {
        return
            left != null &&
            right != null &&
            left.Pointer == right.Pointer;
    }

    private static bool TrySelectInitialSharedFake(
        byte real,
        byte expected,
        out byte fake,
        out int expectedDistance,
        out int candidateCount)
    {
        var candidates =
            new System.Collections.Generic.List<byte>();

        for (byte candidate = 0; candidate <= SwitchMask; candidate++)
        {
            if (!IsExpectedPrivateFake(candidate, real, expected))
            {
                continue;
            }

            candidates.Add(candidate);
        }

        candidateCount = candidates.Count;
        expectedDistance = -1;

        if (candidates.Count == 0 || IRandom.Instance == null)
        {
            fake = 0;
            return false;
        }

        var rawIndex = IRandom.Instance.Next(candidates.Count);
        int selectedIndex;

        if (rawIndex >= 0 && rawIndex < candidates.Count)
        {
            selectedIndex = rawIndex;
        }
        else if (rawIndex == candidates.Count)
        {
            selectedIndex = candidates.Count - 1;
        }
        else
        {
            Logger.Error(
                $"{LogTag} Stage=SHARED_DECOY_RANDOM_INDEX_INVALID " +
                $"Phase=INITIAL RawIndex={rawIndex} " +
                $"CandidateCount={candidates.Count}",
                LogTag);
            fake = 0;
            return false;
        }

        fake = candidates[selectedIndex];
        expectedDistance = HammingDistance(fake, expected);
        return true;
    }

    private static bool TryAdvanceSharedTrajectory(
        SwitchSystem switchSystem,
        byte realIndex)
    {
        var previousReal = SharedRealPrevious;
        var currentReal =
            (byte)(switchSystem.ActualSwitches & SwitchMask);
        var currentExpected =
            (byte)(switchSystem.ExpectedSwitches & SwitchMask);
        var previousFake = SharedFakeActual;

        if (currentExpected != SharedExpected)
        {
            FailSharedTrajectory(
                switchSystem,
                realIndex,
                previousReal,
                currentReal,
                currentExpected,
                previousFake,
                0,
                "EXPECTED_CHANGED");
            return false;
        }

        if (!IsExpectedPrivateFake(
            previousFake,
            previousReal,
            currentExpected))
        {
            FailSharedTrajectory(
                switchSystem,
                realIndex,
                previousReal,
                currentReal,
                currentExpected,
                previousFake,
                0,
                "PREVIOUS_STATE_UNSAFE");
            return false;
        }

        var expectedCurrentReal =
            (byte)(previousReal ^ (1 << realIndex));

        if (expectedCurrentReal != currentReal)
        {
            FailSharedTrajectory(
                switchSystem,
                realIndex,
                previousReal,
                currentReal,
                currentExpected,
                previousFake,
                0,
                "AUTHORITATIVE_STEP_MISMATCH");
            return false;
        }

        var candidates =
            new System.Collections.Generic.List<(
                int FakeIndex,
                byte NextFake,
                int RepairIndexSupport,
                bool HasOffToOnSupport,
                bool HasOnToOffSupport)>();

        for (var fakeIndex = 0; fakeIndex < SwitchCount; fakeIndex++)
        {
            if (fakeIndex == realIndex)
            {
                continue;
            }

            var nextFake =
                (byte)(previousFake ^ (1 << fakeIndex));

            if (!IsExpectedPrivateFake(
                nextFake,
                currentReal,
                currentExpected))
            {
                continue;
            }

            CalculateVisibleTransitionSupport(
                currentExpected,
                previousFake,
                nextFake,
                fakeIndex,
                out var repairIndexSupport,
                out var hasOffToOnSupport,
                out var hasOnToOffSupport);

            if (
                repairIndexSupport >= 2 &&
                hasOffToOnSupport &&
                hasOnToOffSupport)
            {
                candidates.Add((
                    fakeIndex,
                    nextFake,
                    repairIndexSupport,
                    hasOffToOnSupport,
                    hasOnToOffSupport));
            }
        }

        if (candidates.Count == 0 || IRandom.Instance == null)
        {
            FailSharedTrajectory(
                switchSystem,
                realIndex,
                previousReal,
                currentReal,
                currentExpected,
                previousFake,
                candidates.Count,
                candidates.Count == 0
                    ? "NO_STRONG_PRIVATE_SAFE_ONE_BIT_CANDIDATE"
                    : "RANDOM_INSTANCE_UNAVAILABLE");
            return false;
        }

        var rawIndex = IRandom.Instance.Next(candidates.Count);
        int selectedIndex;

        if (rawIndex >= 0 && rawIndex < candidates.Count)
        {
            selectedIndex = rawIndex;
        }
        else if (rawIndex == candidates.Count)
        {
            selectedIndex = candidates.Count - 1;
        }
        else
        {
            Logger.Error(
                $"{LogTag} Stage=SHARED_DECOY_RANDOM_INDEX_INVALID " +
                $"Phase=MOVING RawIndex={rawIndex} " +
                $"CandidateCount={candidates.Count}",
                LogTag);
            FailSharedTrajectory(
                switchSystem,
                realIndex,
                previousReal,
                currentReal,
                currentExpected,
                previousFake,
                candidates.Count,
                "MOVING_RANDOM_INDEX_INVALID");
            return false;
        }

        var selected = candidates[selectedIndex];

        if (!IsExpectedPrivateFake(
            selected.NextFake,
            currentReal,
            currentExpected))
        {
            FailSharedTrajectory(
                switchSystem,
                realIndex,
                previousReal,
                currentReal,
                currentExpected,
                previousFake,
                candidates.Count,
                "SELECTED_STATE_UNSAFE");
            return false;
        }

        SharedFakeActual = selected.NextFake;
        SharedRealPrevious = currentReal;
        RemoteFakePublished = false;
        RemotePublishedGeneration = -1;

        Logger.Info(
            $"{LogTag} Stage=SHARED_DECOY_STEP " +
            $"RealIndex={realIndex} PreviousReal={previousReal} " +
            $"CurrentReal={currentReal} Expected={currentExpected} " +
            $"PreviousFake={previousFake} FakeIndex={selected.FakeIndex} " +
            $"NextFake={selected.NextFake} " +
            $"RepairIndexSupport={selected.RepairIndexSupport} " +
            $"HasOffToOnSupport={selected.HasOffToOnSupport} " +
            $"HasOnToOffSupport={selected.HasOnToOffSupport} " +
            $"CandidateCount={candidates.Count}",
            LogTag);

        return true;
    }

    private static void CalculateVisibleTransitionSupport(
        byte expected,
        byte previousFake,
        byte nextFake,
        int fakeIndex,
        out int repairIndexSupport,
        out bool hasOffToOnSupport,
        out bool hasOnToOffSupport)
    {
        repairIndexSupport = 0;
        hasOffToOnSupport = false;
        hasOnToOffSupport = false;

        var visibleDifference =
            (byte)((previousFake ^ nextFake) & SwitchMask);

        if (visibleDifference != (1 << fakeIndex))
        {
            return;
        }

        byte repairIndexSupportMask = 0;

        for (
            var hypotheticalPreviousRealValue = 0;
            hypotheticalPreviousRealValue <= SwitchMask;
            hypotheticalPreviousRealValue++)
        {
            var hypotheticalPreviousReal =
                (byte)hypotheticalPreviousRealValue;

            if (
                hypotheticalPreviousReal == expected ||
                !IsExpectedPrivateFake(
                    previousFake,
                    hypotheticalPreviousReal,
                    expected))
            {
                continue;
            }

            for (
                var hypotheticalRealIndex = 0;
                hypotheticalRealIndex < SwitchCount;
                hypotheticalRealIndex++)
            {
                if (hypotheticalRealIndex == fakeIndex)
                {
                    continue;
                }

                var realBit = 1 << hypotheticalRealIndex;
                var hypotheticalCurrentReal =
                    (byte)(hypotheticalPreviousReal ^ realBit);

                if (
                    hypotheticalCurrentReal == expected ||
                    !IsExpectedPrivateFake(
                        nextFake,
                        hypotheticalCurrentReal,
                        expected))
                {
                    continue;
                }

                repairIndexSupportMask |=
                    (byte)realBit;

                if ((hypotheticalPreviousReal & realBit) == 0)
                {
                    hasOffToOnSupport = true;
                }
                else
                {
                    hasOnToOffSupport = true;
                }
            }
        }

        repairIndexSupport =
            HammingDistance(repairIndexSupportMask, 0);
    }

    private static bool IsExpectedPrivateFake(
        byte fake,
        byte real,
        byte expected)
    {
        return
            (fake & ~SwitchMask) == 0 &&
            fake != expected &&
            HammingDistance(fake, expected) >= 2 &&
            fake != real;
    }

    private static int HammingDistance(byte left, byte right)
    {
        var difference = (byte)((left ^ right) & SwitchMask);
        var distance = 0;

        for (var index = 0; index < SwitchCount; index++)
        {
            if ((difference & (1 << index)) != 0)
            {
                distance++;
            }
        }

        return distance;
    }

    private static void FailSharedTrajectory(
        SwitchSystem switchSystem,
        int realIndex,
        byte previousReal,
        byte currentReal,
        byte expected,
        byte previousFake,
        int candidateCount,
        string reason)
    {
        Logger.Error(
            $"{LogTag} Stage=SHARED_DECOY_INVARIANT_FAIL " +
            $"Reason={reason} RealIndex={realIndex} " +
            $"PreviousReal={previousReal} CurrentReal={currentReal} " +
            $"Expected={expected} PreviousFake={previousFake} " +
            $"CandidateCount={candidateCount} " +
            $"Active={switchSystem.IsActive}",
            LogTag);

        InvalidateAllMaskState();
    }

    private static void FailCurrentTrajectoryInvariant(
        string source,
        string reason,
        SwitchSystem? switchSystem)
    {
        var authoritativeActual =
            switchSystem == null
                ? (byte)0
                : (byte)(switchSystem.ActualSwitches & SwitchMask);
        var expected =
            switchSystem == null
                ? (byte)0
                : (byte)(switchSystem.ExpectedSwitches & SwitchMask);

        Logger.Error(
            $"{LogTag} Stage=SHARED_DECOY_FAIL_CLOSED " +
            $"Source={source} Reason={reason} " +
            $"BarrierGeneration={RepairOuterBarrierGeneration} " +
            $"ArmedGeneration={RepairOuterBarrierArmedGeneration} " +
            $"TrajectoryGeneration={SharedTrajectoryGeneration} " +
            $"BarrierTrajectoryGeneration={RepairOuterBarrierTrajectoryGeneration} " +
            $"SystemPresent={switchSystem != null} " +
            $"StoredSystemMatch={switchSystem != null && IsStoredSwitchSystem(switchSystem)} " +
            $"Active={switchSystem != null && switchSystem.IsActive} " +
            $"AuthoritativeActual={authoritativeActual} Expected={expected} " +
            $"SharedReal={SharedRealPrevious} SharedExpected={SharedExpected} " +
            $"SharedFake={SharedFakeActual}",
            LogTag);

        InvalidateAllMaskState();
    }

    private static bool IsStoredSwitchSystem(SwitchSystem switchSystem)
    {
        return
            SharedSwitchSystem != null &&
            switchSystem != null &&
            SharedSwitchSystem.Pointer == switchSystem.Pointer;
    }

    private static bool HasPublicationTarget()
    {
        return
            MaskedViewers.Count > 0 ||
            (SharedTrajectoryInitialized &&
                ShouldMaskViewer(PlayerControl.LocalPlayer));
    }

    private static void InvalidateAllMaskState()
    {
        ReassertGeneration++;
        SharedTrajectoryGeneration++;
        SharedTrajectoryInitialized = false;
        RemoteFakePublished = false;
        RemotePublishedGeneration = -1;
        LocalFakeReady = false;
        LocalFakeReadyGeneration = -1;
        SharedFakeActual = 0;
        SharedExpected = 0;
        SharedRealPrevious = 0;
        SharedSwitchSystem = null;
        MaskedViewers.Clear();
        ClearRepairOuterBarrierState(advanceGeneration: true);
    }

    public static void ClearSharedTrajectoryForLifecycle(string reason)
    {
        var hadState =
            SharedTrajectoryInitialized ||
            RemoteFakePublished ||
            RemotePublishedGeneration >= 0 ||
            LocalFakeReady ||
            RepairOuterBarrierArmed ||
            MaskedViewers.Count > 0;

        InvalidateAllMaskState();

        if (hadState)
        {
            Logger.Info(
                $"{LogTag} Stage=LIFECYCLE_CLEAR Reason={reason} " +
                $"Generation={SharedTrajectoryGeneration}",
                LogTag);
        }
    }

    public static void ApplyHostSharedFakePresentation(
        SwitchMinigame switchMinigame)
    {
        if (
            AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost ||
            switchMinigame == null)
        {
            return;
        }

        var localPlayer = PlayerControl.LocalPlayer;

        if (
            localPlayer == null ||
            !ShouldMaskViewer(localPlayer) ||
            !SharedTrajectoryInitialized ||
            !LocalFakeReady ||
            LocalFakeReadyGeneration != SharedTrajectoryGeneration ||
            SharedSwitchSystem == null ||
            !SharedSwitchSystem.IsActive ||
            switchMinigame.ship == null ||
            !switchMinigame.ship.Systems.TryGetValue(
                SystemTypes.Electrical,
                out var electricalSystem))
        {
            return;
        }

        var currentSwitchSystem =
            electricalSystem.TryCast<SwitchSystem>();

        if (
            currentSwitchSystem == null ||
            !IsStoredSwitchSystem(currentSwitchSystem) ||
            !currentSwitchSystem.IsActive ||
            (currentSwitchSystem.ExpectedSwitches & SwitchMask) !=
                SharedExpected)
        {
            return;
        }

        var lights = switchMinigame.lights;
        var switches = switchMinigame.switches;

        if (
            lights == null ||
            switches == null ||
            lights.Length < SwitchCount ||
            switches.Length < SwitchCount)
        {
            return;
        }

        for (var index = 0; index < SwitchCount; index++)
        {
            if (lights[index] == null || switches[index] == null)
            {
                return;
            }
        }

        var fakeCorrectCount = 0;

        for (var index = 0; index < SwitchCount; index++)
        {
            var bit = 1 << index;
            var fakeIsOn = (SharedFakeActual & bit) != 0;
            var expectedIsOn = (SharedExpected & bit) != 0;
            var fakeMatchesExpected = fakeIsOn == expectedIsOn;

            lights[index].color =
                fakeMatchesExpected
                    ? switchMinigame.OnColor
                    : switchMinigame.OffColor;
            switches[index].flipY = fakeIsOn;

            if (fakeMatchesExpected)
            {
                fakeCorrectCount++;
            }
        }

        var fakeCorrectRatio =
            fakeCorrectCount / (float)SwitchCount;

        if (switchMinigame.top != null)
        {
            switchMinigame.top.NoiseLevel =
                1f - fakeCorrectRatio;
        }

        if (switchMinigame.bottom != null)
        {
            switchMinigame.bottom.NoiseP =
                fakeCorrectRatio * 0.47f;
        }
    }

    private static bool ShouldMaskViewer(PlayerControl target)
    {
        if (target == null ||
            target.Data == null ||
            target.Data.Disconnected ||
            target.Data.IsDead)
        {
            return false;
        }

        var roleClass = target.GetRoleClass();

        var isMadmate =
            target.Is(CustomRoleTypes.Madmate) ||
            (roleClass is SchrodingerCat schrodingerCat && schrodingerCat.AmMadmate);

        if (isMadmate && !Options.MadmateCanFixLightsOut.GetBool())
        {
            return true;
        }

        return roleClass is God;
    }

    private static bool CanStillSend(PlayerControl target)
    {
        return
            target != null &&
            target.Data != null &&
            !target.Data.Disconnected &&
            !target.AmOwner &&
            target.GetClientId() >= 0;
    }

    private static System.Collections.Generic.List<
        System.Collections.Generic.KeyValuePair<byte, ViewerMaskState>>
        SnapshotMaskedViewers()
    {
        return new System.Collections.Generic.List<
            System.Collections.Generic.KeyValuePair<byte, ViewerMaskState>>(
                MaskedViewers);
    }

    private static void SendTargetedElectricalUpdate(
        PlayerControl target,
        byte amount)
    {
        global::TownOfHostForE.ExtendedPlayerControl.RpcDesyncUpdateSystem(
            target,
            SystemTypes.Electrical,
            amount);
    }
}

[HarmonyPatch(typeof(SwitchMinigame), nameof(SwitchMinigame.Begin))]
public static class ForbiddenSabotageElectricalHostBeginRendererABV1
{
    public static void Postfix(SwitchMinigame __instance)
    {
        ForbiddenSabotageElectricalInfoMaskABV1Retry10FastRepairReassert
            .ApplyHostSharedFakePresentation(__instance);
    }
}

[HarmonyPatch(typeof(SwitchMinigame), nameof(SwitchMinigame.FixedUpdate))]
public static class ForbiddenSabotageElectricalHostFixedUpdateRendererABV1
{
    public static void Postfix(SwitchMinigame __instance)
    {
        ForbiddenSabotageElectricalInfoMaskABV1Retry10FastRepairReassert
            .ApplyHostSharedFakePresentation(__instance);
    }
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
public static class ForbiddenSabotageElectricalMeetingCleanupABV1
{
    public static void Prefix()
    {
        ForbiddenSabotageElectricalInfoMaskABV1Retry10FastRepairReassert
            .ClearSharedTrajectoryForLifecycle("MEETING_START");
    }
}

[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.OnDestroy))]
public static class ForbiddenSabotageElectricalShipDestroyCleanupABV1
{
    public static void Prefix()
    {
        ForbiddenSabotageElectricalInfoMaskABV1Retry10FastRepairReassert
            .ClearSharedTrajectoryForLifecycle("SHIP_DESTROY");
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
public static class ForbiddenSabotageElectricalGameEndCleanupABV1
{
    public static void Prefix()
    {
        ForbiddenSabotageElectricalInfoMaskABV1Retry10FastRepairReassert
            .ClearSharedTrajectoryForLifecycle("GAME_END");
    }
}

[HarmonyPatch(typeof(SwitchSystem), nameof(SwitchSystem.Serialize))]
public static class ForbiddenSabotageElectricalWireProbeABV1
{
    private const string LogTag =
        "FORBIDDEN_SABOTAGE_ELECTRICAL_WIRE_PROBE_AB_V1";

    public readonly struct SerializeState
    {
        public SerializeState(
            int startPosition,
            byte actualBefore,
            byte expectedBefore,
            bool isDirtyBefore,
            bool initialState)
        {
            StartPosition = startPosition;
            ActualBefore = actualBefore;
            ExpectedBefore = expectedBefore;
            IsDirtyBefore = isDirtyBefore;
            InitialState = initialState;
        }

        public int StartPosition { get; }
        public byte ActualBefore { get; }
        public byte ExpectedBefore { get; }
        public bool IsDirtyBefore { get; }
        public bool InitialState { get; }
    }

    public static void Prefix(
        SwitchSystem __instance,
        [HarmonyArgument(0)] MessageWriter writer,
        [HarmonyArgument(1)] bool initialState,
        out SerializeState __state)
    {
        __state = new SerializeState(
            writer.Position,
            __instance.ActualSwitches,
            __instance.ExpectedSwitches,
            __instance.IsDirty,
            initialState);
    }

    public static void Postfix(
        SwitchSystem __instance,
        [HarmonyArgument(0)] MessageWriter writer,
        SerializeState __state)
    {
        if (!AmongUsClient.Instance.AmHost)
        {
            return;
        }

        var endPosition = writer.Position;
        var startPosition = __state.StartPosition;

        if (
            startPosition < 0 ||
            endPosition < startPosition ||
            endPosition > writer.Length)
        {
            Logger.Info(
                $"{LogTag} Stage=INVALID_RANGE " +
                $"InitialState={__state.InitialState} " +
                $"Start={startPosition} End={endPosition} Length={writer.Length}",
                LogTag);

            return;
        }

        var serializedLength =
            endPosition - startPosition;

        ForbiddenSabotageElectricalInfoMaskABV1Retry10FastRepairReassert
            .ObserveNativeRepairSerialization(
                __instance,
                __state.InitialState,
                __state.IsDirtyBefore,
                serializedLength);

        var bytes =
            new byte[serializedLength];

        for (
            var index = 0;
            index < serializedLength;
            index++)
        {
            bytes[index] =
                writer.Buffer[startPosition + index];
        }

        var hex =
            global::System.Convert.ToHexString(bytes);

        Logger.Info(
            $"{LogTag} Stage=SERIALIZED " +
            $"InitialState={__state.InitialState} " +
            $"Start={startPosition} End={endPosition} " +
            $"Length={serializedLength} Hex={hex} " +
            $"ActualBefore={__state.ActualBefore} " +
            $"ExpectedBefore={__state.ExpectedBefore} " +
            $"DirtyBefore={__state.IsDirtyBefore} " +
            $"ActualAfter={__instance.ActualSwitches} " +
            $"ExpectedAfter={__instance.ExpectedSwitches} " +
            $"DirtyAfter={__instance.IsDirty}",
            LogTag);
    }
}
