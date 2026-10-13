using AmongUs.GameOptions;

using TownOfHostForE.Roles.Core;

namespace TownOfHostForE.Roles.Vanilla;

// INFLUENCER_V19_NATIVE_GHOST_BRIDGE
// SpiritGuide is Among Us v19's internal RoleTypes name for Influencer.
// This is only TOH4E_EM's identity wrapper around the native role.
// It must never enter the normal initial living-role assignment pool.
public sealed class Influencer : RoleBase
{
    public static readonly SimpleRoleInfo RoleInfo =
        SimpleRoleInfo.CreateForVanilla(
            typeof(Influencer),
            player => new Influencer(player),
            RoleTypes.SpiritGuide,
            "#8cffff",
            assignInfo: new RoleAssignInfo(
                CustomRoles.Influencer,
                CustomRoleTypes.Crewmate)
            {
                IsInitiallyAssignableCallBack = () => false
            }
        );

    public Influencer(PlayerControl player)
        : base(
            RoleInfo,
            player
        )
    { }
}
