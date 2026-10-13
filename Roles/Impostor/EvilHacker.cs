using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AmongUs.GameOptions;
using Hazel;
using TownOfHostForE.Modules;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Interfaces;
using UnityEngine;

namespace TownOfHostForE.Roles.Impostor;

public sealed class EvilHacker : RoleBase, IImpostor, IKillFlashSeeable
{
    public static readonly SimpleRoleInfo RoleInfo =
        SimpleRoleInfo.Create(
            typeof(EvilHacker),
            player => new EvilHacker(player),
            CustomRoles.EvilHacker,
            () => RoleTypes.Impostor,
            CustomRoleTypes.Impostor,
            3100,
            SetupOptionItems,
            "イビルハッカー"
        );
    public EvilHacker(PlayerControl player)
    : base(
        RoleInfo,
        player,
        hasAbility: true
    )
    {
        canSeeDeadMark = OptionCanSeeDeadMark.GetBool();
        canSeeImpostorMark = OptionCanSeeImpostorMark.GetBool();
        canSeeKillFlash = OptionCanSeeKillFlash.GetBool();
        canSeeMurderRoom = OptionCanSeeMurderRoom.GetBool();
        useRealtimeAdmin = SimpleAdminEnabledAtGameStart;
        simpleAdminCooldown = OptionSimpleAdminCooldown.GetFloat();
        showImpostorTeammates = OptionShowImpostorTeammates.GetBool();
        showLastAdminAtMeeting = OptionShowLastAdminAtMeeting.GetBool();

        CustomRoleManager.OnMurderPlayerOthers.Add(HandleMurderRoomNotify);
        instances.Add(this);
    }
    public override void OnDestroy()
    {
        DisableRealtimeAdmin(notifyRoles: true, clearSnapshot: true);
        activeNotifies.Clear();
        instances.Remove(this);
    }

    private static OptionItem OptionCanSeeDeadMark;
    private static OptionItem OptionCanSeeImpostorMark;
    private static OptionItem OptionCanSeeKillFlash;
    private static OptionItem OptionCanSeeMurderRoom;
    private static OptionItem OptionUseRealtimeAdmin = null!;

    private static bool simpleAdminEnabledAtGameStart;
    private static bool hasSimpleAdminGameStartSnapshot;

    // EVILHACKER_SIMPLEADMIN_NATIVE_ROLE_CONTRACT_AB_V1:
    // initial role bundleから会議後rearmまで同じgame-local option snapshotを使う。
    internal static bool SimpleAdminEnabledAtGameStart =>
        hasSimpleAdminGameStartSnapshot
            ? simpleAdminEnabledAtGameStart
            : OptionUseRealtimeAdmin?.GetBool() == true;

    internal static void CaptureSimpleAdminEnabledAtGameStart()
    {
        simpleAdminEnabledAtGameStart =
            OptionUseRealtimeAdmin?.GetBool() == true;
        hasSimpleAdminGameStartSnapshot = true;

        Logger.Info(
            $"EVILHACKER_SIMPLEADMIN_GAMESTART_SNAPSHOT SimpleAdminEnabled={simpleAdminEnabledAtGameStart}",
            nameof(EvilHacker));
    }
    private static OptionItem OptionSimpleAdminCooldown = null!;
    private static OptionItem OptionShowImpostorTeammates = null!;
    private static OptionItem OptionShowLastAdminAtMeeting = null!;
    private enum OptionName
    {
        EvilHackerCanSeeDeadMark,
        EvilHackerCanSeeImpostorMark,
        EvilHackerCanSeeKillFlash,
        EvilHackerCanSeeMurderRoom,
        EvilHackerUseRealtimeAdmin,
        EvilHackerSimpleAdminCooldown,
        EvilHackerShowImpostorTeammates,
        EvilHackerShowLastAdminAtMeeting,
    }
    private static bool canSeeDeadMark;
    private static bool canSeeImpostorMark;
    private static bool canSeeKillFlash;
    private static bool canSeeMurderRoom;

    private readonly bool useRealtimeAdmin;
    private readonly float simpleAdminCooldown;
    private readonly bool showImpostorTeammates;
    private readonly bool showLastAdminAtMeeting;
    private bool isRealtimeAdminMode;
    private float nextRealtimeAdminRefresh;
    private string previousSnapshotKey = string.Empty;
    private string lastSentLiveText = string.Empty;
    private EvilHackerTextMinimap.AdminSnapshot? lastAdminSnapshot;

    // SystemTypes is 0..58 in v18. 0xff is reserved here as a role-local
    // CustomRoleSync discriminator and cannot collide with murder-room messages.
    private const byte RealtimeAdminStateSync = byte.MaxValue;

    private static HashSet<EvilHacker> instances = new(1);

    private HashSet<MurderNotify> activeNotifies = new(2);

