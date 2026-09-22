using HarmonyLib;
using TownOfHostForE.Roles.Impostor;

namespace TownOfHostForE;

/// <summary>
/// EvilHacker + RealtimeAdmin の入力だけを consume する。
/// false を返す対象以外は native CheckVanish/CmdCheckVanish を完全に維持する。
/// </summary>
[HarmonyPatch(typeof(PlayerControl))]
internal static class EvilHackerVanishPatch
{
    [HarmonyPatch(nameof(PlayerControl.CmdCheckVanish))]
    [HarmonyPrefix]
    private static bool CmdCheckVanishPrefix(PlayerControl __instance, float maxDuration)
    {
        _ = maxDuration;
        if (!EvilHacker.TryGetVanishInputContext(
                __instance,
                out var simpleAdminEnabled,
                out var ownerMod))
        {
            return true;
        }

        var isHost = AmongUsClient.Instance.AmHost;
        EvilHacker.LogVanishInput(
            __instance,
            isHost ? "HOST" : "LOCAL_MOD",
            ownerMod,
            simpleAdminEnabled,
            nameof(PlayerControl.CmdCheckVanish),
            isHost ? "CONSUME_LOCAL_HOST" : "FORWARD_NATIVE_HOST_REQUEST");

        // Remote MOD / Vanillaはnative CheckVanish requestをHostへ送る。
        if (!isHost)
        {
            return true;
        }

        // EVILHACKER_SIMPLEADMIN_MOD_OWNER_TOGGLE_INPUT_AB_V1:
        // Host MODはnative Phantom IsInvisibleが変化しなくても、
        // 同じ能力入力でOPEN/CLOSEをtoggleできる。
        var consumed =
            ownerMod
                ? EvilHacker.TryConsumeVanishToggle(__instance)
                : EvilHacker.TryConsumeVanish(
                    __instance,
                    requestOpen: true);

        EvilHacker.LogNativeVanishBlock(
            __instance,
            nameof(PlayerControl.CmdCheckVanish),
            consumed);

        return !consumed;
    }

    // v18 authoritative path:
    // CmdCheckVanish -> RpcCalls.CheckVanish -> PlayerControl.HandleRpc
    // -> current native RoleBehaviour.HandleRoleRpc -> PlayerControl.CheckVanish
    [HarmonyPatch(nameof(PlayerControl.HandleRpc))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool HandleRpcPrefix(
        PlayerControl __instance,
        [HarmonyArgument(0)] byte callId)
    {
        var isCheckVanish =
            callId == (byte)RpcCalls.CheckVanish;

        var isCheckAppear =
            callId == (byte)RpcCalls.CheckAppear;

        if (!AmongUsClient.Instance.AmHost ||
            (!isCheckVanish && !isCheckAppear) ||
            !EvilHacker.TryGetVanishInputContext(
                __instance,
                out var simpleAdminEnabled,
                out var ownerMod))
        {
            return true;
        }

        var nativeInput =
            isCheckVanish
                ? "NATIVE_CHECK_VANISH"
                : "NATIVE_CHECK_APPEAR";

        var blockStage =
            isCheckVanish
                ? "HandleRpc.CheckVanish"
                : "HandleRpc.CheckAppear";

        EvilHacker.LogVanishInput(
            __instance,
            "HOST",
            ownerMod,
            simpleAdminEnabled,
            nameof(PlayerControl.HandleRpc),
            "AUTHORITATIVE_REQUEST_RECEIVED");

        EvilHacker.LogVanishHostRequest(
            __instance,
            nativeInput,
            ownerMod && isCheckVanish
                ? "MOD_OWNER_TOGGLE_BEFORE_ROLE_DISPATCH"
                : "EXPLICIT_STATE_BEFORE_ROLE_DISPATCH");
        // EVILHACKER_VANILLA_EXPLICIT_NATIVE_INPUT_AB_V3:
        // MOD owner: CheckVanishはSimpleAdmin現在状態をtoggle。
        // Vanilla owner: v6180.316同様 CheckVanish=OPEN / CheckAppear=CLOSE。
        // native lifecycleを本人へ返すため、Vanillaはexplicit stateを維持する。
        var consumed =
            ownerMod && isCheckVanish
                ? EvilHacker.TryConsumeVanishToggle(__instance)
                : EvilHacker.TryConsumeVanish(
                    __instance,
                    requestOpen: isCheckVanish);

        EvilHacker.LogNativeVanishBlock(
            __instance,
            blockStage,
            consumed);

        return !consumed;
    }

    [HarmonyPatch(nameof(PlayerControl.CheckVanish))]
    [HarmonyPrefix]
    private static bool CheckVanishPrefix(PlayerControl __instance)
    {
        if (!AmongUsClient.Instance.AmHost ||
            !EvilHacker.TryGetVanishInputContext(
                __instance,
                out var simpleAdminEnabled,
                out var ownerMod))
        {
            return true;
        }

        EvilHacker.LogVanishInput(
            __instance,
            "HOST",
            ownerMod,
            simpleAdminEnabled,
            nameof(PlayerControl.CheckVanish),
            "FALLBACK_DIRECT_CALL");

        EvilHacker.LogVanishHostRequest(
            __instance,
            "DIRECT_CHECK_VANISH",
            ownerMod
                ? "MOD_OWNER_TOGGLE_BEFORE_NATIVE_VALIDATION"
                : "EXPLICIT_OPEN_BEFORE_NATIVE_VALIDATION");

        var consumed =
            ownerMod
                ? EvilHacker.TryConsumeVanishToggle(__instance)
                : EvilHacker.TryConsumeVanish(
                    __instance,
                    requestOpen: true);

        EvilHacker.LogNativeVanishBlock(
            __instance,
            nameof(PlayerControl.CheckVanish),
            consumed);

        return !consumed;
    }
}
