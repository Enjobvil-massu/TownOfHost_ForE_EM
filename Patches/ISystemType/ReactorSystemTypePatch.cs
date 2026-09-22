using HarmonyLib;
using Hazel;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Interfaces;
using TownOfHostForE.Roles.Neutral;

namespace TownOfHostForE.Patches.ISystemType;

[HarmonyPatch(typeof(ReactorSystemType), nameof(ReactorSystemType.UpdateSystem))]
public static class ReactorSystemTypeUpdateSystemPatch
{
    public static bool Prefix(ReactorSystemType __instance, [HarmonyArgument(0)] PlayerControl player, [HarmonyArgument(1)] MessageReader msgReader, ref byte __state /* amount */)
    {
        byte amount;
        {
            var newReader = MessageReader.Get(msgReader);
            amount = newReader.ReadByte();
            newReader.Recycle();
        }
        __state = amount;

        // MADMATE_SABOTAGE_REPAIR_OPTIONS_SERVER_PARITY_AB_V1:
        // Server-authoritative repair rejection is shared by MOD and completely Vanilla participants.
        var playerRole = player.GetRoleClass();
        var isMadmate =
            player.Is(CustomRoleTypes.Madmate) ||
            (playerRole is SchrodingerCat schrodingerCat && schrodingerCat.AmMadmate);
        if (isMadmate && !Options.MadmateCanFixReactor.GetBool())
        {
            return false;
        }

        if (playerRole is ISystemTypeUpdateHook systemTypeUpdateHook && !systemTypeUpdateHook.UpdateReactorSystem(__instance, amount))
        {
            return false;
        }
        return true;
    }
    public static void Postfix(ReactorSystemType __instance, byte __state /* amount */ )
    {
        // サボタージュ発動時
        if (__state == ReactorSystemType.StartCountdown)
        {
            if (!Options.SabotageTimeControl.GetBool())
            {
                return;
            }
            var duration = (MapNames)Main.NormalOptions.MapId switch
            {
                MapNames.Polus => Options.PolusReactorTimeLimit.GetFloat(),
                MapNames.Fungle => Options.FungleReactorTimeLimit.GetFloat(),
                _ => float.NaN,
            };
            if (!float.IsNaN(duration))
            {
                __instance.Countdown = duration;
            }
        }
    }
}