    private static void SetupOptionItems()
    {
        OptionUseRealtimeAdmin = BooleanOptionItem.Create(RoleInfo, 14, OptionName.EvilHackerUseRealtimeAdmin, false, false);
        OptionSimpleAdminCooldown = FloatOptionItem.Create(RoleInfo, 17, OptionName.EvilHackerSimpleAdminCooldown, new(0f, 60f, 1f), 0f, false, OptionUseRealtimeAdmin).SetValueFormat(OptionFormat.Seconds);
        OptionShowImpostorTeammates = BooleanOptionItem.Create(RoleInfo, 15, OptionName.EvilHackerShowImpostorTeammates, true, false, OptionUseRealtimeAdmin);
        OptionShowLastAdminAtMeeting = BooleanOptionItem.Create(RoleInfo, 16, OptionName.EvilHackerShowLastAdminAtMeeting, true, false, OptionUseRealtimeAdmin);
        OptionCanSeeDeadMark = BooleanOptionItem.Create(RoleInfo, 10, OptionName.EvilHackerCanSeeDeadMark, true, false);
        OptionCanSeeImpostorMark = BooleanOptionItem.Create(RoleInfo, 11, OptionName.EvilHackerCanSeeImpostorMark, true, false);
        OptionCanSeeKillFlash = BooleanOptionItem.Create(RoleInfo, 12, OptionName.EvilHackerCanSeeKillFlash, true, false);
        OptionCanSeeMurderRoom = BooleanOptionItem.Create(RoleInfo, 13, OptionName.EvilHackerCanSeeMurderRoom, true, false, OptionCanSeeKillFlash);
    }
    /// <summary>相方がキルした部屋を通知する設定がオンなら各プレイヤーに通知を行う</summary>
    private static void HandleMurderRoomNotify(MurderInfo info)
    {
        if (canSeeMurderRoom)
        {
            foreach (var evilHacker in instances)
            {
                evilHacker.OnMurderPlayer(info);
            }
        }
    }

    public override bool OnReportDeadBody(PlayerControl reporter, NetworkedPlayerInfo target)
    {
        if (!Player.IsAlive())
        {
            return true;
        }

        if (useRealtimeAdmin)
        {
            var wasRealtimeAdminMode = isRealtimeAdminMode;
            var heldSnapshot = lastAdminSnapshot;
            NormalizeOwnerNativePhantomBeforeMeeting("REPORT");
            DisableRealtimeAdmin(notifyRoles: true, clearSnapshot: true);

            if (wasRealtimeAdminMode && showLastAdminAtMeeting && heldSnapshot != null &&
                !string.IsNullOrWhiteSpace(heldSnapshot.ChatText))
            {
                ScheduleAdminMessage(heldSnapshot.ChatText);
            }
            return true;
        }

        SendLegacyAdminMessage();
        return true;
    }

    /// <summary>
    /// Realtime Admin が無効なときは、従来の会議開始時 Admin 能力をそのまま維持する。
    /// </summary>
    private void SendLegacyAdminMessage()
    {
        var admins = AdminProvider.CalculateAdmin();
        var builder = new StringBuilder(512);

        // 送信するメッセージを生成
        foreach (var admin in admins)
        {
            var entry = admin.Value;
            if (entry.TotalPlayers <= 0)
            {
                continue;
            }
            // インポスターがいるなら星マークを付ける
            if (canSeeImpostorMark && entry.NumImpostors > 0)
            {
                builder.Append(ImpostorMark);
            }
            // 部屋名と合計プレイヤー数を表記
            builder.Append(DestroyableSingleton<TranslationController>.Instance.GetString(entry.Room));
            builder.Append(": ");
            builder.Append(entry.TotalPlayers);
            // 死体があったら死体の数を書く
            if (canSeeDeadMark && entry.NumDeadBodies > 0)
            {
                builder.Append('(').Append(Translator.GetString("Deadbody"));
                builder.Append('×').Append(entry.NumDeadBodies).Append(')');
            }
            builder.Append('\n');
        }

        // 送信
        ScheduleAdminMessage(builder.ToString());
    }

    private void ScheduleAdminMessage(string message)
    {
        var title = Utils.ColorString(Color.green, Translator.GetString("LastAdminInfo"));

        _ = new LateTask(() =>
        {
            if (GameStates.IsInGame)
            {
                Utils.SendMessage(message, Player.PlayerId, title, false);
            }
        }, 4f, "EvilHacker Admin Message");
    }

    // EVILHACKER_INITIAL_OWNER_NATIVE_PHANTOM_AB_V5
    // 初期SetRoleはここでは送らない。Releaseのinitial bundleを唯一の初期writerとする。
    public override void OnSpawn(bool initialState = false)
    {
        _ = initialState;

        if (!useRealtimeAdmin ||
            Player == null ||
            Player.Data == null ||
            !Player.AmOwner)
        {
            return;
        }

        var nativeRole = Player.Data.Role;
        Logger.Info(
            $"EVILHACKER_INITIAL_OWNER_NATIVE_PHANTOM_AB_V5 POSTCONDITION PlayerId={Player.PlayerId} NativeRole={nativeRole?.Role} RuntimeType={nativeRole?.GetType().Name ?? "<null>"} RoleBaseHasAbility={HasAbility}",
            nameof(EvilHacker));
    }

