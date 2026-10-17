using System.Collections.Generic;
using AmongUs.GameOptions;
using HarmonyLib;
using TownOfHostForE.Attributes;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Interfaces;

namespace TownOfHostForE.Roles.Impostor;

public sealed class EvilDyer : RoleBase, IImpostor
{
    public static readonly SimpleRoleInfo RoleInfo =
        SimpleRoleInfo.Create(
            typeof(EvilDyer),
            player => new EvilDyer(player),
            CustomRoles.EvilDyer,
            () => RoleTypes.Impostor,
            CustomRoleTypes.Impostor,
            61200,
            SetupOptionItem,
            "イビル真っ赤",
            countType: CountTypes.Impostor
        );

    private static OptionItem OptionKillCooldown = null!;
    private static OptionItem OptionDyerTime = null!;
    private static bool IsColorCamouflage;
    private static int DyeGeneration;
    private static readonly HashSet<byte> DyedPlayerIds = new();

    private readonly float killCooldown;
    private readonly float dyerTime;

    private enum OptionName
    {
        EvilDyerDyeTime,
    }

    public EvilDyer(PlayerControl player)
        : base(RoleInfo, player, () => HasTask.False)
    {
        killCooldown = OptionKillCooldown.GetFloat();
        dyerTime = OptionDyerTime.GetFloat();
    }

    [GameModuleInitializer]
    public static void Init()
    {
        IsColorCamouflage = false;
        DyeGeneration = 0;
        DyedPlayerIds.Clear();
    }

    private static void SetupOptionItem()
    {
        OptionKillCooldown = FloatOptionItem.Create(
            RoleInfo, 10, GeneralOption.KillCooldown,
            new(0f, 180f, 2.5f), 30f, false)
            .SetValueFormat(OptionFormat.Seconds);

        OptionDyerTime = FloatOptionItem.Create(
            RoleInfo, 11, OptionName.EvilDyerDyeTime,
            new(0.5f, 30f, 0.5f), 7f, false)
            .SetValueFormat(OptionFormat.Seconds);
    }

    public float CalculateKillCooldown() => killCooldown;

