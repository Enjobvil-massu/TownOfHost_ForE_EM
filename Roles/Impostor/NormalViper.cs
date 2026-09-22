using AmongUs.GameOptions;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Class;
using TownOfHostForE.Roles.Core.Interfaces;

namespace TownOfHostForE.Roles.Impostor;

public sealed class NormalViper : RoleBase, IImpostor
{
    public static readonly SimpleRoleInfo RoleInfo =
        SimpleRoleInfo.Create(
            typeof(NormalViper),
            player => new NormalViper(player),
            CustomRoles.NormalViper,
            () => RoleTypes.Viper,
            CustomRoleTypes.Impostor,
            1200,
            SetupOptionItem,
            "バイパー"
        );

    public NormalViper(PlayerControl player)
        : base(RoleInfo, player)
    {
        dissolveTime = OptionDissolveTime.GetFloat();
    }

    public enum OptionName
    {
        ViperDissolveTime,
    }

    private static OptionItem OptionDissolveTime = null!;
    private static float dissolveTime;

    public static void SetupOptionItem()
    {
        OptionDissolveTime =
            FloatOptionItem.Create(
                RoleInfo,
                3,
                OptionName.ViperDissolveTime,
                new(5f, 60f, 5f),
                20f,
                false
            )
            .SetValueFormat(OptionFormat.Seconds);
    }

    // VIPER_OBSERVER_DISSOLVE_OPTIONS_AB_V1:
    // ViperDeadBody is constructed locally on every observing client.
    // Therefore every client needs the configured dissolve duration, not only
    // the player whose custom main role is NormalViper.
    internal static void ApplyObserverDissolveOptionsAbV1(IGameOptions opt)
    {
        if (opt.TryCast<NormalGameOptionsV11>(out var normalOptions)
            && normalOptions.roleOptions.TryGetRoleOptions<ViperRoleOptionsV11>(
                RoleTypes.Viper,
                out var viperOptions))
        {
            viperOptions.ViperDissolveTime = OptionDissolveTime.GetFloat();
        }
    }

    public override void ApplyGameOptions(IGameOptions opt)
    {
        if (opt.TryCast<NormalGameOptionsV11>(out var normalOptions)
            && normalOptions.roleOptions.TryGetRoleOptions<ViperRoleOptionsV11>(
                RoleTypes.Viper,
                out var viperOptions))
        {
            viperOptions.ViperDissolveTime = dissolveTime;
        }
    }
}
