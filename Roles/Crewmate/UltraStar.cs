using AmongUs.GameOptions;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Interfaces;
using TownOfHostForE.Roles.Neutral;
using UnityEngine;

namespace TownOfHostForE.Roles.Crewmate;

public sealed class UltraStar : RoleBase, IKiller, ISchrodingerCatOwner
{
    public static readonly SimpleRoleInfo RoleInfo =
        SimpleRoleInfo.Create(
            typeof(UltraStar),
            player => new UltraStar(player),
            CustomRoles.UltraStar,
            () => OptionCanSeeKillCooldown!.GetBool() ? RoleTypes.Engineer : RoleTypes.Crewmate,
            CustomRoleTypes.Crewmate,
            41400,
            SetupOptionItem,
            "us",
            "#ffff8e"
        );

    public UltraStar(PlayerControl player)
        : base(RoleInfo, player)
    {
        Speed = OptionSpeed.GetFloat();
        CanKillByContact = OptionCanKill.GetBool();
        KillCooldown = OptionKillCooldown.GetFloat();
        OriginalColorId = player.Data?.DefaultOutfit.ColorId ?? 0;
        CanBeSeenByEveryone = OptionCanSeeAllPlayers.GetBool();
    }

    private static OptionItem OptionSpeed = null!;
    private static OptionItem OptionCanKill = null!;
    private static OptionItem OptionKillCooldown = null!;
    public static OptionItem OptionCheckKill = null!;
    private static OptionItem OptionCanSeeKillCooldown = null!;
    private static OptionItem OptionCanSeeAllPlayers = null!;

    private enum OptionName
    {
        AddSpeed,
        UltraStarCankill,
        UltraStarcheckkill,
        UltraStarVentCanseekillcool,
        UltraStarCanseeallplayer,
    }

    private float ColorChange;
    private readonly int OriginalColorId;
    private static bool CanBeSeenByEveryone;
    private static float Speed;
    private static bool CanKillByContact;
    private float KillCooldown;

    public static bool IgnoresRoleGuards => !OptionCheckKill.GetBool();

    private static void SetupOptionItem()
    {
        OptionSpeed = FloatOptionItem.Create(
                RoleInfo,
                9,
                OptionName.AddSpeed,
                new(0f, 5f, 0.25f),
                2.0f,
                false)
            .SetValueFormat(OptionFormat.Multiplier);

        OptionCanSeeAllPlayers = BooleanOptionItem.Create(
            RoleInfo,
            14,
            OptionName.UltraStarCanseeallplayer,
            false,
            false);

        OptionCanKill = BooleanOptionItem.Create(
            RoleInfo,
            10,
            OptionName.UltraStarCankill,
            false,
            false);

        OptionKillCooldown = FloatOptionItem.Create(
                RoleInfo,
                13,
                GeneralOption.KillCooldown,
                new(0f, 180f, 0.5f),
                30f,
                false,
                OptionCanKill)
            .SetUiStepMultiplier(2)
            .SetValueFormat(OptionFormat.Seconds);

        OptionCheckKill = BooleanOptionItem.Create(
            RoleInfo,
            11,
            OptionName.UltraStarcheckkill,
            false,
            false,
            OptionCanKill);

        OptionCanSeeKillCooldown = BooleanOptionItem.Create(
            RoleInfo,
            12,
            OptionName.UltraStarVentCanseekillcool,
            false,
            false,
            OptionCanKill);
    }

    public override void Add()
    {
        Main.AllPlayerSpeed[Player.PlayerId] += Speed;
    }

    public override void OnFixedUpdate(PlayerControl player)
    {
        if (!AmongUsClient.Instance.AmHost
            || !GameStates.IsInTask
            || !player.IsAlive()
            || !MyState.HasSpawned)
        {
            return;
        }

        ColorChange %= 18;
        if (ColorChange is >= 0 and < 1) player.RpcSetColor(8);
        else if (ColorChange is >= 1 and < 2) player.RpcSetColor(1);
        else if (ColorChange is >= 2 and < 3) player.RpcSetColor(10);
        else if (ColorChange is >= 3 and < 4) player.RpcSetColor(2);
        else if (ColorChange is >= 4 and < 5) player.RpcSetColor(11);
        else if (ColorChange is >= 5 and < 6) player.RpcSetColor(14);
        else if (ColorChange is >= 6 and < 7) player.RpcSetColor(5);
        else if (ColorChange is >= 7 and < 8) player.RpcSetColor(4);
        else if (ColorChange is >= 8 and < 9) player.RpcSetColor(17);
        else if (ColorChange is >= 9 and < 10) player.RpcSetColor(0);
        else if (ColorChange is >= 10 and < 11) player.RpcSetColor(3);
        else if (ColorChange is >= 11 and < 12) player.RpcSetColor(13);
        else if (ColorChange is >= 12 and < 13) player.RpcSetColor(7);
        else if (ColorChange is >= 13 and < 14) player.RpcSetColor(15);
        else if (ColorChange is >= 14 and < 15) player.RpcSetColor(6);
        else if (ColorChange is >= 15 and < 16) player.RpcSetColor(12);
        else if (ColorChange is >= 16 and < 17) player.RpcSetColor(9);
        else if (ColorChange is >= 17 and < 18) player.RpcSetColor(16);
        ColorChange += Time.fixedDeltaTime * 1.5f;

        if (!CanKillByContact)
        {
            return;
        }

        KillCooldown -= Time.fixedDeltaTime;
        Vector2 ultraStarPosition = player.transform.position;
        PlayerControl? target = null;
        const float killRange = 0.4f;

        foreach (var candidate in Main.AllAlivePlayerControls)
        {
            if (candidate.PlayerId == player.PlayerId)
            {
                continue;
            }

            float targetDistance =
                Vector2.Distance(ultraStarPosition, candidate.transform.position);

            if (targetDistance <= killRange
                && player.CanMove
                && candidate.CanMove)
            {
                target = candidate;
                break;
            }
        }

        if (target == null || KillCooldown > 0)
        {
            return;
        }

        KillCooldown = OptionKillCooldown.GetFloat();
        CustomRoleManager.OnCheckMurder(Player, target, Player, target);
        Utils.MarkEveryoneDirtySettings();
        Player.RpcResetAbilityCooldown();
    }

    public override void ApplyGameOptions(IGameOptions opt)
    {
        AURoleOptions.EngineerCooldown = KillCooldown;
    }

    public override void OnSpawn(bool initialState = false)
    {
        if (!CanKillByContact)
        {
            return;
        }

        KillCooldown = OptionKillCooldown.GetFloat() + 1.5f;
        Player.RpcResetAbilityCooldown();
    }

    public override bool OnReportDeadBody(
        PlayerControl reporter,
        NetworkedPlayerInfo target)
    {
        Player.RpcSetColor((byte)OriginalColorId);
        return true;
    }

    public override void OverrideDisplayRoleNameAsSeen(
        PlayerControl seer,
        bool isMeeting,
        ref bool enabled,
        ref Color roleColor,
        ref string roleText)
    {
        enabled |= CanBeSeenByEveryone;
    }

    public override string GetAbilityButtonText()
        => Translator.GetString(StringNames.KillLabel);

    public bool CanUseSabotageButton() => false;
    bool IKiller.CanUseImpostorVentButton() => false;
    bool IKiller.CanKill => false;
    bool IKiller.IsKiller => true;

    public override bool OnEnterVent(PlayerPhysics physics, int ventId) => false;

    public SchrodingerCat.TeamType SchrodingerCatChangeTo
        => SchrodingerCat.TeamType.Crew;
}
