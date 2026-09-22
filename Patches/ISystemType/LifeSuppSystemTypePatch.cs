using HarmonyLib;
using Hazel;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Interfaces;
using TownOfHostForE.Roles.Neutral;

namespace TownOfHostForE.Patches.ISystemType;

[HarmonyPatch(typeof(LifeSuppSystemType), nameof(LifeSuppSystemType.UpdateSystem))]
public static class LifeSuppSystemUpdateSystemPatch
{
    public static bool Prefix(LifeSuppSystemType __instance, [HarmonyArgument(0)] PlayerControl player, [HarmonyArgument(1)] MessageReader msgReader)
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
        if (isMadmate && !Options.MadmateCanFixOxygen.GetBool())
        {
            return false;
        }

        if (playerRole is ISystemTypeUpdateHook systemTypeUpdateHook && !systemTypeUpdateHook.UpdateLifeSuppSystem(__instance, amount))
        {
            return false;
        }
        return true;
    }
}
