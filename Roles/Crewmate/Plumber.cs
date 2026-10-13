using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using TownOfHostForE.Attributes;
using TownOfHostForE.Roles.Core;

namespace TownOfHostForE.Roles.Crewmate;

// PLUMBER_HOST_ONLY_V1: real vanilla tasks; TaskState remains the crew-win authority.
public sealed class Plumber : RoleBase
{
    public static readonly SimpleRoleInfo RoleInfo = SimpleRoleInfo.Create(
        typeof(Plumber), player => new Plumber(player), CustomRoles.Plumber,
        () => RoleTypes.Engineer, CustomRoleTypes.Crewmate, 41800, SetupOptionItem,
        "配管工", "#48b7a8",
        assignInfo: new(CustomRoles.Plumber, CustomRoleTypes.Crewmate)
        {
            IsInitiallyAssignableCallBack = IsMapAssignable,
        });

    private const string LogTag = "PLUMBER_HOST_ONLY_V1";
    private static OptionItem OptionTotalTasks = null!;
    private static OptionItem OptionUnlockTasks = null!;
    private static OptionItem OptionDarknessDuration = null!;
    private static OptionItem OptionVentArrow = null!;
    private static OptionItem OptionArrowDuration = null!;
    private static OptionItem OptionVentCooldown = null!;
    private static OptionItem OptionVentMaxTime = null!;
    private enum OptionName
    {
        PlumberTotalTasks, PlumberUnlockTasks, PlumberDarknessDuration,
        PlumberVentArrow, PlumberArrowDuration, PlumberVentCooldown, PlumberVentMaxTime,
    }

    private static readonly Dictionary<byte, byte> LastTaskIndex = new();
    private static readonly Dictionary<byte, float> DarknessUntil = new();
    private static readonly HashSet<byte> ForcedVentExits = new();
    private readonly int totalTasks;
    private readonly int unlockTasks;
    private readonly float darknessDuration;
    private readonly bool showVentArrow;
    private readonly float arrowDuration;
    private readonly float ventCooldown;
    private readonly float ventMaxTime;
    private bool unlocked;
    private bool pendingTaskAssignment;
    private bool arrowActive;
    private float arrowUntil;

    public Plumber(PlayerControl player) : base(RoleInfo, player)
    {
        totalTasks = Math.Max(1, OptionTotalTasks.GetInt());
        unlockTasks = Math.Clamp(OptionUnlockTasks.GetInt(), 1, totalTasks);
        darknessDuration = OptionDarknessDuration.GetFloat();
        showVentArrow = OptionVentArrow.GetBool();
        arrowDuration = OptionArrowDuration.GetFloat();
        ventCooldown = OptionVentCooldown.GetFloat();
        ventMaxTime = OptionVentMaxTime.GetFloat();
    }

    private static void SetupOptionItem()
    {
        OptionTotalTasks = IntegerOptionItem.Create(RoleInfo, 10, OptionName.PlumberTotalTasks,
            new(1, 30, 1), 6, false).SetValueFormat(OptionFormat.Pieces);
        OptionUnlockTasks = IntegerOptionItem.Create(RoleInfo, 11, OptionName.PlumberUnlockTasks,
            new(1, 30, 1), 3, false).SetValueFormat(OptionFormat.Pieces);
        OptionDarknessDuration = FloatOptionItem.Create(RoleInfo, 12, OptionName.PlumberDarknessDuration,
            new(1f, 60f, 1f), 10f, false).SetValueFormat(OptionFormat.Seconds);
        OptionVentArrow = BooleanOptionItem.Create(RoleInfo, 13, OptionName.PlumberVentArrow, true, false);
        OptionArrowDuration = FloatOptionItem.Create(RoleInfo, 14, OptionName.PlumberArrowDuration,
            new(1f, 30f, 1f), 5f, false, OptionVentArrow).SetValueFormat(OptionFormat.Seconds);
        OptionVentCooldown = FloatOptionItem.Create(RoleInfo, 15, OptionName.PlumberVentCooldown,
            new(0f, 180f, 5f), 5f, false).SetValueFormat(OptionFormat.Seconds);
        OptionVentMaxTime = FloatOptionItem.Create(RoleInfo, 16, OptionName.PlumberVentMaxTime,
            new(0f, 180f, 5f), 10f, false).SetValueFormat(OptionFormat.Seconds);
    }

    public override void ApplyGameOptions(IGameOptions opt)
    {
        AURoleOptions.EngineerCooldown = ventCooldown <= 0f ? 1f : ventCooldown;
        AURoleOptions.EngineerInVentMaxTime = ventMaxTime <= 0f ? 999f : ventMaxTime;
    }

