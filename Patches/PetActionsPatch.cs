using HarmonyLib;
using Hazel;
using System.Collections.Generic;
using TownOfHostForE.Attributes;
using TownOfHostForE.OneTimeAbillitys;
using TownOfHostForE.Roles;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Crewmate;

namespace TownOfHostForE.Patches;
// DOG_SHERIFF_PET_IMMEDIATE_CLICK_PERMANENT
// ドッグシェリフ本人のPetButtonだけHoldToUseを無効化し、短押しで通常DoClick経路へ進める。
[HarmonyPatch(typeof(PassiveButton), "Update")]
class DogSheriffPetImmediateClickPatch
{
private static readonly Dictionary<int, bool> OriginalHoldToUse = new();

    public static void Prefix(PassiveButton __instance)
    {
        if (__instance.TargetActionButton is not PetButton) return;

        var instanceId = __instance.GetInstanceID();
        var localPlayer = PlayerControl.LocalPlayer;
        if (localPlayer != null &&
            GameStates.IsInGame &&
            localPlayer.Is(CustomRoles.DogSheriff))
        {
            if (!OriginalHoldToUse.ContainsKey(instanceId))
                OriginalHoldToUse[instanceId] = __instance.HoldToUse;

            if (__instance.HoldToUse)
            {
                __instance.HoldToUse = false;
}
            return;
        }

        if (OriginalHoldToUse.TryGetValue(instanceId, out var originalHoldToUse))
        {
            __instance.HoldToUse = originalHoldToUse;
            OriginalHoldToUse.Remove(instanceId);
}
    }
}

/*
 * HUGE THANKS TO
 * ImaMapleTree / 단풍잎
 * FOR THE CODE
 *
 * THIS IS JUST SMALL FOR NOW BUT MAY EVENTUALLY BE BIG
*/

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.TryPet))]
class LocalPetPatch
{
    public static void Prefix(PlayerControl __instance)
    {
if (!GameStates.IsLobby)
        {
            if (!(AmongUsClient.Instance.AmHost && AmongUsClient.Instance.AmClient)) return;
            //__instance.petting = true;
            ExternalRpcPetPatch.Prefix(__instance.MyPhysics, 51, new MessageReader());
        }
    }

    //public static void Postfix(PlayerControl __instance)
    //{
    //    if (!GameStates.IsLobby)
    //    {
    //        if (!(AmongUsClient.Instance.AmHost && AmongUsClient.Instance.AmClient)) return;
    //        __instance.petting = false;

    //    }
    //}

}