    public override void ApplyGameOptions(IGameOptions opt)
    {
        if (!useRealtimeAdmin)
        {
            return;
        }

        if (opt.TryCast<NormalGameOptionsV12>(out var normalOptions)
            && normalOptions.roleOptions.TryGetRoleOptions<PhantomRoleOptionsV11>(
                RoleTypes.Phantom,
                out var phantomOptions))
        {
            // NATIVE_PHANTOM_ZERO_COOLDOWN_ICON_SAFE_V2:
            // 設定表示0秒は維持し、公式Phantom carrierへ渡す値だけ1秒へ逃がす。
            // v18実機では0.5秒でアイコン非表示、明示1秒で表示を確認したため1秒を下限とする。
            var nativePhantomCooldown = simpleAdminCooldown <= 0f ? 1f : simpleAdminCooldown;
            phantomOptions.PhantomCooldown = nativePhantomCooldown;

            // EVILHACKER_PHANTOM_DURATION_STANDARD_MAX_AB_V1
            // Phantomは全ownerの能力ボタンcarrierとして維持する。Remote VanillaへDirect lifecycle RPCは返さない。
            // 現行30秒前後の自動Appearを減らすため、公式設定範囲内の最大90秒を使用する。
            phantomOptions.PhantomDuration = 90f;
        }
    }

    private void ArmSelfOnlyPhantomRoleABV3A()
    {
        var clientId = Player.GetClientId();
        if (clientId < 0)
        {
            return;
        }

        if (Player.AmOwner)
        {
            if (Player.Data?.Role?.Role != RoleTypes.Phantom)
            {
                Player.StartCoroutine(Player.CoSetRole(RoleTypes.Phantom, false));
            }

            Logger.Info(
                $"EVILHACKER_SELF_ONLY_PHANTOM_AB_V3A Scope=HOST_LOCAL PlayerId={Player.PlayerId}",
                nameof(EvilHacker));
            return;
        }

        var sender = CustomRpcSender.Create(
            "EvilHacker Self-Only Phantom AB v3A",
            SendOption.Reliable);

        sender.StartMessage(clientId);
        sender.StartRpc(Player.NetId, RpcCalls.SetRole)
            .Write((ushort)RoleTypes.Phantom)
            .Write(true)
            .EndRpc();
        sender.EndMessage();
        sender.SendMessage();

        Logger.Info(
            $"EVILHACKER_SELF_ONLY_PHANTOM_AB_V3A Scope=TARGET_SELF_ONLY PlayerId={Player.PlayerId} ClientId={clientId}",
            nameof(EvilHacker));
    }

    // EVILHACKER_FINAL_CANONICAL_REARM_AB_V3C
    // Final owner-only Phantom write after all canonical post-meeting role writers.
    internal static void RearmSelfOnlyPhantomAfterFinalCanonicalABV3C()
    {
        if (!AmongUsClient.Instance.AmHost)
        {
            return;
        }

        foreach (var player in Main.AllPlayerControls)
        {
            if (
                player == null ||
                player.Data == null ||
                player.Data.Disconnected ||
                !player.IsAlive() ||
                player.GetRoleClass() is not EvilHacker evilHacker
            )
            {
                continue;
            }

            var beforeNativeRole = evilHacker.GetOwnerNativeRoleBeforeFinalRearm();
            if (!evilHacker.useRealtimeAdmin)
            {
                evilHacker.LogFinalRearm(beforeNativeRole, "SKIP_OFF");
                continue;
            }

            if (beforeNativeRole == RoleTypes.Phantom)
            {
                evilHacker.LogFinalRearm(beforeNativeRole, "SKIP_ALREADY_PHANTOM");
                continue;
            }

            evilHacker.ArmSelfOnlyPhantomRoleABV3A();
            evilHacker.LogFinalRearm(beforeNativeRole, "SEND_PHANTOM");
        }
    }

    private RoleTypes GetOwnerNativeRoleBeforeFinalRearm()
    {
        if (Player.AmOwner)
        {
            return Player.Data?.Role?.Role ?? RoleTypes.Impostor;
        }

        return SelectRolesPatch.TryGetExileDisplayRoleViewPermanent(
            Player.PlayerId,
            Player.PlayerId,
            out var ownerCanonicalRole)
                ? ownerCanonicalRole
                : RoleTypes.Impostor;
    }

    private void LogFinalRearm(RoleTypes beforeNativeRole, string action)
    {
        Logger.Info(
            $"EVILHACKER_FINAL_REARM PlayerId={Player.PlayerId} " +
            $"ClientId={Player.GetClientId()} " +
            $"SimpleAdminEnabled={useRealtimeAdmin} " +
            $"BeforeNativeRole={beforeNativeRole} Action={action}",
            nameof(EvilHacker));
    }

