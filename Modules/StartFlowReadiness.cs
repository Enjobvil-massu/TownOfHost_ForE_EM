using System;
using TownOfHostForE.Roles.Core;

namespace TownOfHostForE.Modules;

/// <summary>
/// Host-side readiness contract for custom intro synchronization.
/// Native IntroCutscene execution is never blocked by this coordinator.
/// </summary>
internal static class StartFlowReadiness
{
    private const string LogLabel = "STARTFLOW_READINESS_AB_V1";

    private static Action? deferredIntroSync;

    internal static bool FlowActive { get; private set; }
    internal static bool RoleCommitted { get; private set; }
    internal static bool ShipStatusBeginReturned { get; private set; }
    internal static bool TasksReady { get; private set; }
    internal static bool IntroSyncReady { get; private set; }
    internal static bool IntroSyncApplied { get; private set; }

    internal static void BeginFlow()
    {
        FlowActive = AmongUsClient.Instance?.AmHost == true;
        RoleCommitted = false;
        ShipStatusBeginReturned = false;
        TasksReady = false;
        IntroSyncReady = false;
        IntroSyncApplied = false;
        deferredIntroSync = null;

        Logger.Info(
            $"STARTFLOW_BEGIN HostFlow={FlowActive}",
            LogLabel);
    }

    internal static void MarkRoleCommitted()
    {
        if (!FlowActive) return;

        RoleCommitted = true;
        Logger.Info("STARTFLOW_ROLE_COMMITTED", LogLabel);
        TryPromoteIntroSyncReady();
    }

    internal static void MarkShipStatusBeginReturned()
    {
        if (!FlowActive) return;

        ShipStatusBeginReturned = true;
        Logger.Info("STARTFLOW_SHIPSTATUS_BEGIN_RETURNED", LogLabel);
        TryPromoteIntroSyncReady();
    }

    internal static bool CanInitializeTaskState(PlayerControl? player)
    {
        if (!FlowActive) return true;
        if (IntroSyncReady) return true;

        TryPromoteIntroSyncReady();
        if (IntroSyncReady) return true;

        Logger.Info(
            $"TASKSTATE_INIT_DEFERRED PlayerId={player?.PlayerId.ToString() ?? "NULL"} " +
            ReadinessSnapshot(),
            LogLabel);
        return false;
    }

    internal static bool CanNotifyRoles()
    {
        if (!FlowActive) return true;
        if (IntroSyncReady) return true;

        TryPromoteIntroSyncReady();
        if (IntroSyncReady) return true;

        Logger.Info(
            $"NOTIFY_ROLES_DEFERRED {ReadinessSnapshot()}",
            LogLabel);
        return false;
    }

    internal static bool CanUseIntroRoleState()
    {
        if (!FlowActive) return true;
        if (IntroSyncReady) return true;

        TryPromoteIntroSyncReady();
        return IntroSyncReady;
    }

    internal static bool IsSafeCachedSelfName(
        PlayerControl player,
        string cachedName)
    {
        if (!CanUseIntroRoleState()) return false;

        var state = player == null
            ? null
            : PlayerState.GetByPlayerId(player.PlayerId);

        if (state == null || state.MainRole == CustomRoles.NotAssigned)
        {
            return false;
        }

        // Secondary fail-safe only. State readiness above is the primary gate.
        return !cachedName.Contains("[INVALID:", StringComparison.Ordinal) &&
               !cachedName.Contains("<INVALID", StringComparison.Ordinal);
    }

    internal static void RequestIntroSync(Action action)
    {
        if (!FlowActive)
        {
            action();
            return;
        }

        if (IntroSyncApplied) return;

        if (IntroSyncReady)
        {
            IntroSyncApplied = true;
            action();
            Logger.Info(
                "STARTFLOW_INTRO_CUSTOM_SYNC_APPLIED",
                LogLabel);
            return;
        }

        deferredIntroSync = action;
        if (!TryPromoteIntroSyncReady())
        {
            Logger.Info(
                $"STARTFLOW_INTRO_CUSTOM_SYNC_DEFERRED {ReadinessSnapshot()}",
                LogLabel);
        }
    }

    internal static System.Collections.IEnumerator WaitForIntroSyncReady(
        float timeoutSeconds = 12f)
    {
        var elapsedSeconds = 0f;

        while (!TryPromoteIntroSyncReady() && elapsedSeconds < timeoutSeconds)
        {
            elapsedSeconds += UnityEngine.Time.unscaledDeltaTime;
            yield return null;
        }

        if (!IntroSyncReady)
        {
            deferredIntroSync = null;
            Logger.Error(
                $"STARTFLOW_INTRO_SYNC_TIMEOUT Action=CUSTOM_SYNC_SKIPPED " +
                $"ElapsedMs={(long)(elapsedSeconds * 1000f)} {ReadinessSnapshot()}",
                LogLabel);
        }
    }

    private static bool TryPromoteIntroSyncReady()
    {
        if (!FlowActive || IntroSyncReady)
        {
            return IntroSyncReady;
        }

        if (!RoleCommitted || !ShipStatusBeginReturned)
        {
            return false;
        }

        foreach (var player in Main.AllPlayerControls)
        {
            if (player == null || player.Data == null)
            {
                return false;
            }

            if (player.Data.Disconnected) continue;

            var state = PlayerState.GetByPlayerId(player.PlayerId);
            if (state == null ||
                state.MainRole == CustomRoles.NotAssigned ||
                player.Data.Tasks == null)
            {
                return false;
            }
        }

        TasksReady = true;
        IntroSyncReady = true;
        Logger.Info(
            $"STARTFLOW_TASKS_READY {ReadinessSnapshot()}",
            LogLabel);
        Logger.Info(
            $"STARTFLOW_INTRO_SYNC_READY {ReadinessSnapshot()}",
            LogLabel);

        if (!IntroSyncApplied && deferredIntroSync != null)
        {
            var action = deferredIntroSync;
            deferredIntroSync = null;
            IntroSyncApplied = true;
            action();
            Logger.Info(
                "STARTFLOW_INTRO_CUSTOM_SYNC_APPLIED",
                LogLabel);
        }

        return true;
    }

    private static string ReadinessSnapshot()
    {
        return
            $"RoleCommitted={RoleCommitted} " +
            $"ShipStatusBeginReturned={ShipStatusBeginReturned} " +
            $"TasksReady={TasksReady} " +
            $"IntroSyncReady={IntroSyncReady}";
    }
}
