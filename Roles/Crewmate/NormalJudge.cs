using AmongUs.GameOptions;
using TownOfHostForE.Roles.Core;

namespace TownOfHostForE.Roles.Crewmate;

public sealed class NormalJudge : RoleBase
{
    public static readonly SimpleRoleInfo RoleInfo =
        SimpleRoleInfo.Create(
            typeof(NormalJudge),
            player => new NormalJudge(player),
            CustomRoles.NormalJudge,
            () => RoleTypes.Judge,
            CustomRoleTypes.Crewmate,
            2200,
            SetupOptionItem,
            "ジャッジ"
        );

    public NormalJudge(PlayerControl player)
        : base(RoleInfo, player)
    {
        taskRequirementPercentage =
            OptionTaskRequirementPercentage.GetFloat();
    }

    public enum OptionName
    {
        JudgeTaskRequirement,
    }

    private static OptionItem OptionTaskRequirementPercentage = null!;
    private static float taskRequirementPercentage;

    public static void SetupOptionItem()
    {
        OptionTaskRequirementPercentage =
            FloatOptionItem.Create(
                RoleInfo,
                3,
                OptionName.JudgeTaskRequirement,
                new(0f, 100f, 25f),
                50f,
                false
            )
            .SetValueFormat(OptionFormat.Percent);
    }

    public override void ApplyGameOptions(IGameOptions opt)
    {
        if (opt.TryCast<NormalGameOptionsV12>(out var normalOptions)
            && normalOptions.roleOptions.TryGetRoleOptions<JudgeRoleOptionsV11>(
                RoleTypes.Judge,
                out var judgeOptions))
        {
            judgeOptions.JudgeTaskRequirementPercentage =
                taskRequirementPercentage;
        }
    }
}