    public override void OnStartMeeting()
    {
        NormalizeOwnerNativePhantomBeforeMeeting("MEETING_START");
        DisableRealtimeAdmin(notifyRoles: false, clearSnapshot: true);
    }
    private void OnMurderPlayer(MurderInfo info)
    {
        // 生きてる間に相方のキルでキルフラが鳴った場合に通知を出す
        if (!Player.IsAlive() || !CheckKillFlash(info) || info.AttemptKiller == Player)
        {
            return;
        }
        RpcCreateMurderNotify(info.AttemptTarget.GetPlainShipRoom()?.RoomId ?? SystemTypes.Hallway);
    }
    private void RpcCreateMurderNotify(SystemTypes room)
    {
        CreateMurderNotify(room);
        if (AmongUsClient.Instance.AmHost)
        {
            using var sender = CreateSender();
            sender.Writer.Write((byte)room);
        }
    }
    public override void ReceiveRPC(MessageReader reader)
    {
        var messageType = reader.ReadByte();
        if (messageType == RealtimeAdminStateSync)
        {
            ApplyRealtimeAdminStateFromHost(reader.ReadBoolean());
            return;
        }

        CreateMurderNotify((SystemTypes)messageType);
    }
    /// <summary>
    /// 名前の下にキル発生通知を出す
    /// </summary>
    /// <param name="room">キルが起きた部屋</param>
    private void CreateMurderNotify(SystemTypes room)
    {
        activeNotifies.Add(new()
        {
            CreatedAt = DateTime.Now,
            Room = room,
        });
        if (AmongUsClient.Instance.AmHost)
        {
            Utils.NotifyRoles(SpecifySeer: Player);
        }
    }
    public override void OnFixedUpdate(PlayerControl player)
    {
        // 古い通知の削除処理 Mod入りは自分でやる
        if (!AmongUsClient.Instance.AmHost && Player != PlayerControl.LocalPlayer)
        {
            return;
        }

        // EVILHACKER_REMOTE_MOD_REALTIME_REFRESH_AB_V1
        if (isRealtimeAdminMode &&
            (AmongUsClient.Instance.AmHost || Player == PlayerControl.LocalPlayer))
        {
            if (Player.Data == null || Player.Data.Disconnected || !Player.IsAlive() || !GameStates.IsInTask)
            {
                DisableRealtimeAdmin(notifyRoles: Player.Data != null && !Player.Data.Disconnected, clearSnapshot: true);
            }
            else if (Time.time >= nextRealtimeAdminRefresh)
            {
                RefreshRealtimeAdmin();
            }
        }

        if (activeNotifies.Count <= 0)
        {
            return;
        }
        // NotifyRolesを実行するかどうかのフラグ
        var doNotifyRoles = false;
        // 古い通知があれば削除
        foreach (var notify in activeNotifies)
        {
            if (DateTime.Now - notify.CreatedAt > NotifyDuration)
            {
                activeNotifies.Remove(notify);
                doNotifyRoles = true;
            }
        }
        if (doNotifyRoles && AmongUsClient.Instance.AmHost)
        {
            Utils.NotifyRoles(SpecifySeer: Player);
        }
    }
    public override string GetSuffix(PlayerControl seer, PlayerControl seen = null, bool isForMeeting = false)
    {
        seen ??= seer;
        if (isForMeeting || seer != Player || seen != Player)
        {
            return base.GetSuffix(seer, seen, isForMeeting);
        }

        // EVILHACKER_SIMPLEADMIN_FINAL_UI_AB_V1:
        // OPEN中は通常suffixを混ぜず、Admin盤面だけをself表示へ渡す。
        if (isRealtimeAdminMode && lastAdminSnapshot != null)
        {
            return lastAdminSnapshot.LiveText;
        }

        var suffix = new StringBuilder(768);
        suffix.Append(base.GetSuffix(seer, seen, isForMeeting));

        if (canSeeMurderRoom && activeNotifies.Count > 0)
        {
            if (suffix.Length > 0)
            {
                suffix.Append("\r\n");
            }
            var roomNames = activeNotifies.Select(notify => DestroyableSingleton<TranslationController>.Instance.GetString(notify.Room));
            suffix.Append(Utils.ColorString(Color.green, $"{Translator.GetString("MurderNotify")}: {string.Join(", ", roomNames)}"));
        }
        return suffix.ToString();
    }

    // EVILHACKER_SIMPLEADMIN_MOD_OWNER_BUTTON_LABEL_AB_V1:
    // MOD ownerはnative Phantom IsInvisibleではなくSimpleAdminのHost-authority状態を表示する。
    // CLOSED="消える" / OPEN="出現する"。
    public override string GetAbilityButtonText()
    {
        if (
            useRealtimeAdmin &&
            Player != null &&
            Player.AmOwner &&
            Player.IsModClient())
        {
            var label =
                isRealtimeAdminMode
                    ? StringNames.PhantomAbilityUndo
                    : StringNames.PhantomAbility;

            return
                DestroyableSingleton<TranslationController>
                    .Instance
                    .GetString(label);
        }

        return base.GetAbilityButtonText();
    }

    // Vanilla self-name合成の最終段からだけ使用する。
    // SimpleAdmin CLOSE時はfalseとなり通常表示へ自動復帰する。
    internal bool TryGetRealtimeAdminSelfDisplay(out string displayText)
    {
        if (isRealtimeAdminMode && lastAdminSnapshot != null)
        {
            displayText = lastAdminSnapshot.LiveText;
            return true;
        }

        displayText = string.Empty;
        return false;
    }

    /// <summary>
    /// EvilHacker の Phantom 入力だけをホストで消費する。通常 Phantom 系は false で元処理へ返す。
    /// </summary>
    internal static bool TryGetVanishInputContext(
        PlayerControl player,
        out bool simpleAdminEnabled,
        out bool ownerMod)
    {
        simpleAdminEnabled = false;
        ownerMod = false;
        if (player == null || player.GetRoleClass() is not EvilHacker evilHacker)
        {
            return false;
        }

        simpleAdminEnabled = evilHacker.useRealtimeAdmin;
        ownerMod = player.IsModClient();
        return true;
    }

    internal static void LogVanishInput(
        PlayerControl player,
        string side,
        bool ownerMod,
        bool simpleAdminEnabled,
        string method,
        string action)
    {
        Logger.Info(
            $"EVILHACKER_VANISH_INPUT Side={side} " +
            $"PlayerId={player.PlayerId} ClientId={player.GetClientId()} " +
            $"OwnerMod={ownerMod} SimpleAdminEnabled={simpleAdminEnabled} " +
            $"Method={method} Action={action}",
            nameof(EvilHacker));
    }