[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.HandleRpc))]
class ExternalRpcPetPatch
{
    private static readonly Dictionary<byte, long> LocalPetDispatchTicks = new();
    private static readonly long DuplicateWindowTicks =
        System.TimeSpan.FromSeconds(1).Ticks;
    public static void Prefix(PlayerPhysics __instance, [HarmonyArgument(0)] byte callId,
        [HarmonyArgument(1)] MessageReader reader)
    {
        var rpcType = callId == 51 ? RpcCalls.Pet : (RpcCalls)callId;
        if (rpcType != RpcCalls.Pet) return;

        PlayerControl playerControl = __instance.myPlayer;
if (!AmongUsClient.Instance.AmHost || GameStates.IsLobby) return;

        var suppressDuplicateAbility = false;
        var nowTicks = System.DateTime.UtcNow.Ticks;

        // callId 51はホスト自身のTryPet Prefixから行う先行処理。
        // 直後に本来のPet RPCが到着した場合だけ、能力処理を重複させない。
        if (callId == 51)
        {
            LocalPetDispatchTicks[playerControl.PlayerId] = nowTicks;
        }
        else if (LocalPetDispatchTicks.TryGetValue(
                     playerControl.PlayerId,
                     out var localDispatchTicks))
        {
            LocalPetDispatchTicks.Remove(playerControl.PlayerId);

            var elapsedTicks = nowTicks - localDispatchTicks;

            suppressDuplicateAbility =
                elapsedTicks >= 0 &&
                elapsedTicks <= DuplicateWindowTicks;
        }

        // NYAOHA_PET_LONGPRESS_PERMANENT
        // 発射準備中に長押しで再送されるPet RPCは、
        // CancelPet・同期・能力本体より前に破棄する。
        if (playerControl.GetRoleClass() is
                TownOfHostForE.Roles.Animals.Nyaoha nyaoha &&
            nyaoha.IsPetAbilityActive)
        {
            return;
        }

        //if (callId == 51 && playerControl.GetCustomRole().PetActivatedAbility() && GameStates.IsInGame)
        if (callId == 51 && GameStates.IsInGame &&
            (playerControl.GetCustomRole().PetActivatedAbility()
             ||
             OneTimeAbilittyController.CheckSettingAbilitys(playerControl.PlayerId,OneTimeAbilittyController.OneTimeAbility.petKill))
            )
                __instance.CancelPet();

        if (callId != 51)
        {
            // PET003_REMOVE_UNUSED_CUSTOMRPCSENDER_PERMANENT:
            // ここではCustomRpcSenderを使用しておらず、Create時のMessageWriterだけが未返却になるため生成しない。
            if (AmongUsClient.Instance.AmHost && playerControl.GetCustomRole().PetActivatedAbility() && GameStates.IsInGame)
                __instance.CancelPet();
            foreach (PlayerControl player in PlayerControl.AllPlayerControls)
            {
                if (!player.TryGetClientId(out var clientId)) continue;
                AmongUsClient.Instance.FinishRpcImmediately(AmongUsClient.Instance.StartRpcImmediately(__instance.NetId, 50, SendOption.None, clientId));
            }
        }

        // 実Pet RPC側ではCancelPetと同期処理を従来どおり実行し、
        // 先行処理済みの場合だけ能力本体の二重実行を止める。
        if (suppressDuplicateAbility) return;

        // RENEGADE_R2G_GLOBAL_PET_ABILITY_LOCKDOWN
        // Petの見た目/同期処理は維持し、能力本体だけを覚醒中は実行しない。
        if (global::TownOfHostForE.Roles.Neutral.Renegade.IsAwakeningActive())
        {
            Logger.Info(
                $"R2G awakened chase: pet active ability blocked player={playerControl.PlayerId}",
                "Renegade");
            return;
        }

        // ROLEBUGTRACE_STAGE1A_PERMANENT: accepted pet dispatch only
        RoleBugTrace.Pet(playerControl, callId == 51 ? "HostLocal" : "Rpc");

        GreatDetective.TargetOnPetsButton(playerControl);

        //ワンタイムがあればそちらを優先
        if (OneTimeAbilittyController.CheckSettingAbilitys(playerControl.PlayerId, OneTimeAbilittyController.OneTimeAbility.petKill))
        {
            OneTimeAbilittyController.ActivationPetAbillitys(playerControl);
            // ROLEBUGTRACE_PET_AFTER_DISPATCH_PERMANENT: one-time ability After snapshot
            RoleBugTrace.PetAfter(playerControl, callId == 51 ? "HostLocal-OneTime" : "Rpc-OneTime");
            return;
        }

        var cRole = playerControl.GetCustomRole();
        if (!cRole.IsNotAssignRoles())
        {
            playerControl.GetRoleClass().OnTouchPet(playerControl);
        }
        // ROLEBUGTRACE_PET_AFTER_DISPATCH_PERMANENT: normal role After snapshot
        RoleBugTrace.PetAfter(playerControl, callId == 51 ? "HostLocal-Role" : "Rpc-Role");
    }
}
public static class PetSettings
{
    public static HashSet<byte> petNotSetPlayerIds = new();
    public static bool AllPetAssign = false;

    public const string FREEPET_STRING = "pet_HamPet";

    [GameModuleInitializer]
    public static void GameInit()
    {

        petNotSetPlayerIds.Clear();

    }


    public static void CheckNotHasPetPlayers()
    {
        foreach (var pc in Main.AllPlayerControls)
        {
            if (pc == null || pc.Data == null || pc.Data.Disconnected)
                continue;

            var defaultPetId = pc.Data.DefaultOutfit.PetId ?? "";
            var currentPetId = pc.CurrentOutfit?.PetId ?? "";

            // RENTAL_PET_EMPTY_SENTINEL_REPAIR_AB_V1:
            // Preserve only real pet ids. Empty values and the official empty sentinel remain rental-eligible.
            var defaultHasRealPet =
                !string.IsNullOrEmpty(defaultPetId) &&
                defaultPetId != "pet_EmptyPet";
            var currentHasRealPet =
                !string.IsNullOrEmpty(currentPetId) &&
                currentPetId != "pet_EmptyPet";

            if (defaultHasRealPet || currentHasRealPet)
            {
                Logger.Info(
                    $"Owned pet preserved: PlayerId={pc.PlayerId}, " +
                    $"DefaultPetId={defaultPetId}, CurrentPetId={currentPetId}",
                    "RentalPetOwnedGuard");
                continue;
            }

            // Default/CurrentともPetId無しの人だけレンタル対象。
            petNotSetPlayerIds.Add(pc.PlayerId);

            Logger.Info(
                $"Rental pet eligible: PlayerId={pc.PlayerId}, " +
                $"DefaultPetId={defaultPetId}, CurrentPetId={currentPetId}",
                "RentalPetOwnedGuard");
        }
    }

