using System;
using System.Collections.Generic;
using Hazel;
using InnerNet;
using TownOfHostForE.Roles.Crewmate;

namespace TownOfHostForE.Roles.Core.Class;

// AEGIS_REFLECTION_V1: every caller supplies the actual source, never a stale RealKiller.
internal static class AegisReflectionGuard
{
    private static readonly Dictionary<byte, PlayerControl> ReflectionDeaths = new();
    private static readonly Dictionary<byte, PlayerControl> AttackSources = new();

    // Scoped source for visual self-murders and proxies. Never infer it from old RealKiller state.
    internal static PlayerControl GetAttackSource(PlayerControl appearance, PlayerControl target)
    {
        if (target == null) return appearance;
        if (AttackSources.TryGetValue(target.PlayerId, out var source)) return source;
        if (CustomRoleManager.CheckMurderInfos.TryGetValue(appearance.PlayerId, out var info) &&
            info.AppearanceTarget == target && info.AttemptTarget == target) return info.AttemptKiller;
        return appearance;
    }

    internal static void RunAttack(PlayerControl source, PlayerControl target, Action murder)
    {
        var hadPrevious = AttackSources.TryGetValue(target.PlayerId, out var previous);
        AttackSources[target.PlayerId] = source;
        try { murder(); }
        finally
        {
            if (hadPrevious) AttackSources[target.PlayerId] = previous!;
            else AttackSources.Remove(target.PlayerId);
        }
    }

    internal static void PreserveAttribution(PlayerControl player)
    {
        if (!ReflectionDeaths.TryGetValue(player.PlayerId, out var aegis)) return;
        PlayerState.GetByPlayerId(player.PlayerId).DeathReason = CustomDeathReason.Reflection;
        if (PlayerState.GetByPlayerId(player.PlayerId).RealKiller.Item2 != aegis.PlayerId)
            player.SetRealKiller(aegis);
    }

    internal static bool IsReflectionDeath(PlayerControl player) =>
        player != null && ReflectionDeaths.ContainsKey(player.PlayerId);

    internal static bool IsBarrierActive(PlayerControl target) =>
        target != null && target.Data != null && !target.Data.Disconnected &&
        target.IsAlive() && target.GetRoleClass() is Aegis aegis && aegis.IsBarrierActive;

    internal static bool CanReflect(PlayerControl attacker, PlayerControl target, string attackKind) =>
        AmongUsClient.Instance.AmHost && attacker != null && attacker.Data != null &&
        target != null && attacker.PlayerId != target.PlayerId &&
        attackKind != "Reflection" && !IsReflectionDeath(target) && IsBarrierActive(target);

    internal static bool TryReflect(PlayerControl attacker, PlayerControl target, string attackKind)
    {
        if (!CanReflect(attacker, target, attackKind)) return false;
        foreach (var key in new List<byte>(CustomRoleManager.CheckMurderInfos.Keys))
        {
            if (CustomRoleManager.CheckMurderInfos[key].AttemptTarget == target)
                CustomRoleManager.CheckMurderInfos.Remove(key);
        }

        Logger.Info($"Stage=REFLECT | AegisId={target.PlayerId} | AttackerId={attacker.PlayerId} | " +
            $"AttackKind={attackKind} | AttackerRole={attacker.GetCustomRole()} | TargetRole=Aegis", "AEGIS_REFLECTION_V1");

        if (!attacker.IsAlive() || attacker.Data.Disconnected)
        {
            Logger.Info($"Stage=REFLECT_SOURCE_ALREADY_DEAD | AegisId={target.PlayerId} | AttackerId={attacker.PlayerId}", "AEGIS_REFLECTION_V1");
            return true;
        }

        if (!ReflectionDeaths.TryAdd(attacker.PlayerId, target)) return true;
        try
        {
            // Reflection is a forced consequence, outside ordinary murder/role-guard checks.
            CustomRoleManager.CheckMurderInfos.Remove(attacker.PlayerId);
            PlayerState.GetByPlayerId(attacker.PlayerId).DeathReason = CustomDeathReason.Reflection;
            attacker.SetRealKiller(target); // overwrite any earlier attempted-kill attribution
            AntiBlackout.TryRotateViewerNativeSentinelBeforeMurderAbV27(attacker);
            var flags = MurderResultFlags.Succeeded; // no DecisionByHost guardian check
            var writer = AmongUsClient.Instance.StartRpcImmediately(attacker.NetId,
                (byte)RpcCalls.MurderPlayer, SendOption.Reliable);
            writer.WriteNetObject(attacker);
            writer.Write((int)flags);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
            attacker.MurderPlayer(attacker, flags);
            var finalState = PlayerState.GetByPlayerId(attacker.PlayerId);
            Logger.Info($"Stage=POST_REFLECT_ATTRIBUTION | AttackerId={attacker.PlayerId} | AegisId={target.PlayerId} | " +
                $"FinalDeathReason={finalState.DeathReason} | FinalRealKillerId={finalState.RealKiller.Item2}", "AEGIS_REFLECTION_V1");
        }
        finally
        {
            ReflectionDeaths.Remove(attacker.PlayerId);
        }
        return true;
    }
}
