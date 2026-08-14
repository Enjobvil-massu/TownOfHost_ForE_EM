using System.Linq;
using System.Text;
using UnityEngine;
using AmongUs.GameOptions;

namespace TownOfHostForE.Roles.Core.Descriptions;

public sealed class RoleHelpSettingLine
{
    public RoleHelpSettingLine(string namePlain, string valueRich)
    {
        NamePlain = namePlain;
        ValueRich = valueRich;
        ValuePlain = valueRich.RemoveHtmlTags();
    }

    public string NamePlain { get; }
    public string ValueRich { get; }
    public string ValuePlain { get; }
    public string PlainText => $"{NamePlain}: {ValuePlain}";
}

public sealed class RoleHelpSections
{
    public RoleHelpSections(
        string roleNamePlain,
        Color roleNameReadableColor,
        string factionLabelPlain,
        string factionNamePlain,
        Color factionColor,
        string abilityPlain,
        string settingsHeaderPlain,
        RoleHelpSettingLine[] settingLines)
    {
        RoleNamePlain = roleNamePlain;
        RoleNameReadableColor = roleNameReadableColor;
        FactionLabelPlain = factionLabelPlain;
        FactionNamePlain = factionNamePlain;
        FactionColor = factionColor;
        AbilityPlain = abilityPlain;
        SettingsHeaderPlain = settingsHeaderPlain;
        SettingLines = settingLines;
        SettingLinesPlain = settingLines.Select(line => line.PlainText).ToArray();
    }

    public string RoleNamePlain { get; }
    public Color RoleNameReadableColor { get; }
    public string FactionLabelPlain { get; }
    public string FactionNamePlain { get; }
    public Color FactionColor { get; }
    public string AbilityPlain { get; }
    public string SettingsHeaderPlain { get; }
    public RoleHelpSettingLine[] SettingLines { get; }
    public string[] SettingLinesPlain { get; }
}

public abstract class RoleDescription
{
    public RoleDescription(SimpleRoleInfo roleInfo)
    {
        RoleInfo = roleInfo;
    }

    public SimpleRoleInfo RoleInfo { get; }
    /// <summary>イントロなどで表示される短い文</summary>
    public abstract string Blurb { get; }
    /// <summary>
    /// ヘルプコマンドで使用される長い説明文<br/>
    /// AmongUs2023.7.12時点で，Impostor, Crewmateに関してはバニラ側でロング説明文が未実装のため「タスクを行う」と表示される
    /// </summary>
    public abstract string Description { get; }
    public string FullFormatHelp
    {
        get
        {
            //var builder = new StringBuilder(256);
            var builder = new StringBuilder();
            // 役職名と説明文
            builder.AppendFormat("<size={0}>\n", BlankLineSize);
            builder.AppendFormat("<size={0}>{1}\n", FirstHeaderSize, Translator.GetRoleString(RoleInfo.RoleName.ToString()).Color(RoleInfo.RoleColor.ToReadableColor()));
            // 陣営
            //   マッドメイトはインポスター陣営
            var roleTeam = RoleInfo.CustomRoleType == CustomRoleTypes.Madmate ? CustomRoleTypes.Impostor : RoleInfo.CustomRoleType;
            Color roleColor = RoleInfo.CustomRoleType == CustomRoleTypes.Crewmate ? Utils.GetRoleColor(CustomRoles.Crewmate) : Utils.GetRoleColor(RoleInfo.RoleName);
            builder.AppendFormat("<size={0}>{1}\n", ThirdHeaderSize, $"陣営：{Utils.ColorString(roleColor,Translator.GetString($"CustomRoleTypes.{roleTeam}"))}");
            builder.AppendFormat("<size={0}>\n", BlankLineSize);
            builder.AppendFormat("<size={0}>{1}\n", BodySize, Description);
            builder.AppendFormat("<size={0}>\n", BlankLineSize);
            //設定
            builder.AppendFormat("<size={0}>{1}\n", ThirdHeaderSize, $"【設定】");
            foreach (var opt in Options.CustomRoleSpawnChances[RoleInfo.RoleName].Children.Select((v, i) => new { Value = v, Index = i + 1 }))
            {
                builder.Append($"{opt.Value.GetName(true).RemoveHtmlTags()}: {opt.Value.GetString()}\n");
            }

            return builder.ToString();
        }
    }

    public RoleHelpSections NetworkHelpSections
    {
        get
        {
            var roleTeam = RoleInfo.CustomRoleType == CustomRoleTypes.Madmate
                ? CustomRoleTypes.Impostor
                : RoleInfo.CustomRoleType;
            Color factionColor = RoleInfo.CustomRoleType == CustomRoleTypes.Crewmate
                ? Utils.GetRoleColor(CustomRoles.Crewmate)
                : Utils.GetRoleColor(RoleInfo.RoleName);
            var settingLines = Options.CustomRoleSpawnChances[RoleInfo.RoleName]
                .Children
                .Select(option => new RoleHelpSettingLine(
                    option.GetName(true).RemoveHtmlTags(),
                    option.GetString()))
                .ToArray();

            return new RoleHelpSections(
                Translator.GetRoleString(RoleInfo.RoleName.ToString()).RemoveHtmlTags(),
                RoleInfo.RoleColor.ToReadableColor(),
                "陣営：",
                Translator.GetString($"CustomRoleTypes.{roleTeam}").RemoveHtmlTags(),
                factionColor,
                Description.RemoveHtmlTags(),
                "【設定】",
                settingLines);
        }
    }

    public const string FirstHeaderSize = "130%";
    public const string SecondHeaderSize = "100%";
    public const string ThirdHeaderSize = "80%";
    public const string BodySize = "70%";
    public const string BlankLineSize = "30%";
}