    //ペット付けてない人がいたらペットつけるための処理
    public static void SetPetRoleInPet()
    {

        if (AllPetAssign == false)
        {
            return;
        }

        foreach (byte playerId in petNotSetPlayerIds)
        {
            var pc = Utils.GetPlayerById(playerId);
            // PETSETTINGS_STALE_PLAYER_NULL_GUARD_PERMANENT_SET
            // ShipStatus.Start後の切断などで保存済みPlayerIdが取得不能になった場合だけ安全に除外する。
            if (pc == null)
            {
                Logger.Warn($"RentalPet.SetPet skipped: PlayerControl missing, PlayerId={playerId}", "PetSettings");
                continue;
            }

            // ホスト所有でないPlayerControlにも届くSetPetStr形式で送信する。
            // RENTAL_PET_DEFAULT_OUTFIT_PERSIST_AB_V1:
            // v18の会議・迷彩・GameData復元が参照するDefaultOutfitにも
            // rental petを保持し、visual-only状態にしない。
            pc.SetPet(FREEPET_STRING);
            pc.Data.DefaultOutfit.PetId = FREEPET_STRING;
            pc.Data.DefaultOutfit.PetSequenceId += 10;

            var rentalPetSequenceId =
                pc.GetNextRpcSequenceId(RpcCalls.SetPetStr);

            var rentalPetSender = CustomRpcSender.Create(
                $"RentalPet.SetPetStr(PlayerId={playerId})",
                Hazel.SendOption.Reliable
            );

            rentalPetSender
                .AutoStartRpc(
                    pc.NetId,
                    (byte)RpcCalls.SetPetStr
                )
                .Write(FREEPET_STRING)
                .Write(rentalPetSequenceId)
                .EndRpc();

            rentalPetSender.SendMessage();


        }

    }

    // DEAD_PET_RELIABLE_SET_PET_STR_V1:
    // 死亡・追放後に残るPet visualだけを除去する。
    // DefaultOutfit.PetId自体は保持し、生前の装備情報は破壊しない。
    public static void RemoveDeadPetVisualReliable(
        PlayerControl pc,
        string source)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (pc == null || pc.Data == null) return;
        if (pc.Data.Disconnected) return;

        const string emptyPetId = "";

        try
        {
            pc.SetPet(emptyPetId);
        }
        catch (System.Exception ex)
        {
            Logger.Warn(
                $"DeadPetCleanup local SetPet failed: " +
                $"PlayerId={pc.PlayerId}, Source={source}, " +
                $"Exception={ex.GetType().Name}, Message={ex.Message}",
                "DeadPetCleanup");
        }