    [GameModuleInitializer]
    public static void Initialize()
    {
        LastTaskIndex.Clear();
        DarknessUntil.Clear();
        ForcedVentExits.Clear();
    }

    public override void Add() => CustomRoleManager.OnFixedUpdateOthers.Add(UpdateDarkness);

    public override void OnDestroy()
    {
        ClearArrow();
        pendingTaskAssignment = false;
        unlocked = false;
        LastTaskIndex.Remove(Player.PlayerId);
        if (!CustomRoleManager.AllActiveRoles.Values.OfType<Plumber>().Any(role => role != this))
        {
            ClearTransientEffects();
            CustomRoleManager.OnFixedUpdateOthers.Remove(UpdateDarkness);
        }
    }

    internal static int GetTotalTaskCount(PlayerControl player) =>
        player.GetRoleClass() is Plumber plumber ? plumber.totalTasks : Math.Max(1, OptionTotalTasks.GetInt());

    private static bool IsSupportedMap(MapNames map) =>
        map is MapNames.Skeld or MapNames.MiraHQ or MapNames.Airship;

    private static bool IsMapAssignable()
    {
        if (Main.NormalOptions == null || !IsSupportedMap((MapNames)Main.NormalOptions.MapId)) return false;
        return TryGetTaskPool(out _);
    }

    private static bool TryGetTaskPool(out List<byte> pool)
    {
        pool = new();
        var ship = ShipStatus.Instance;
        if (Main.NormalOptions == null || ship == null || ship.ShortTasks == null)
        {
            Logger.Error("Stage=TASK_POOL_ERROR | ShipStatus/ShortTasks/options unavailable; no task IDs sent.", LogTag, false);
            return false;
        }
        var map = (MapNames)Main.NormalOptions.MapId;
        if (!IsSupportedMap(map))
        {
            Logger.Error($"Stage=TASK_POOL_ERROR | Unsupported map={map}; no task IDs sent.", LogTag, false);
            return false;
        }

        var hasVentCleaning = false;
        var hasCleanToilet = false;
        foreach (var task in ship.ShortTasks)
        {
            if (task == null) continue;
            if (task.TaskType != TaskTypes.VentCleaning &&
                !(map == MapNames.Airship && task.TaskType == TaskTypes.CleanToilet)) continue;
            if (task.Index < 0 || task.Index > byte.MaxValue)
            {
                Logger.Error($"Stage=TASK_POOL_ERROR | Invalid task.Index={task.Index}; no task IDs sent.", LogTag, false);
                pool.Clear();
                return false;
            }
            hasVentCleaning |= task.TaskType == TaskTypes.VentCleaning;
            hasCleanToilet |= task.TaskType == TaskTypes.CleanToilet;
            var id = (byte)task.Index;
            if (!pool.Contains(id)) pool.Add(id);
        }
        if (!hasVentCleaning || (map == MapNames.Airship && !hasCleanToilet))
        {
            Logger.Error($"Stage=TASK_POOL_ERROR | Map={map} | VentCleaning={hasVentCleaning} | CleanToilet={hasCleanToilet}; no task IDs sent.", LogTag, false);
            pool.Clear();
            return false;
        }
        return true;
    }

    internal static bool TryBuildSingleTaskArray(PlayerControl player, out Il2CppStructArray<byte> taskIds)
    {
        taskIds = new Il2CppStructArray<byte>(0);
        if (!AmongUsClient.Instance.AmHost || player == null || player.Data == null || player.Data.Disconnected)
            return false;
        if (player.GetRoleClass() is Plumber plumber &&
            plumber.MyTaskState.CompletedTasksCount >= plumber.totalTasks)
        {
            Logger.Info($"Stage=TASKS_FINISHED | PlayerId={player.PlayerId} | Completed={plumber.MyTaskState.CompletedTasksCount}/{plumber.totalTasks}", LogTag);
            return true;
        }
        if (!TryGetTaskPool(out var pool)) return false;
        var selected = pool[IRandom.Instance.Next(pool.Count)];
        LastTaskIndex[player.PlayerId] = selected;
        taskIds = new Il2CppStructArray<byte>(1);
        taskIds[0] = selected;
        Logger.Info($"Stage=TASK_ASSIGNED | PlayerId={player.PlayerId} | Map={(MapNames)Main.NormalOptions.MapId} | TaskIndex={selected} | VisibleTasks=1", LogTag);
        return true;
    }

    internal bool CanCompleteAssignedTask(uint taskId)
    {
        if (pendingTaskAssignment || Player.Data == null || Player.Data.Disconnected ||
            Player.Data.Tasks == null || Player.Data.Tasks.Count != 1 ||
            !MyTaskState.hasTasks || MyTaskState.AllTasksCount != totalTasks ||
            MyTaskState.CompletedTasksCount >= totalTasks) return false;
        var task = Player.Data.FindTaskById(taskId);
        return task != null && !task.Complete;
    }

