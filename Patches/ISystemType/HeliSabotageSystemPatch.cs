using HarmonyLib;
using Hazel;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Interfaces;
using TownOfHostForE.Roles.Neutral;

namespace TownOfHostForE.Patches.ISystemType;

[HarmonyPatch(typeof(HeliSabotageSystem), nameof(HeliSabotageSystem.UpdateSystem))]
public static class HeliSabotageSystemUpdateSystemPatch
{
    public static bool Prefix(HeliSabotageSystem __instance, [HarmonyArgument(0)] PlayerControl player, [HarmonyArgument(1)] MessageReader msgReader)
    {
        byte amount;
        {
            var newReader = MessageReader.Get(msgReader);
            amount = newReader.ReadByte();
            newReader.Recycle();
        }

        // MADMATE_SABOTAGE_REPAIR_OPTIONS_SERVER_PARITY_AB_V1:
        // Server-authoritative repair rejection is shared by MOD and completely Vanilla participants.
        var playerRole = player.GetRoleClass();
        var isMadmate =
            player.Is(CustomRoleTypes.Madmate) ||
            (playerRole is SchrodingerCat schrodingerCat && schrodingerCat.AmMadmate);
        if (isMadmate && !Options.MadmateCanFixHeliSabotage.GetBool())
        {
            return false;
        }

        if (playerRole is ISystemTypeUpdateHook systemTypeUpdateHook && !systemTypeUpdateHook.UpdateHeliSabotageSystem(__instance, amount))
        {
            return false;
        }
        return true;
    }
}

//参考
//https://github.com/Koke1024/Town-Of-Moss/blob/main/TownOfMoss/Patches/MeltDownBoost.cs

[HarmonyPatch(typeof(HeliSabotageSystem), nameof(HeliSabotageSystem.Deteriorate))]
public static class HeliSabotageSystemPatch
{
    public static void Prefix(HeliSabotageSystem __instance)
    {
        if (!__instance.IsActive || !Options.SabotageTimeControl.GetBool())
            return;
        if (AirshipStatus.Instance != null)
            if (__instance.Countdown >= Options.AirshipReactorTimeLimit.GetFloat())
                __instance.Countdown = Options.AirshipReactorTimeLimit.GetFloat();
    }
}