    public void OnMurderPlayerAsKiller(MurderInfo info)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!info.DoKill || info.IsSuicide || info.IsFakeSuicide) return;
        if (info.AttemptKiller == null || info.AttemptKiller.PlayerId != Player.PlayerId) return;

        StartDye(dyerTime, info.AttemptTarget.PlayerId);
    }

    public override bool OnReportDeadBody(PlayerControl reporter, NetworkedPlayerInfo target)
    {
        if (AmongUsClient.Instance.AmHost) ClearDye("ReportDeadBody");
        return true;
    }

    public override void OnStartMeeting()
    {
        if (AmongUsClient.Instance.AmHost) ClearDye("StartMeeting");
    }

    private static void StartDye(float duration, byte killedTargetId)
    {
        DyeGeneration++;
        int generation = DyeGeneration;
        if (!IsColorCamouflage) DyedPlayerIds.Clear();
        IsColorCamouflage = true;
        int count = 0;

        foreach (var target in Main.AllAlivePlayerControls)
        {
            if (target == null || target.Data == null || target.Data.Disconnected || target.PlayerId == killedTargetId) continue;
            DyedPlayerIds.Add(target.PlayerId);
            ApplyRedBody(target, "Start");
            count++;
        }

        Logger.Info($"Stage=DYE_START | Generation={generation} | Duration={duration:0.0} | LivingTargets={count}", nameof(EvilDyer));
        // STABILITY_R1: Costume sync above already sends the necessary outfit
        // RPCs. Only changed nameplates need syncing; avoid force-resending
        // unchanged names to every Vanilla viewer (O(players^2) RPC burst).
        Utils.NotifyRoles();

        _ = new LateTask(() =>
        {
            if (!AmongUsClient.Instance.AmHost || !IsColorCamouflage || generation != DyeGeneration) return;
            ClearDye($"Timeout:G{generation}");
        }, duration, $"EvilDyer Dye Expire G{generation}");
    }

    private static void ClearDye(string reason)
    {
        if (!IsColorCamouflage) return;

        int generation = ++DyeGeneration;
        IsColorCamouflage = false;
        int count = 0;

        foreach (var playerId in DyedPlayerIds)
        {
            var target = Utils.GetPlayerById(playerId);
            if (target == null || target.Data == null || target.Data.Disconnected) continue;
            RestoreOutfit(target);
            count++;
        }

        DyedPlayerIds.Clear();

        Logger.Info($"Stage=DYE_CLEAR | Generation={generation} | Reason={reason} | LivingTargets={count}", nameof(EvilDyer));
        // STABILITY_R1: Costume sync above already sends the necessary outfit
        // RPCs. Only changed nameplates need syncing; avoid force-resending
        // unchanged names to every Vanilla viewer (O(players^2) RPC burst).
        Utils.NotifyRoles();
    }

    private static bool NeedsRedBody(PlayerControl target)
    {
        var outfit = target.Data.DefaultOutfit;
        return outfit.ColorId != 0
            || !string.IsNullOrEmpty(outfit.HatId)
            || !string.IsNullOrEmpty(outfit.SkinId)
            || !string.IsNullOrEmpty(outfit.VisorId)
            || !string.IsNullOrEmpty(outfit.PetId);
    }

    private static void ApplyRedBody(PlayerControl target, string reason)
    {
        if (!NeedsRedBody(target)) return;

        var red = new NetworkedPlayerInfo.PlayerOutfit().Set(
            target.Data.PlayerName, 0, "", "", "", "");

        WriteOutfit(target, red);
        Logger.Info($"Stage=DYE_APPLY | TargetId={target.PlayerId} | Reason={reason}", nameof(EvilDyer));
    }

    private static void RestoreOutfit(PlayerControl target)
    {
        if (target.IsAlive() && Options.CommsCamouflage.GetBool() && !target.Is(CustomRoles.Rainbow))
        {
            Camouflage.RpcSetSkin(target);
            return;
        }

        byte id = target.PlayerId;

        if (target.IsAlive()
            && Main.CheckShapeshift.TryGetValue(id, out var shapeshifting)
            && shapeshifting
            && !Main.shapeSwitchPlayerIds.Contains(id))
        {
            id = Main.ShapeshiftTarget[id];
        }

        if (!Camouflage.PlayerSkins.TryGetValue(id, out var outfit) || outfit == null)
            outfit = target.Data.DefaultOutfit;

        if (!target.IsAlive())
        {
            var deadOutfit = new NetworkedPlayerInfo.PlayerOutfit().Set(
                outfit.PlayerName,
                outfit.ColorId,
                outfit.HatId,
                outfit.SkinId,
                outfit.VisorId,
                "");

            WriteOutfit(target, deadOutfit);
            return;
        }

        WriteOutfit(target, outfit);
    }

    private static void WriteOutfit(PlayerControl target, NetworkedPlayerInfo.PlayerOutfit outfit)
    {
        var sender = CustomRpcSender.Create(name: $"EvilDyer.WriteOutfit({target.Data.PlayerName})");

        target.SetColor(outfit.ColorId);
        sender.AutoStartRpc(target.NetId, (byte)RpcCalls.SetColor)
            .Write(target.Data.NetId).Write(outfit.ColorId).EndRpc();

        target.SetHat(outfit.HatId, outfit.ColorId);
        sender.AutoStartRpc(target.NetId, (byte)RpcCalls.SetHatStr)
            .Write(outfit.HatId).Write(target.GetNextRpcSequenceId(RpcCalls.SetHatStr)).EndRpc();

        target.SetSkin(outfit.SkinId, outfit.ColorId);
        sender.AutoStartRpc(target.NetId, (byte)RpcCalls.SetSkinStr)
            .Write(outfit.SkinId).Write(target.GetNextRpcSequenceId(RpcCalls.SetSkinStr)).EndRpc();

        target.SetVisor(outfit.VisorId, outfit.ColorId);
        sender.AutoStartRpc(target.NetId, (byte)RpcCalls.SetVisorStr)
            .Write(outfit.VisorId).Write(target.GetNextRpcSequenceId(RpcCalls.SetVisorStr)).EndRpc();

        target.SetPet(outfit.PetId);
        sender.AutoStartRpc(target.NetId, (byte)RpcCalls.SetPetStr)
            .Write(outfit.PetId).Write(target.GetNextRpcSequenceId(RpcCalls.SetPetStr)).EndRpc();

        sender.SendMessage();
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetColor))]
    private static class RpcSetColorGuard
    {
        private static bool Prefix(PlayerControl __instance)
        {
            if (!AmongUsClient.Instance.AmHost) return true;
            if (!IsColorCamouflage || !GameStates.IsInTask) return true;
            if (__instance == null || !__instance.IsAlive()) return true;

            return false;
        }
    }
}