    internal void AfterTaskCompleted(uint taskId, int completedBefore)
    {
        var task = Player.Data.FindTaskById(taskId);
        if (task == null || !task.Complete || MyTaskState.CompletedTasksCount != completedBefore + 1)
        {
            // The shared prefix ran first; do not count an unconfirmed vanilla completion.
            if (MyTaskState.CompletedTasksCount == completedBefore + 1)
                MyTaskState.CompletedTasksCount = completedBefore;
            GameData.Instance.RecomputeTaskCounts();
            Logger.Error($"Stage=TASK_COMPLETE_REJECTED | PlayerId={Player.PlayerId} | TaskId={taskId}", LogTag, false);
            return;
        }
        if (!unlocked && MyTaskState.CompletedTasksCount >= unlockTasks)
        {
            unlocked = true;
            Logger.Info($"Stage=ABILITY_UNLOCKED | PlayerId={Player.PlayerId} | Completed={MyTaskState.CompletedTasksCount}/{totalTasks} | Required={unlockTasks}", LogTag);
        }
        // Run on the next task-phase physics tick, after vanilla CompleteTask has finished.
        // This also preserves the queue across a meeting and clears the visible list at total/total.
        pendingTaskAssignment = true;
        Logger.Info($"Stage=TASK_COMPLETED | PlayerId={Player.PlayerId} | Completed={MyTaskState.CompletedTasksCount}/{MyTaskState.AllTasksCount}", LogTag);
        Utils.NotifyRoles(SpecifySeer: Player);
    }

    public override void OnFixedUpdate(PlayerControl player)
    {
        if (!AmongUsClient.Instance.AmHost || Player.Data == null || Player.Data.Disconnected) return;
        if (arrowActive && (!Player.IsAlive() || Time.realtimeSinceStartup >= arrowUntil))
        {
            ClearArrow();
            Utils.NotifyRoles(SpecifySeer: Player);
        }
        if (!pendingTaskAssignment) return;
        pendingTaskAssignment = false;
        Player.Data.RpcSetTasks(new Il2CppStructArray<byte>(0));
        // Never reset either TaskState count during redistribution.
        GameData.Instance.RecomputeTaskCounts();
        Utils.NotifyRoles(SpecifySeer: Player);
    }

    public override string GetProgressText(bool comms = false) =>
        unlocked ? Utils.ColorString(RoleInfo.RoleColor, $" [{Translator.GetString("PlumberUnlocked")}]") : "";

    public override string GetLowerText(PlayerControl seer, PlayerControl seen = null!,
        bool isForMeeting = false, bool isForHud = false)
    {
        if (!arrowActive || isForMeeting || !GameStates.IsInTask || !Player.IsAlive() ||
            seer.PlayerId != Player.PlayerId || (seen != null && seen.PlayerId != Player.PlayerId)) return "";
        return Utils.ColorString(RoleInfo.RoleColor, TargetArrow.GetArrowsP(Player, Player.PlayerId));
    }

    private void ClearArrow()
    {
        TargetArrow.RemoveSourcePosition(Player.PlayerId, Player.PlayerId);
        arrowActive = false;
        arrowUntil = 0f;
    }

    // CountTypes.Crew is also used by hostile neutrals, so it is not a faction filter.
    internal static bool IsAbilityTarget(PlayerControl target)
    {
        if (target == null || target.Data == null || target.Data.Role == null ||
            target.Data.Disconnected || !target.IsAlive()) return false;
        var role = target.GetCustomRole();
        var faction = role.GetCustomRoleTypes();
        if (faction == CustomRoleTypes.Crewmate || role is CustomRoles.GM or CustomRoles.God) return false;
        if (!target.CanUseImpostorVentButton() && target.Data.Role.Role != RoleTypes.Engineer) return false;
        if (role.IsImpostorTeam()) return true;
        return faction == CustomRoleTypes.Neutral && role != CustomRoles.Opportunist &&
            (target.IsNeutralKiller() || role == CustomRoles.JClient);
    }

    private bool CanUseAbility => unlocked && Player.Data != null && !Player.Data.Disconnected && Player.IsAlive();

    internal static bool ShouldObserveExit(PlayerControl player) =>
        AmongUsClient.Instance.AmHost && GameStates.IsInTask && !GameStates.IsMeeting &&
        !Roles.Neutral.Renegade.IsAwakeningActive() && player != null && player.inVent &&
        !ForcedVentExits.Contains(player.PlayerId) && IsAbilityTarget(player) &&
        CustomRoleManager.AllActiveRoles.Values.OfType<Plumber>().Any(role => role.CanUseAbility);

