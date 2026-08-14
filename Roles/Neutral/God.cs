using System.Linq;
using UnityEngine;
using AmongUs.GameOptions;

using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Interfaces;

namespace TownOfHostForE.Roles.Neutral;

// GOD_ROLE_SOURCE_PERMANENT
// Ported and adapted from Town Of Host_Y v519.27.1 by Yumenopai.
// Town Of Host_Y credits SuperNewRoles as the original reference.
// Source license: GPL-3.0.
public sealed class God : RoleBase, ISystemTypeUpdateHook
{
    public static readonly SimpleRoleInfo RoleInfo =
        SimpleRoleInfo.Create(
            typeof(God),
            player => new God(player),
            CustomRoles.God,
            () => RoleTypes.Crewmate,
            CustomRoleTypes.Neutral,
            60800,
            SetupOptionItem,
            "神",
            "#ffff00"
        );

    public God(PlayerControl player)
        : base(
            RoleInfo,
            player,
            () => HasTask.ForRecompute
        )
    {
        taskCompleteToWin = OptionTaskCompleteToWin.GetBool();
        viewVoteFor = OptionViewVoteFor.GetBool();

        if (Player == null) return;

        foreach (var pc in Main.AllPlayerControls)
        {
            NameColorManager.Add(Player.PlayerId, pc.PlayerId);
        }
    }

    // GOD_NULLABILITY_BASELINE_FIX_PERMANENT: SetupOptionItemで必ず初期化されるフィールド
    private static OptionItem OptionTaskCompleteToWin = null!;
    private static OptionItem OptionViewVoteFor = null!;
    private static Options.OverrideTasksData Tasks = null!;

    private static bool taskCompleteToWin;
    private static bool viewVoteFor;

    public static void SetupOptionItem()
    {
        OptionTaskCompleteToWin = BooleanOptionItem.Create(
                60810,
                "GodTaskCompleteToWin",
                true,
                TabGroup.NeutralRoles,
                false
            )
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.God]);

        OptionViewVoteFor = BooleanOptionItem.Create(
                60811,
                "GodViewVoteFor",
                false,
                TabGroup.NeutralRoles,
                false
            )
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.God]);

        Tasks = Options.OverrideTasksData.Create(RoleInfo, 20);
    }

    public override void ApplyGameOptions(IGameOptions opt)
    {
        if (viewVoteFor)
        {
            opt.SetBool(BoolOptionNames.AnonymousVotes, false);
        }
    }

    public override void OverrideDisplayRoleNameAsSeer(
        PlayerControl seen,
        bool isMeeting,
        ref bool enabled,
        ref Color roleColor,
        ref string roleText)
    {
        enabled = true;
    }

    // GOD_WIN_ELIGIBILITY_SHARED_PERMANENT: 勝利成立判定と勝者追加判定で同一条件を使用
    public static bool IsWinEligible(PlayerControl player)
    {
        return player.Is(CustomRoles.God) &&
               (!taskCompleteToWin ||
                player.GetPlayerTaskState().IsTaskFinished);
    }

    public static bool CheckWin()
    {
        return Main.AllAlivePlayerControls.Any(IsWinEligible);
    }

    bool ISystemTypeUpdateHook.UpdateHudOverrideSystem(
        HudOverrideSystemType switchSystem,
        byte amount)
    {
        if ((amount & HudOverrideSystemType.DamageBit) <= 0)
        {
            return false;
        }

        return true;
    }

    bool ISystemTypeUpdateHook.UpdateHqHudSystem(
        HqHudSystemType hqHudSystemType,
        byte amount)
    {
        var tags =
            (HqHudSystemType.Tags)
            (amount & HqHudSystemType.TagMask);

        return tags != HqHudSystemType.Tags.FixBit;
    }

    bool ISystemTypeUpdateHook.UpdateSwitchSystem(
        SwitchSystem switchSystem,
        byte amount)
    {
        return false;
    }

    bool ISystemTypeUpdateHook.UpdateLifeSuppSystem(
        LifeSuppSystemType switchSystem,
        byte amount)
    {
        return false;
    }

    bool ISystemTypeUpdateHook.UpdateReactorSystem(
        ReactorSystemType switchSystem,
        byte amount)
    {
        return false;
    }

    bool ISystemTypeUpdateHook.UpdateHeliSabotageSystem(
        HeliSabotageSystem switchSystem,
        byte amount)
    {
        return false;
    }
}
