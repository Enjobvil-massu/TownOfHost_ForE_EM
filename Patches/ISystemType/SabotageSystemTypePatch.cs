using HarmonyLib;
using Hazel;
using TownOfHostForE.Attributes;
using TownOfHostForE.Modules;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Interfaces;
using TownOfHostForE.Roles.Neutral;

namespace TownOfHostForE.Patches.ISystemType;

[HarmonyPatch(typeof(SabotageSystemType), nameof(SabotageSystemType.UpdateSystem))]
public static class SabotageSystemTypeUpdateSystemPatch
{
    private static bool isCooldownModificationEnabled;
    private static float modifiedCooldownSec;
    private static readonly LogHandler logger = Logger.Handler(nameof(SabotageSystemType));

    [GameModuleInitializer]
    public static void Initialize()
    {
        isCooldownModificationEnabled = Options.ModifySabotageCooldown.GetBool();
        modifiedCooldownSec = Options.SabotageCooldown.GetFloat();
    }

    public static bool Prefix([HarmonyArgument(0)] PlayerControl player, [HarmonyArgument(1)] MessageReader msgReader)
    {
        byte amount;
        {
            var newReader = MessageReader.Get(msgReader);
            amount = newReader.ReadByte();
            newReader.Recycle();
        }

        var nextSabotage = (SystemTypes)amount;
        logger.Info($"PlayerName: {player.GetNameWithRole()}, SabotageType: {nextSabotage}");

        // ROLEBUGTRACE_STAGE1B_PERMANENT
        bool TraceReturn(bool accepted, string reason)
        {
            RoleBugTrace.Sabotage(player, nextSabotage, accepted, reason);
            return accepted;
        }

        // RENEGADE_R2G_GLOBAL_SABOTAGE_LOCKDOWN
        // 新規サボタージュ要求をHost authorityで拒否。追加の周期RPCは送らない。
        if (Renegade.IsAwakeningActive())
            return TraceReturn(false, "RenegadeAwakeningLockdown");

        //HASモードではサボタージュ不可
        if (Options.CurrentGameMode == CustomGameMode.HideAndSeek || Options.IsStandardHAS) return TraceReturn(false, "GameMode");

        if (!CustomRoleManager.OnSabotage(player, nextSabotage))
        {
            return TraceReturn(false, "CustomRoleManager.OnSabotage");
        }

        if (DarkGameMaster.InDeathGamePenalty(player)== false) return TraceReturn(false, "DeathGamePenalty");

        var roleClass = player.GetRoleClass();
        if (roleClass is IKiller killer)
        {
            //そもそもサボタージュボタン使用不可ならサボタージュ不可
            if (!killer.CanUseSabotageButton()) return TraceReturn(false, "CanUseSabotageButton");
            //その他処理が必要であれば処理
            var accepted = roleClass.OnInvokeSabotage(nextSabotage);
            return TraceReturn(accepted, "IKiller.OnInvokeSabotage");
        }
        else
        {
            var accepted = CanSabotage(player);
            return TraceReturn(accepted, "CustomRoleTypes.Impostor");
        }
    }
    private static bool CanSabotage(PlayerControl player)
    {
        //サボタージュ出来ないキラー役職はサボタージュ自体をキャンセル
        if (!player.Is(CustomRoleTypes.Impostor))
        {
            return false;
        }
        return true;
    }
    public static void Postfix(SabotageSystemType __instance, bool __runOriginal /* Prefixの結果，本体処理が実行されたかどうか */ )
    {
        if (!__runOriginal || !isCooldownModificationEnabled || !AmongUsClient.Instance.AmHost)
        {
            return;
        }
        // サボタージュクールダウンを変更
        __instance.Timer = modifiedCooldownSec;
        __instance.IsDirty = true;
    }
}

[HarmonyPatch(typeof(ElectricTask), nameof(ElectricTask.Initialize))]
public static class ElectricTaskInitializePatch
{
    public static void Postfix()
    {
        Utils.MarkEveryoneDirtySettings();
        if (!GameStates.IsMeeting)
            Utils.NotifyRoles(ForceLoop: true);
    }
}
[HarmonyPatch(typeof(ElectricTask), nameof(ElectricTask.Complete))]
public static class ElectricTaskCompletePatch
{
    public static void Postfix()
    {
        Utils.MarkEveryoneDirtySettings();
        if (!GameStates.IsMeeting)
            Utils.NotifyRoles(ForceLoop: true);
    }
}
