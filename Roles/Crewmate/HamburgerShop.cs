using AmongUs.GameOptions;

using TownOfHostForE.Roles.Core;

namespace TownOfHostForE.Roles.Crewmate;

// HAMBURGER_SHOP_PORT_PERMANENT: SNR SHR方式をTOH4Eのホスト専用構成へ局所移植
public sealed class HamburgerShop : RoleBase
{
    public static readonly SimpleRoleInfo RoleInfo =
        SimpleRoleInfo.Create(
            typeof(HamburgerShop),
            player => new HamburgerShop(player),
            CustomRoles.HamburgerShop,
            () => RoleTypes.Crewmate,
            CustomRoleTypes.Crewmate,
            41600,
            SetupOptionItem,
            "hb",
            "#ff8040",
            assignInfo: new(CustomRoles.HamburgerShop, CustomRoleTypes.Crewmate)
            {
                // MakeBurger実タスクが存在するAirshipでのみ初期配役を許可する
                IsInitiallyAssignableCallBack =
                    () => (MapNames)Main.NormalOptions.MapId == MapNames.Airship,
            }
        );

    private static OptionItem OptionUseCustomTaskCount = null!;
    private static OptionItem OptionCommonTasks = null!;
    private static OptionItem OptionShortTasks = null!;
    private static OptionItem OptionLongTasks = null!;

    private enum OptionName
    {
        HamburgerShopUseCustomTaskCount,
        HamburgerShopCommonTasks,
        HamburgerShopShortTasks,
        HamburgerShopLongTasks,
    }

    public HamburgerShop(PlayerControl player)
        : base(RoleInfo, player)
    { }

    private static void SetupOptionItem()
    {
        OptionUseCustomTaskCount = BooleanOptionItem.Create(
            RoleInfo,
            10,
            OptionName.HamburgerShopUseCustomTaskCount,
            true,
            false);

        OptionCommonTasks = IntegerOptionItem.Create(
                RoleInfo,
                11,
                OptionName.HamburgerShopCommonTasks,
                new(0, 12, 1),
                1,
                false)
            .SetParent(OptionUseCustomTaskCount)
            .SetValueFormat(OptionFormat.Pieces);

        OptionShortTasks = IntegerOptionItem.Create(
                RoleInfo,
                12,
                OptionName.HamburgerShopShortTasks,
                new(0, 69, 1),
                1,
                false)
            .SetParent(OptionUseCustomTaskCount)
            .SetValueFormat(OptionFormat.Pieces);

        OptionLongTasks = IntegerOptionItem.Create(
                RoleInfo,
                13,
                OptionName.HamburgerShopLongTasks,
                new(0, 45, 1),
                1,
                false)
            .SetParent(OptionUseCustomTaskCount)
            .SetValueFormat(OptionFormat.Pieces);
    }

    public static int GetAssignedTaskCount(int defaultTaskCount)
    {
        if (!OptionUseCustomTaskCount.GetBool())
        {
            // SNR 3.1.4.4公式修正:
            // 個別タスク数を設定しない場合も元の配布件数を維持する
            return defaultTaskCount;
        }

        return OptionCommonTasks.GetInt()
            + OptionShortTasks.GetInt()
            + OptionLongTasks.GetInt();
    }

    public static bool TryGetMakeBurgerTaskId(out byte taskId)
    {
        taskId = 0;

        if ((MapNames)Main.NormalOptions.MapId != MapNames.Airship
            || ShipStatus.Instance == null)
        {
            return false;
        }

        foreach (var task in ShipStatus.Instance.ShortTasks)
        {
            if (task != null && task.TaskType == TaskTypes.MakeBurger)
            {
                taskId = (byte)task.Index;
                return true;
            }
        }

        return false;
    }
}
