using AmongUs.GameOptions;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Interfaces;

namespace TownOfHostForE.Roles.Impostor;

public sealed class NormalPhantom : RoleBase, IImpostor
{
    public static readonly SimpleRoleInfo RoleInfo =
        SimpleRoleInfo.Create(
            typeof(NormalPhantom),
            player => new NormalPhantom(player),
            CustomRoles.NormalPhantom,
            () => RoleTypes.Phantom,
            CustomRoleTypes.Impostor,
            1150,
            SetupOptionItem,
            "ファントム"
        );

    public NormalPhantom(PlayerControl player)
        : base(RoleInfo, player)
    {
    }

    private static void SetupOptionItem()
    {
    }
}