    internal static void LogVanishHostRequest(
        PlayerControl player,
        string source,
        string action)
    {
        Logger.Info(
            $"EVILHACKER_VANISH_HOST_REQUEST PlayerId={player.PlayerId} " +
            $"ClientId={player.GetClientId()} Source={source} Action={action}",
            nameof(EvilHacker));
    }

    internal static void LogNativeVanishBlock(
        PlayerControl player,
        string stage,
        bool blocked)
    {
        Logger.Info(
            $"EVILHACKER_VANISH_NATIVE_BLOCK PlayerId={player.PlayerId} " +
            $"Stage={stage} Blocked={blocked}",
            nameof(EvilHacker));
    }

    // EVILHACKER_SIMPLEADMIN_ALL_OWNER_TOGGLE_AB_V1:
    // Host / Remote MOD / Remote VanillaのCheckVanish入力を
    // native invisibilityに依存せずHost-authorityのSimpleAdmin状態で反転する。
    public static bool TryConsumeVanishToggle(PlayerControl player)
    {
        if (
            !AmongUsClient.Instance.AmHost ||
            player == null ||
            player.GetRoleClass() is not EvilHacker evilHacker)
        {
            return false;
        }

        return
            TryConsumeVanish(
                player,
                requestOpen: !evilHacker.isRealtimeAdminMode);
    }

