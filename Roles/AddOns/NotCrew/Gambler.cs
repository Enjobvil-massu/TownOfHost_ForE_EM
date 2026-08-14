using System.Collections.Generic;
using UnityEngine;
using TownOfHostForE.Roles.Core;
using static TownOfHostForE.Options;
using static TownOfHostForE.Translator;
using TownOfHostForE.Attributes;

namespace TownOfHostForE.Roles.AddOns.NotCrew;

public static class Gambler
{
    private static readonly int Id = 85500;
    private static Color RoleColor = Utils.GetRoleColor(CustomRoles.Gambler);
    public static string SubRoleMark = Utils.ColorString(RoleColor, "$");
    private static List<byte> playerIdList = new();

    public static OptionItem GaCanGuessTime;
    public static OptionItem GaCanGuessVanilla;
    public static OptionItem GaCanGuessTaskDoneSnitch;
    public static OptionItem ChangeGuessDeathReason;
    public static OptionItem GaCantWhiteCrew;

    // GAMBLER_FACTION_LIMIT_OPTIONS_AB_V1:
    // 5陣営すべて初期値0。既存Maximumは総付与上限として維持する。
    public static OptionItem ImpostorMaximum = null!;
    public static OptionItem MadmateMaximum = null!;
    public static OptionItem NeutralMaximum = null!;
    public static OptionItem CrewmateMaximum = null!;
    public static OptionItem AnimalsMaximum = null!;

    enum OptionName
    {
        GuesserCanGuessTimes,
        EGCanGuessImp,
        EGCanGuessVanilla,
        EGCanGuessTaskDoneSnitch,
        EGGuesserTryHideMsg,
        ChangeGuessDeathReason,
        GuessCantWhiteCrew,
    }
    public static void SetupCustomOption()
    {
        SetupRoleOptions(Id, TabGroup.Addons, CustomRoles.Gambler,RoleColor);

        // GAMBLER_UI_CLEANUP_PERMANENT:
        // SetupRoleOptionsが作るCustomRoleCounts登録は互換性のため保持し、
        // Gamblerでは実配役に使わないMaximumだけUIから隠す。
        CustomRoleCounts[CustomRoles.Gambler].SetHidden(true);

        // GAMBLER_FACTION_LIMIT_OPTIONS_AB_V1:
        ImpostorMaximum = IntegerOptionItem.Create(Id + 20, "%roleTypes%Maximum", new(0, 3, 1), 0, TabGroup.Addons, false)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Gambler])
            .SetValueFormat(OptionFormat.Players);
        ImpostorMaximum.ReplacementDictionary = new Dictionary<string, string>
        {
            { "%roleTypes%", Utils.ColorString(Palette.ImpostorRed, GetString("TeamImpostor")) }
        };

        MadmateMaximum = IntegerOptionItem.Create(Id + 21, "%roleTypes%Maximum", new(0, 15, 1), 0, TabGroup.Addons, false)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Gambler])
            .SetValueFormat(OptionFormat.Players);
        MadmateMaximum.ReplacementDictionary = new Dictionary<string, string>
        {
            { "%roleTypes%", Utils.ColorString(Utils.GetRoleColor(CustomRoles.Madmate), GetString("Madmate")) }
        };

        NeutralMaximum = IntegerOptionItem.Create(Id + 22, "%roleTypes%Maximum", new(0, 15, 1), 0, TabGroup.Addons, false)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Gambler])
            .SetValueFormat(OptionFormat.Players);
        NeutralMaximum.ReplacementDictionary = new Dictionary<string, string>
        {
            { "%roleTypes%", Utils.ColorString(Palette.AcceptedGreen, GetString("Neutral")) }
        };

        CrewmateMaximum = IntegerOptionItem.Create(Id + 23, "%roleTypes%Maximum", new(0, 15, 1), 0, TabGroup.Addons, false)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Gambler])
            .SetValueFormat(OptionFormat.Players);
        CrewmateMaximum.ReplacementDictionary = new Dictionary<string, string>
        {
            { "%roleTypes%", Utils.ColorString(Palette.CrewmateBlue, GetString("TeamCrewmate")) }
        };

        AnimalsMaximum = IntegerOptionItem.Create(Id + 24, "%roleTypes%Maximum", new(0, 15, 1), 0, TabGroup.Addons, false)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Gambler])
            .SetValueFormat(OptionFormat.Players);
        AnimalsMaximum.ReplacementDictionary = new Dictionary<string, string>
        {
            { "%roleTypes%", Utils.ColorString(Palette.AcceptedGreen, GetString("Animals")) }
        };

        GaCanGuessTime = IntegerOptionItem.Create(Id + 10, OptionName.GuesserCanGuessTimes, new(1, 15, 1), 3, TabGroup.Addons, false)
                .SetValueFormat(OptionFormat.Players);
        GaCanGuessVanilla = BooleanOptionItem.Create(Id + 12, OptionName.EGCanGuessVanilla, false, TabGroup.Addons, false);
        GaCanGuessTaskDoneSnitch = BooleanOptionItem.Create(Id + 13, OptionName.EGCanGuessTaskDoneSnitch, false, TabGroup.Addons, false);
        ChangeGuessDeathReason = BooleanOptionItem.Create(Id + 15, OptionName.ChangeGuessDeathReason, false, TabGroup.Addons, false);
        GaCantWhiteCrew = BooleanOptionItem.Create(Id + 16, OptionName.GuessCantWhiteCrew, false, TabGroup.Addons, false);
    }
    [GameModuleInitializer]
    public static void Init()
    {
        playerIdList = new();
    }
    public static void Add(byte playerId)
    {
        // GOD_GAMBLER_REGISTRY_GUARD_PERMANENT: PlayerState上でGodならGambler内部登録も拒否
        if (PlayerState.GetByPlayerId(playerId)?.MainRole == CustomRoles.God) return;
        if (!playerIdList.Contains(playerId))
            playerIdList.Add(playerId);
    }
    // GOD_GAMBLER_REGISTRY_REMOVE_PERMANENT: God化時にSubRolesと内部IDリストを同時に解消する
    public static void Remove(byte playerId)
    {
        playerIdList.Remove(playerId);
    }
    public static bool IsEnable => playerIdList.Count > 0;
    public static bool IsThisRole(byte playerId) => playerIdList.Contains(playerId);

}