    internal static void RecordVentEnter(PlayerControl player)
    {
        if (AmongUsClient.Instance.AmHost && player != null)
            ForcedVentExits.Remove(player.PlayerId);
    }

    internal static void RecordForcedVentExit(PlayerControl player)
    {
        if (AmongUsClient.Instance.AmHost && player != null)
            ForcedVentExits.Add(player.PlayerId);
    }

    internal static void OnSuccessfulExit(PlayerControl target, int ventId)
    {
        if (!AmongUsClient.Instance.AmHost || !GameStates.IsInTask || GameStates.IsMeeting ||
            Roles.Neutral.Renegade.IsAwakeningActive() || target == null || target.inVent ||
            ForcedVentExits.Contains(target.PlayerId) || !IsAbilityTarget(target)) return;
        var plumbers = CustomRoleManager.AllActiveRoles.Values.OfType<Plumber>()
            .Where(role => role.CanUseAbility).ToArray();
        if (plumbers.Length == 0) return;
        var now = Time.realtimeSinceStartup;
        DarknessUntil[target.PlayerId] = now + plumbers.Max(role => role.darknessDuration);
        target.MarkDirtySettings();
        Logger.Info($"Stage=EXIT_VENT_SUCCESS | TargetId={target.PlayerId} | VentId={ventId} | DarknessUntil={DarknessUntil[target.PlayerId]} | TimerMode=RESET", LogTag);

        if (!plumbers.Any(role => role.showVentArrow)) return;
        var ship = ShipStatus.Instance;
        var vent = ship == null || ship.AllVents == null ? null : ship.AllVents.FirstOrDefault(item => item != null && item.Id == ventId);
        if (vent == null)
        {
            Logger.Error($"Stage=VENT_POSITION_ERROR | VentId={ventId}; darkness applied, arrow skipped.", LogTag, false);
            return;
        }
        var fixedPosition = vent.transform.position;
        foreach (var plumber in plumbers)
        {
            if (!plumber.showVentArrow) continue;
            // A self-owned source key cannot collide with a beam's viewer/other-owner key.
            TargetArrow.AddSourcePosition(plumber.Player.PlayerId, plumber.Player.PlayerId, fixedPosition);
            plumber.arrowActive = true;
            plumber.arrowUntil = now + plumber.arrowDuration;
            Utils.NotifyRoles(SpecifySeer: plumber.Player);
            Logger.Info($"Stage=VENT_ARROW | PlayerId={plumber.Player.PlayerId} | VentId={ventId} | Position={fixedPosition} | Until={plumber.arrowUntil}", LogTag);
        }
    }

    internal static void ApplyDarknessOptions(PlayerControl player, IGameOptions opt)
    {
        if (!AmongUsClient.Instance.AmHost || !IsAbilityTarget(player) ||
            !DarknessUntil.TryGetValue(player.PlayerId, out var until) || Time.realtimeSinceStartup >= until) return;
        // Cap both carriers after all normal role/add-on vision modifiers; preserve stronger blackouts.
        opt.SetFloat(FloatOptionNames.CrewLightMod, Mathf.Min(opt.GetFloat(FloatOptionNames.CrewLightMod), 0.05f));
        opt.SetFloat(FloatOptionNames.ImpostorLightMod, Mathf.Min(opt.GetFloat(FloatOptionNames.ImpostorLightMod), 0.05f));
    }

    private static void UpdateDarkness(PlayerControl player)
    {
        if (!AmongUsClient.Instance.AmHost || !DarknessUntil.TryGetValue(player.PlayerId, out var until)) return;
        if (Time.realtimeSinceStartup < until && IsAbilityTarget(player)) return;
        DarknessUntil.Remove(player.PlayerId);
        if (player.Data != null && !player.Data.Disconnected) player.MarkDirtySettings();
        Logger.Info($"Stage=DARKNESS_RESTORED | TargetId={player.PlayerId} | Restore=NORMAL_OPTIONS_REBUILD", LogTag);
    }

    internal static void ClearTransientEffects()
    {
        foreach (var id in DarknessUntil.Keys.ToArray())
        {
            var target = Utils.GetPlayerById(id);
            if (target != null && target.Data != null && !target.Data.Disconnected) target.MarkDirtySettings();
        }
        DarknessUntil.Clear();
        foreach (var plumber in CustomRoleManager.AllActiveRoles.Values.OfType<Plumber>().ToArray())
            plumber.ClearArrow();
    }

    internal static void EndGame()
    {
        ClearTransientEffects();
        Initialize();
        foreach (var plumber in CustomRoleManager.AllActiveRoles.Values.OfType<Plumber>())
        {
            plumber.unlocked = false;
            plumber.pendingTaskAssignment = false;
        }
    }
}