    public static bool TryConsumeVanish(PlayerControl player, bool requestOpen)
    {
        if (!AmongUsClient.Instance.AmHost ||
            player == null ||
            player.GetRoleClass() is not EvilHacker evilHacker)
        {
            return false;
        }

        if (!evilHacker.useRealtimeAdmin)
        {
            Logger.Info(
                $"EVILHACKER_VANISH_SIMPLEADMIN PlayerId={player.PlayerId} " +
                $"SimpleAdminEnabled=False Action=OFF_STALE_CONSUMED " +
                $"RequestedOpen={requestOpen} Cooldown={evilHacker.simpleAdminCooldown}",
                nameof(EvilHacker));
            return true;
        }

        // EvilHackerのnative Phantom入力はHost authorityでconsumeする。
        // 62=OPEN / 64=CLOSE を明示状態として扱い、stale packetで逆方向へtoggleしない。
        if (!GameStates.IsInTask || !player.IsAlive())
        {
            Logger.Info(
                $"EVILHACKER_VANISH_SIMPLEADMIN PlayerId={player.PlayerId} " +
                $"SimpleAdminEnabled=True Action=INACTIVE_STALE_CONSUMED " +
                $"CurrentOpen={evilHacker.isRealtimeAdminMode} RequestedOpen={requestOpen} " +
                $"Cooldown={evilHacker.simpleAdminCooldown}",
                nameof(EvilHacker));
            return true;
        }

        var previousOpen = evilHacker.isRealtimeAdminMode;
        var stateChanged = previousOpen != requestOpen;

        evilHacker.isRealtimeAdminMode = requestOpen;
        evilHacker.previousSnapshotKey = string.Empty;
        evilHacker.lastSentLiveText = string.Empty;
        evilHacker.nextRealtimeAdminRefresh = 0f;

        if (!requestOpen)
        {
            evilHacker.lastAdminSnapshot = null;
        }

        // stateChanged=falseでもowner stateを再assertする。
        // MOD ownerはCustomRoleSync + local Phantom stateで再正規化し、
        // Vanilla ownerだけnative StartVanish/StartAppearを維持する。
        evilHacker.RearmVanishButton();

        if (requestOpen)
        {
            evilHacker.RefreshRealtimeAdmin(forceNotify: true);
        }
        else
        {
            // EVILHACKER_REMOTE_MOD_ACTUAL_CLOSE_RESTORE_AB_V1:
            // Host-authorityの実CLOSE経路から、owner表示の復元を必ず実行する。
            // Host-local / Vanillaは従来どおりNotifyRoles、
            // Remote MODだけはtargeted SetNameでcanonical名へ戻す。
            evilHacker.RestoreRealtimeAdminOwnerDisplay();
        }

        var action =
            requestOpen
                ? (stateChanged ? "OPEN_ADMIN" : "DUPLICATE_OPEN_REASSERT")
                : (stateChanged ? "CLOSE_ADMIN" : "STALE_CLOSE_NORMALIZE");

        Logger.Info(
            $"EVILHACKER_VANISH_SIMPLEADMIN PlayerId={player.PlayerId} " +
            $"SimpleAdminEnabled=True Action={action} " +
            $"PreviousOpen={previousOpen} RequestedOpen={requestOpen} " +
            $"StateChanged={stateChanged} Cooldown={evilHacker.simpleAdminCooldown}",
            nameof(EvilHacker));
        return true;
    }
    private void RearmVanishButton()
    {
        // Host-local は v6180.265 で連続toggle実績があるため既存方式を維持する。
        if (Player.AmOwner)
        {
            try
            {
                if (HudManager.InstanceExists && Player.Data?.Role?.Ability != null)
                {
                    HudManager.Instance.AbilityButton.SetFromSettings(Player.Data.Role.Ability);
                }
            }
            catch (Exception error)
            {
                Logger.Warn($"Host-local Vanish button re-arm failed: {error.Message}", nameof(EvilHacker));
            }

            Player.RpcResetAbilityCooldown();
            return;
        }

        var clientId = Player.GetClientId();
        if (clientId < 0)
        {
            return;
        }

        // EVILHACKER_REMOTE_MOD_NATIVE_LIFECYCLE_TARGETED_AB_V1:
        // Remote MOD ownerもnative Phantom lifecycleを本人targetedで完結させる。
        // StartVanish / StartAppearがAbility state machineとボタン遷移を担当し、
        // CustomRoleSyncはSimpleAdminのauthoritative OPEN/CLOSEだけを同期する。
        // 長いSimpleAdmin SetNameはRemote MODへ送信しない。
        if (Player.IsModClient())
        {
            var modSender = CustomRpcSender.Create(
                "EvilHacker MOD Owner Native Lifecycle Targeted A/B v1",
                SendOption.Reliable);

            modSender.StartMessage(clientId);

            if (isRealtimeAdminMode)
            {
                modSender.StartRpc(Player.NetId, RpcCalls.StartVanish)
                    .EndRpc();
            }
            else
            {
                modSender.StartRpc(Player.NetId, RpcCalls.StartAppear)
                    .Write(true)
                    .EndRpc();
            }

            modSender.StartRpc(
                    PlayerControl.LocalPlayer.NetId,
                    (byte)CustomRPC.CustomRoleSync)
                .Write(Player.PlayerId)
                .Write(RealtimeAdminStateSync)
                .Write(isRealtimeAdminMode)
                .EndRpc();

            modSender.EndMessage();
            modSender.SendMessage();

            Logger.Info(
                $"EVILHACKER_REMOTE_MOD_NATIVE_LIFECYCLE_TARGETED_AB_V1 " +
                $"PlayerId={Player.PlayerId} ClientId={clientId} " +
                $"AdminOpen={isRealtimeAdminMode} OwnerMod=True " +
                $"NativeAction={(isRealtimeAdminMode ? "START_VANISH_63" : "START_APPEAR_65")} " +
                $"StateSync=CUSTOM_ROLE_SYNC Targeted=True Broadcast=False NetworkSetName=False",
                nameof(EvilHacker));
            return;
        }

        // Vanilla ownerにはCustomRoleSyncがないため、既存のowner-only native lifecycleを維持する。
        // EVILHACKER_VANILLA_COMPACT_NATIVE_LIFECYCLE_AB_V3:
        // v6180.316のowner-only Phantom lifecycleを復元する。
        // SimpleAdmin表示はv6180.326のCompact <=220 UTF8 bytesを維持する。
        // StartVanish / StartAppearはVanilla本人だけへtargeted送信しbroadcastしない。
        var vanillaSender = CustomRpcSender.Create(
            "EvilHacker Vanilla Owner Phantom Lifecycle Compact A/B v3",
            SendOption.Reliable);

        vanillaSender.StartMessage(clientId);

        if (isRealtimeAdminMode)
        {
            vanillaSender.StartRpc(Player.NetId, RpcCalls.StartVanish)
                .EndRpc();
        }
        else
        {
            vanillaSender.StartRpc(Player.NetId, RpcCalls.StartAppear)
                .Write(true)
                .EndRpc();
        }

        vanillaSender.EndMessage();
        vanillaSender.SendMessage();

        Logger.Info(
            $"EVILHACKER_VANILLA_COMPACT_NATIVE_LIFECYCLE_AB_V3 " +
            $"PlayerId={Player.PlayerId} ClientId={clientId} " +
            $"AdminOpen={isRealtimeAdminMode} " +
            $"NativeAction={(isRealtimeAdminMode ? "START_VANISH_63" : "START_APPEAR_65")} " +
            $"CompactAdmin=True OwnerMod=False Broadcast=False",
            nameof(EvilHacker));    }
    // EVILHACKER_MEETING_NATIVE_NORMALIZE_AB_V1
    // Realtime Admin OPEN中のRemote ownerだけを、会議へ入る前にnative visibleへ戻す。
    // 他viewerにはStartAppearを送らない。
    private void NormalizeOwnerNativePhantomBeforeMeeting(string source)
    {
        if (!AmongUsClient.Instance.AmHost ||
            !useRealtimeAdmin ||
            !isRealtimeAdminMode ||
            Player == null ||
            !Player.IsAlive())
        {
            return;
        }

        // Host-localは既存の成功経路を変更しない。
        if (Player.AmOwner)
        {
            Logger.Info(
                $"EVILHACKER_MEETING_NATIVE_NORMALIZE_AB_V2 " +
                $"PlayerId={Player.PlayerId} ClientId={Player.GetClientId()} " +
                $"Source={source} Scope=HOST_LOCAL_NO_REMOTE_RPC",
                nameof(EvilHacker));
            return;
        }

        var clientId = Player.GetClientId();
        if (clientId < 0)
        {
            Logger.Warn(
                $"EVILHACKER_MEETING_NATIVE_NORMALIZE_AB_V2 " +
                $"PlayerId={Player.PlayerId} Source={source} " +
                $"Action=SKIP_INVALID_CLIENT",
                nameof(EvilHacker));
            return;
        }

        // EVILHACKER_MEETING_REMOTE_MOD_NATIVE_NORMALIZE_TARGETED_AB_V1:
        // 会議前はRemote MOD本人にもnative StartAppear(false)をtargeted送信し、
        // Phantom lifecycleをvisibleへ正規化してからSimpleAdmin stateを閉じる。
        if (Player.IsModClient())
        {
            var modSender = CustomRpcSender.Create(
                "EvilHacker Meeting MOD Native Normalize Targeted A/B v1",
                SendOption.Reliable);

            modSender.StartMessage(clientId);

            modSender.StartRpc(Player.NetId, RpcCalls.StartAppear)
                .Write(false)
                .EndRpc();

            modSender.StartRpc(
                    PlayerControl.LocalPlayer.NetId,
                    (byte)CustomRPC.CustomRoleSync)
                .Write(Player.PlayerId)
                .Write(RealtimeAdminStateSync)
                .Write(false)
                .EndRpc();

            modSender.EndMessage();
            modSender.SendMessage();

            Logger.Info(
                $"EVILHACKER_MEETING_REMOTE_MOD_NATIVE_NORMALIZE_TARGETED_AB_V1 " +
                $"PlayerId={Player.PlayerId} ClientId={clientId} " +
                $"Source={source} NativeAction=START_APPEAR_65 " +
                $"ShouldAnimate=False StateSync=CUSTOM_ROLE_SYNC_FALSE " +
                $"OwnerMod=True Targeted=True Broadcast=False NetworkSetName=False",
                nameof(EvilHacker));
            return;
        }

        // Vanilla ownerはCustomRoleSyncを受信できないため既存native normalizeを維持する。
        // EVILHACKER_MEETING_VANILLA_COMPACT_NATIVE_NORMALIZE_AB_V3:
        // native lifecycle復元時は会議前にVanilla本人だけvisibleへ戻す。
        // v6180.316と同じStartAppear(false)、他viewerへのbroadcastはしない。
        var vanillaSender = CustomRpcSender.Create(
            "EvilHacker Meeting Vanilla Compact Native Normalize A/B v3",
            SendOption.Reliable);

        vanillaSender.StartMessage(clientId);
        vanillaSender.StartRpc(Player.NetId, RpcCalls.StartAppear)
            .Write(false)
            .EndRpc();
        vanillaSender.EndMessage();
        vanillaSender.SendMessage();

        Logger.Info(
            $"EVILHACKER_MEETING_VANILLA_COMPACT_NATIVE_NORMALIZE_AB_V3 " +
            $"PlayerId={Player.PlayerId} ClientId={clientId} " +
            $"Source={source} NativeAction=START_APPEAR_65 " +
            $"ShouldAnimate=False OwnerMod=False Broadcast=False",
            nameof(EvilHacker));    }
    private void ApplyRealtimeAdminStateFromHost(bool enabled)
    {
        if (AmongUsClient.Instance.AmHost)
        {
            return;
        }

        isRealtimeAdminMode = enabled;
        previousSnapshotKey = string.Empty;
        lastSentLiveText = string.Empty;
        nextRealtimeAdminRefresh = 0f;

        // EVILHACKER_REMOTE_MOD_CUSTOM_STATE_ONLY_AB_V1:
        // Remote MODのnative Phantom stateはHostから本人targetedで届く
        // StartVanish / StartAppearに一元化する。
        // CustomRoleSync受信側ではSetRoleInvisibilityを再実行せず、
        // SimpleAdminのauthoritative stateとlocal displayだけを更新する。

        if (enabled)
        {
            // NotifyRoles is Host-only; on a MOD owner this populates the local suffix
            // snapshot without creating a network write.
            RefreshRealtimeAdmin(forceNotify: true);
        }
        else
        {
            lastAdminSnapshot = null;
        }

        LogVanishInput(
            Player,
            "LOCAL_MOD",
            ownerMod: true,
            simpleAdminEnabled: useRealtimeAdmin,
            method: "CustomRoleSync",
            action: enabled ? "APPLY_AUTHORITATIVE_OPEN_SAFE_TRANSPORT" : "APPLY_AUTHORITATIVE_CLOSE_SAFE_TRANSPORT");
    }
    private void RefreshRealtimeAdmin(bool forceNotify = false)
    {
        nextRealtimeAdminRefresh = Time.time + EvilHackerTextMinimap.RefreshIntervalSeconds;
        var snapshot = EvilHackerTextMinimap.Capture(Player, showImpostorTeammates, canSeeDeadMark);
        if (!forceNotify && snapshot.Key == previousSnapshotKey)
        {
            return;
        }

        previousSnapshotKey = snapshot.Key;
        lastAdminSnapshot = snapshot;
        if (forceNotify || snapshot.LiveText != lastSentLiveText)
        {
            lastSentLiveText = snapshot.LiveText;
            PublishRealtimeAdminDisplay(snapshot.LiveText);
        }
    }