        try
        {
            pc.Data.DefaultOutfit.PetSequenceId += 10;

            var petSequenceId =
                pc.GetNextRpcSequenceId(RpcCalls.SetPetStr);

            var sender =
                CustomRpcSender.Create(
                    $"DeadPetCleanup.SetPetStr(" +
                    $"PlayerId={pc.PlayerId}," +
                    $"Source={source})",
                    Hazel.SendOption.Reliable);

            sender
                .AutoStartRpc(
                    pc.NetId,
                    (byte)RpcCalls.SetPetStr)
                .Write(emptyPetId)
                .Write(petSequenceId)
                .EndRpc();

            sender.SendMessage();

            Logger.Info(
                $"PlayerId={pc.PlayerId}, " +
                $"Source={source}, " +
                $"Sequence={petSequenceId}, " +
                $"DefaultPetPreserved={pc.Data.DefaultOutfit.PetId}, " +
                $"Reliable=True, NativeSetPetStr=True",
                "DeadPetCleanup");
        }
        catch (System.Exception ex)
        {
            Logger.Warn(
                $"DeadPetCleanup network SetPetStr failed: " +
                $"PlayerId={pc.PlayerId}, Source={source}, " +
                $"Exception={ex.GetType().Name}, Message={ex.Message}",
                "DeadPetCleanup");
        }
    }

    public static void RemoveAllDeadPetVisualsReliable(
        string source)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        foreach (var pc in Main.AllPlayerControls)
        {
            if (pc == null || pc.Data == null) continue;
            if (pc.Data.Disconnected) continue;

            if (pc.IsAlive() && !pc.Data.IsDead)
                continue;

            RemoveDeadPetVisualReliable(
                pc,
                source);
        }
    }
    // HOST_LOCAL_REMOTE_PET_REANCHOR_AB_V4:
    // Host display only. No Pet RPC, SetPetStr, outfit or ability state
    // is changed. Only clearly detached live Remote pets are moved back
    // to their PlayerControl after Airship meeting respawn.
    public static void ReanchorHostLocalRemotePetVisual(
        PlayerControl pc,
        string source)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (pc == null || pc.AmOwner || pc.Data == null) return;
        if (pc.Data.Disconnected || pc.Data.IsDead || !pc.IsAlive()) return;

        try
        {
            var cosmetics = pc.cosmetics;
            var pet = cosmetics == null ? null : cosmetics.currentPet;

            if (pet == null) return;

            var playerPosition = pc.transform.position;
            var petPosition = pet.transform.position;
            var dx = petPosition.x - playerPosition.x;
            var dy = petPosition.y - playerPosition.y;
            var distanceSquared = dx * dx + dy * dy;

            if (distanceSquared <= 2.25f)
                return;

            pet.transform.position =
                new UnityEngine.Vector3(
                    playerPosition.x,
                    playerPosition.y,
                    petPosition.z);

            Logger.Info(
                $"PlayerId={pc.PlayerId} | Source={source} | " +
                $"DistanceSquared={distanceSquared} | " +
                $"Player=({playerPosition.x},{playerPosition.y}) | " +
                $"PetBefore=({petPosition.x},{petPosition.y})",
                "HostLocalPetReanchor");
        }
        catch (System.Exception ex)
        {
            Logger.Warn(
                $"Host-local Pet reanchor failed: PlayerId={pc.PlayerId}, " +
                $"Source={source}, Exception={ex.GetType().Name}, Message={ex.Message}",
                "HostLocalPetReanchor");
        }
    }

    public static void RemovePetSet()
    {
        //登録が0じゃないなら
        if(petNotSetPlayerIds.Count != 0)
        {
            foreach (byte playerId in petNotSetPlayerIds)
            {
                var pc = Utils.GetPlayerById(playerId);

                // PETSETTINGS_STALE_PLAYER_NULL_GUARD_PERMANENT_REMOVE
                // SetPetRoleInPetと同じ保存済みIDを使うため、切断済みPlayerControlだけを安全に除外する。
                if (pc == null)
                {
                    Logger.Warn($"RentalPet.Remove skipped: PlayerControl missing, PlayerId={playerId}", "PetSettings");
                    continue;
                }

                // RENTAL_PET_DEFAULT_OUTFIT_PERSIST_AB_V1:
                // Game開始時に保持したauthoritative rental petは、
                // 生死に関係なく次ロビーへ持ち越さないよう先に解除する。
                pc.Data.DefaultOutfit.PetId = "";

                // PET001_REMOVE_DEAD_CONTINUE_PERMANENT:
                // 死亡者だけをvisual RPC対象外にし、後続の生存プレイヤー処理は継続する。
                if (!pc.IsAlive()) continue;

                pc.RpcSetPet("");
            }
        }
        AllPetAssign = false;
    }

    //private static bool PetRoleCheck()
    //{
    //    //ちょっと地道だけどいい方法見つけたらそれにする
    //    return CustomRoles.Badger.IsPresent() ||
    //           CustomRoles.Gizoku.IsPresent() ||
    //           CustomRoles.SpiderMad.IsPresent() ||
    //           CustomRoles.Nyaoha.IsPresent() ||
    //           CustomRoles.DogSheriff.IsPresent();
    //}
    public static void PetRoleCheck(CustomRoles nowRole)
    {
        switch (nowRole)
        {
            case CustomRoles.Badger:
            case CustomRoles.Gizoku:
            case CustomRoles.SpiderMad:
            case CustomRoles.Nyaoha:
            case CustomRoles.Dolphin:
            case CustomRoles.DogSheriff:
                AllPetAssign = true;
                break;
        }

    }
}