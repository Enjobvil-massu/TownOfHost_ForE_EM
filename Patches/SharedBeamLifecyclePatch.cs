using HarmonyLib;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Class;

namespace TownOfHostForE;

// Beam-only cleanup; no changes to meeting, death attribution or game-end decisions.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
internal static class SharedBeamBeforeMeetingPatch
{
    public static void Prefix()
    {
        if (SharedBeamController.CleanupAll())
            Utils.NotifyRoles(isForMeeting: true, ForceLoop: true);
    }
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
internal static class SharedBeamMeetingFallbackPatch
{
    public static void Prefix() => SharedBeamController.CleanupAll();
}

[HarmonyPatch(typeof(PlayerState), nameof(PlayerState.SetDead))]
internal static class SharedBeamDeathPatch
{
    public static void Postfix(PlayerState __instance, byte ___PlayerId)
    {
        if (__instance.IsDead) SharedBeamController.CleanupPlayer(___PlayerId);
    }
}

[HarmonyPatch(typeof(RoleBase), nameof(RoleBase.Dispose))]
internal static class SharedBeamDisposePatch
{
    public static void Prefix(RoleBase __instance)
    {
        if (__instance.Player != null) SharedBeamController.CleanupPlayer(__instance.Player.PlayerId);
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
internal static class SharedBeamGameEndPatch
{
    public static void Prefix() => SharedBeamController.CleanupAll();
}