    // EVILHACKER_SIMPLEADMIN_UI_AB_V2:
    // Vanilla:
    //   existing NotifyRoles path.
    //
    // Remote MOD:
    //   NotifyRolesはMOD端末をskipするため、
    //   本人だけへexisting native SetNameをtargeted送信。
    //
    // Host MOD:
    //   existing TryGetRealtimeAdminSelfDisplay()から
    //   同じsnapshotをlocal rendererが直接取得する。
    private void PublishRealtimeAdminDisplay(string liveText)
    {
        if (Player == null)
        {
            return;
        }

        if (Player.AmOwner)
        {
            return;
        }

        // EVILHACKER_REMOTE_MOD_LOCAL_RENDER_V317:
        // Remote MODはRealtimeAdminStateSync受信後に同じsnapshot.LiveTextを端末内生成できる。
        // 公式サーバーへ長いSimpleAdmin SetNameを送らず、表示内容は既存Local Renderで維持する。
        if (Player.IsModClient())
        {
            Logger.Info(
                $"EVILHACKER_REMOTE_MOD_LOCAL_RENDER_V317 " +
                $"PlayerId={Player.PlayerId} ClientId={Player.GetClientId()} " +
                $"NetworkSetName=False LocalRender=True",
                nameof(EvilHacker));
            return;
        }

        // EVILHACKER_REMOTE_VANILLA_REALDATA_FULLPAYLOAD_TARGETED_AB_V1:
        //
        // The same finalized snapshot.LiveText used by Host/MOD is
        // sent only to the Vanilla EvilHacker owner.
        //
        // The former 220-byte truncation is not enforced on this path.
        // The previous Fungle stress A/B successfully exercised
        // 677 UTF-8 byte targeted payloads without converting to broadcast.
        // 677 bytes is runtime evidence, not a protocol maximum.
        var vanillaLiveText =
            liveText;

        var vanillaUtf8Bytes =
            System.Text.Encoding.UTF8.GetByteCount(
                vanillaLiveText);

        if (string.IsNullOrEmpty(vanillaLiveText))
        {
            Logger.Info(
                $"EVILHACKER_REMOTE_VANILLA_REALDATA_FULLPAYLOAD_TARGETED_AB_V1 " +
                $"MapId={Main.NormalOptions.MapId} " +
                $"PlayerId={Player.PlayerId} ClientId={Player.GetClientId()} " +
                $"RealDataEmpty=True NetworkSetName=False",
                nameof(EvilHacker));
            return;
        }

        Player.RpcSetNamePrivate(
            vanillaLiveText,
            false,
            Player,
            force: true);

        Logger.Warn(
            $"EVILHACKER_REMOTE_VANILLA_REALDATA_FULLPAYLOAD_TARGETED_AB_V1 " +
            $"MapId={Main.NormalOptions.MapId} " +
            $"PlayerId={Player.PlayerId} ClientId={Player.GetClientId()} " +
            $"RealDataChars={vanillaLiveText.Length} " +
            $"RealDataUtf8Bytes={vanillaUtf8Bytes} " +
            $"PreviousStressReferenceBytes=677 " +
            $"Targeted=True Broadcast=False " +
            $"NetworkSetName=True RealData=True",
            nameof(EvilHacker));
        return;
    }
    private void RestoreRealtimeAdminOwnerDisplay()
    {
        if (
            AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost ||
            Player == null)
        {
            return;
        }

        // EVILHACKER_REMOTE_MOD_CLOSE_SETNAME_RESTORE_AB_V2:
        // OPENはRemote MOD本人だけへのforced targeted SetName。
        // CLOSEも同じ本人targetへcanonical名をforced SetNameし、
        // existing RpcSetNamePrivateでLastNotifyNames cacheも同時に戻す。
        if (!Player.AmOwner && !Player.IsModClient())
        {
            var restoreName = Player.Data?.PlayerName;

            if (!string.IsNullOrEmpty(restoreName))
            {
                Player.RpcSetNamePrivate(
                    restoreName,
                    false,
                    Player,
                    force: true);

                Logger.Info(
                    $"EVILHACKER_REMOTE_MOD_CLOSE_SETNAME_RESTORE_AB_V2 " +
                    $"PlayerId={Player.PlayerId} ClientId={Player.GetClientId()} " +
                    $"RestoreName={restoreName} Broadcast=False",
                    nameof(EvilHacker));
            }
            else
            {
                Logger.Warn(
                    $"EVILHACKER_REMOTE_MOD_CLOSE_SETNAME_RESTORE_AB_V2 " +
                    $"PlayerId={Player.PlayerId} RestoreSkipped=EMPTY_PLAYER_DATA_NAME",
                    nameof(EvilHacker));
            }

            return;
        }

        Utils.NotifyRoles(
            SpecifySeer: Player);
    }

