using System.Collections;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using TownOfHostForE.Roles.AddOns.Crewmate;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Crewmate;

namespace TownOfHostForE;

// Plumber-only validation before the existing shared TaskState increment.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CompleteTask))]
internal static class PlumberCompleteTaskPatch
{
    [HarmonyPriority(Priority.First)]
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] uint taskId, out int __state)
    {
        __state = -1;
        if (!AmongUsClient.Instance.AmHost || __instance.GetRoleClass() is not Plumber plumber) return true;
        if (!plumber.CanCompleteAssignedTask(taskId)) return false;
        __state = plumber.MyTaskState.CompletedTasksCount;
        return true;
    }

    [HarmonyPriority(Priority.Last)]
    public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] uint taskId, int __state)
    {
        if (__state >= 0 && AmongUsClient.Instance.AmHost && __instance.GetRoleClass() is Plumber plumber)
            plumber.AfterTaskCompleted(taskId, __state);
    }
}

// Dedicated totals are fixed. Other roles retain Workhorse's original eligibility and behavior.
[HarmonyPatch(typeof(Workhorse), nameof(Workhorse.OnCompleteTask))]
internal static class PlumberWorkhorseTaskPatch
{
    public static bool Prefix(PlayerControl pc, ref bool __result)
    {
        if (pc.GetRoleClass() is not Plumber) return true;
        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.CoExitVent))]
internal static class PlumberExitVentPatch
{
    public static void Prefix(PlayerPhysics __instance, out bool __state) =>
        __state = Plumber.ShouldObserveExit(__instance.myPlayer);

    public static void Postfix(PlayerPhysics __instance, [HarmonyArgument(0)] int ventId,
        bool __state, ref Il2CppSystem.Collections.IEnumerator __result)
    {
        if (__state && __result != null)
            __result = AfterNativeExit(__result, __instance.myPlayer, ventId).WrapToIl2Cpp();
    }

    private static IEnumerator AfterNativeExit(Il2CppSystem.Collections.IEnumerator original,
        PlayerControl player, int ventId)
    {
        // Forward every native yield unchanged. Cancellation/exception never reaches the trigger.
        while (original.MoveNext()) yield return original.Current;
        Plumber.OnSuccessfulExit(player, ventId);
    }
}

[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.CoEnterVent))]
internal static class PlumberVentEnterPatch
{
    public static void Postfix(PlayerPhysics __instance) => Plumber.RecordVentEnter(__instance.myPlayer);
}

[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.BootFromVent))]
internal static class PlumberForcedVentExitPatch
{
    public static void Prefix(PlayerPhysics __instance) => Plumber.RecordForcedVentExit(__instance.myPlayer);
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
internal static class PlumberMeetingPatch
{
    public static void Prefix() => Plumber.ClearTransientEffects();
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
internal static class PlumberMeetingFallbackPatch
{
    public static void Prefix() => Plumber.ClearTransientEffects();
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
internal static class PlumberGameEndPatch
{
    public static void Prefix() => Plumber.EndGame();
}