    private void DisableRealtimeAdmin(bool notifyRoles, bool clearSnapshot)
    {
        var wasEnabled = isRealtimeAdminMode;
        isRealtimeAdminMode = false;
        nextRealtimeAdminRefresh = 0f;
        previousSnapshotKey = string.Empty;
        lastSentLiveText = string.Empty;
        if (clearSnapshot)
        {
            lastAdminSnapshot = null;
        }

        if (wasEnabled && notifyRoles && AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost && Player != null)
        {
            RestoreRealtimeAdminOwnerDisplay();
        }
    }
    public bool CheckKillFlash(MurderInfo info) =>
        canSeeKillFlash && !info.IsSuicide && !info.IsAccident && info.AttemptKiller.Is(CustomRoleTypes.Impostor);

    private static readonly string ImpostorMark = "★".Color(Palette.ImpostorRed);
    /// <summary>相方がキルしたときに名前の下に通知を表示する長さ</summary>
    private static readonly TimeSpan NotifyDuration = TimeSpan.FromSeconds(10);

    private readonly struct MurderNotify
    {
        /// <summary>通知が作成された時間</summary>
        public DateTime CreatedAt { get; init; }
        /// <summary>キルが起きた部屋</summary>
        public SystemTypes Room { get; init; }
    }
}
