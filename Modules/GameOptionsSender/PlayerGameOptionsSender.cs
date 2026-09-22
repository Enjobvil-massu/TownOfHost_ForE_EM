using System.Linq;
using System.Runtime.CompilerServices;
using AmongUs.GameOptions;
using Hazel;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSystem.Linq;
using InnerNet;
using Mathf = UnityEngine.Mathf;

using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Neutral;
using TownOfHostForE.Roles.Animals;
using TownOfHostForE.Roles.Crewmate;
using TownOfHostForE.Roles.Impostor;
using TownOfHostForE.Roles.AddOns.Common;

namespace TownOfHostForE.Modules
{
    public class PlayerGameOptionsSender : GameOptionsSender
    {
        // GAMEOPTIONS_EXACT_DUP_TARGETED_SUPPRESS_AB_V4:
        // Official-server safety. Only SyncAllSettings-origin targeted sends
        // are eligible. GameId/client/index/every serialized byte must exactly
        // match a successful send from the previous 100ms.
        private sealed class RecentTargetedOptionsSend
        {
            internal long SentAtTicks;
            internal byte[] Bytes = new byte[0];
        }

        private static readonly global::System.Collections.Generic.Dictionary<string, RecentTargetedOptionsSend>
            RecentTargetedOptionsSends = new();

        private static readonly long ExactDuplicateTargetedWindowTicks =
            global::System.TimeSpan.FromMilliseconds(100).Ticks;

        private static string TargetedOptionsKey(
            int targetClientId,
            byte logicOptionsIndex)
            => $"{AmongUsClient.Instance.GameId}:{targetClientId}:{logicOptionsIndex}";

        private static bool ShouldSuppressExactTargetedOptions(
            Il2CppStructArray<byte> optionArray,
            byte logicOptionsIndex,
            int targetClientId,
            string origin)
        {
            if (!global::System.String.Equals(
                    origin,
                    "SyncAllSettings",
                    global::System.StringComparison.Ordinal))
            {
                return false;
            }

            if (targetClientId < 0 || optionArray == null)
                return false;

            var key = TargetedOptionsKey(
                targetClientId,
                logicOptionsIndex);

            if (!RecentTargetedOptionsSends.TryGetValue(
                    key,
                    out var previous))
            {
                return false;
            }

            var deltaTicks =
                global::System.DateTime.UtcNow.Ticks -
                previous.SentAtTicks;

            if (deltaTicks < 0 ||
                deltaTicks > ExactDuplicateTargetedWindowTicks ||
                previous.Bytes.Length != optionArray.Length)
            {
                return false;
            }

            for (var i = 0; i < optionArray.Length; i++)
            {
                if (previous.Bytes[i] != optionArray[i])
                    return false;
            }

            var deltaMs =
                deltaTicks / global::System.TimeSpan.TicksPerMillisecond;

            Logger.Info(
                $"Origin={origin} | TargetClientId={targetClientId} | " +
                $"LogicOptionsIndex={logicOptionsIndex} | Length={optionArray.Length} | " +
                $"DeltaMs={deltaMs}",
                "GameOptionsExactDuplicateSuppress");

            return true;
        }

        private static void RememberTargetedOptionsSend(
            Il2CppStructArray<byte> optionArray,
            byte logicOptionsIndex,
            int targetClientId,
            string origin)
        {
            if (!global::System.String.Equals(
                    origin,
                    "SyncAllSettings",
                    global::System.StringComparison.Ordinal))
            {
                return;
            }

            if (targetClientId < 0 || optionArray == null)
                return;

            try
            {
                if (RecentTargetedOptionsSends.Count > 64)
                    RecentTargetedOptionsSends.Clear();

                var bytes = new byte[optionArray.Length];

                for (var i = 0; i < optionArray.Length; i++)
                    bytes[i] = optionArray[i];

                RecentTargetedOptionsSends[
                    TargetedOptionsKey(
                        targetClientId,
                        logicOptionsIndex)] =
                    new RecentTargetedOptionsSend
                    {
                        SentAtTicks = global::System.DateTime.UtcNow.Ticks,
                        Bytes = bytes,
                    };
            }
            catch (global::System.Exception ex)
            {
                Logger.Warn(
                    $"Targeted GameOptions dedupe cache update failed: " +
                    $"{ex.GetType().Name}: {ex.Message}",
                    "GameOptionsExactDuplicateSuppress");
            }
        }
        public static void SetDirty(
            PlayerControl player,
            [CallerMemberName] string origin = "Unknown") =>
            SetDirty(player.PlayerId, origin);
        public static void SetDirty(
            byte playerId,
            [CallerMemberName] string origin = "Unknown")
        {
            var senders = AllSenders.OfType<PlayerGameOptionsSender>()
                .Where(sender => sender.player.PlayerId == playerId)
                .ToList();
            CriticalStartFlowPacketDiagnostic.Mark(
                "GAMEOPTIONS_DIRTY_PLAYER",
                $"Origin={origin} PlayerId={playerId} SenderCount={senders.Count}");
            senders.ForEach(sender => sender.SetDirty(origin));
        }
        public static void SetDirtyToAll(
            [CallerMemberName] string origin = "Unknown")
        {
            var senders = AllSenders.OfType<PlayerGameOptionsSender>().ToList();
            CriticalStartFlowPacketDiagnostic.Mark(
                "GAMEOPTIONS_DIRTY_ALL",
                $"Origin={origin} SenderCount={senders.Count}");
            senders.ForEach(sender => sender.SetDirty(origin));
        }

        public override IGameOptions BasedGameOptions =>
            Main.RealOptionsData.Restore(new NormalGameOptionsV11(new UnityLogger().Cast<ILogger>()).Cast<IGameOptions>());
        public override bool IsDirty { get; protected set; }

        public PlayerControl player;
        private string lastDirtyOrigin = "Unknown";

        public PlayerGameOptionsSender(PlayerControl player)
        {
            this.player = player;
        }
        public void SetDirty([CallerMemberName] string origin = "Unknown")
        {
            var wasDirty = IsDirty;
            lastDirtyOrigin = origin;
            IsDirty = true;
            TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.Dirty(this, origin));
            CriticalStartFlowPacketDiagnostic.Mark(
                "GAMEOPTIONS_DIRTY_SET",
                $"Origin={origin} PlayerId={player?.PlayerId.ToString() ?? "NULL"} WasDirty={wasDirty} NowDirty={IsDirty}");
        }

        public override void SendGameOptions()
        {
            if (player.AmOwner)
            {
                var opt = BuildGameOptions();
                foreach (var com in GameManager.Instance.LogicComponents)
                {
                    if (com.TryCast<LogicOptions>(out var lo))
                        lo.SetGameOptions(opt);
                }
                GameOptionsManager.Instance.CurrentGameOptions = opt;
                TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.OwnerApply(this, opt.GameMode));
            }
            else base.SendGameOptions();
        }

        public override void SendOptionsArray(Il2CppStructArray<byte> optionArray)
        {
            if (!player.TryGetClientId(out var clientId)) return;
            for (byte i = 0; i < GameManager.Instance.LogicComponents.Count; i++)
            {
                if (GameManager.Instance.LogicComponents[i].TryCast<LogicOptions>(out _))
                {
                    if (ShouldSuppressExactTargetedOptions(
                            optionArray,
                            i,
                            clientId,
                            lastDirtyOrigin))
                    {
                        continue;
                    }

                    base.SendOptionsArray(
                        optionArray,
                        i,
                        clientId);

                    RememberTargetedOptionsSend(
                        optionArray,
                        i,
                        clientId,
                        lastDirtyOrigin);
                }
            }
        }
        public static void RemoveSender(PlayerControl player)
        {
            var sender = AllSenders.OfType<PlayerGameOptionsSender>()
            .FirstOrDefault(sender => sender.player.PlayerId == player.PlayerId);
            if (sender == null) return;
            sender.player = null;
            AllSenders.Remove(sender);
        }
        public override IGameOptions BuildGameOptions()
        {
            if (Main.RealOptionsData == null)
            {
                Main.RealOptionsData = new OptionBackupData(GameOptionsManager.Instance.CurrentGameOptions);
            }

            var opt = BasedGameOptions;
            AURoleOptions.SetOpt(opt);
            var state = PlayerState.GetByPlayerId(player.PlayerId);
            opt.BlackOut(state.IsBlackOut);

            CustomRoles role = player.GetCustomRole();
            switch (role.GetCustomRoleTypes())
            {
                case CustomRoleTypes.Impostor:
                    AURoleOptions.ShapeshifterCooldown = Options.DefaultShapeshiftCooldown.GetFloat();
                    break;
                case CustomRoleTypes.Madmate:
                    // MADMATE_VENT_MAX_INFINITY_999_V1:
                    // MadmateVentMaxTimeの保存index 0はUI上「∞」として扱い、
                    // 公式Engineer carrierへは999秒を渡す。正数設定とCooldown挙動は変更しない。
                    var madmateVentCooldown = Options.MadmateVentCooldown.GetFloat();
                    var madmateVentMaxTime = Options.MadmateVentMaxTime.GetFloat();
                    AURoleOptions.EngineerCooldown = madmateVentCooldown <= 0f ? 1f : madmateVentCooldown;
                    AURoleOptions.EngineerInVentMaxTime = madmateVentMaxTime <= 0f ? 999f : madmateVentMaxTime;

                    if (Options.MadmateCanSeeOtherVotes.GetBool())
                        opt.SetBool(BoolOptionNames.AnonymousVotes, false);
                    break;
            }
            if (!role.IsImpostor())
            {
                AURoleOptions.NoisemakerImpostorAlert = true;
            }

            var roleClass = player.GetRoleClass();
            roleClass?.ApplyGameOptions(opt);
            // MADMATE_SHARED_VISION_FINAL_AUTHORITY_V1:
            // Role-specific vision runs first; the shared Madmate selection is the final base vision.
            // AddLight / Sunglasses sub-role modifiers are intentionally applied afterward.
            if (role.GetCustomRoleTypes() == CustomRoleTypes.Madmate)
            {
                opt.SetVision(Options.MadmateHasImpostorVision.GetValue() == 1);
            }
            // VIPER_OBSERVER_DISSOLVE_OPTIONS_AB_V1:
            // Native Viper corpse timing is observer-local.
            NormalViper.ApplyObserverDissolveOptionsAbV1(opt);
            //switch (role)
            //{
            //    case CustomRoles.EgoSchrodingerCat:
            //        opt.SetVision(true);
            //        break;
            //    case CustomRoles.JSchrodingerCat:
            //        ((Jackal)roleClass).ApplyGameOptions(opt);
            //        break;
            //    case CustomRoles.OSchrodingerCat:
            //        ((Opportunist)roleClass).ApplyGameOptions(opt);
            //        break;
            //    case CustomRoles.DSchrodingerCat:
            //        ((DarkHide)roleClass).ApplyGameOptions(opt);
            //        break;
            //    case CustomRoles.GSchrodingerCat:
            //        ((Gizoku)roleClass).ApplyGameOptions(opt);
            //        break;
            //    case CustomRoles.ASchrodingerCat:
            //        if (Coyote.RoleInfo.IsEnable)
            //        {
            //            ((Coyote)roleClass).ApplyGameOptions(opt);
            //        }
            //        else if (Braki.RoleInfo.IsEnable)
            //        {
            //            ((Braki)roleClass).ApplyGameOptions(opt);
            //        }
            //        else if (Leopard.RoleInfo.IsEnable)
            //        {
            //            ((Leopard)roleClass).ApplyGameOptions(opt);
            //        }
            //        break;
            //}

            foreach (var subRole in player.GetCustomSubRoles())
            {
                switch (subRole)
                {
                    case CustomRoles.AddWatch: AddWatch.ApplyGameOptions(opt); break;
                    case CustomRoles.AddLight: AddLight.ApplyGameOptions(opt); break;
                    case CustomRoles.Sunglasses: Sunglasses.ApplyGameOptions(opt); break;
                }
            }
            Blinder.ApplyGameOptionsByOther(player.PlayerId, opt);
            Kraken.ApplyGameOptionsByOther(player.PlayerId, opt);

            if (Main.AllPlayerKillCooldown.TryGetValue(player.PlayerId, out var killCooldown))
            {
                AURoleOptions.KillCooldown = Mathf.Max(0f, killCooldown);
            }

            if (Main.AllPlayerSpeed.TryGetValue(player.PlayerId, out var speed))
            {
                AURoleOptions.PlayerSpeedMod = Mathf.Clamp(speed, Main.MinSpeed, 3f);
            }

            var hadTasksBeforeBuild = state.taskState.hasTasks;
            var hasTasksAtBuild = Utils.HasTasks(player.Data, false);
            state.taskState.hasTasks = hasTasksAtBuild;
            CriticalStartFlowPacketDiagnostic.Mark(
                "GAMEOPTIONS_BUILD_TASKSTATE_WRITE",
                $"HadTasksBefore={hadTasksBeforeBuild} EvaluatedHasTasks={hasTasksAtBuild} " +
                CriticalStartFlowPacketDiagnostic.PlayerSnapshot(player));
            if (Options.GhostCanSeeOtherVotes.GetBool() && player.Data.IsDead)
                opt.SetBool(BoolOptionNames.AnonymousVotes, false);
            if (Options.AdditionalEmergencyCooldown.GetBool() &&
                Options.AdditionalEmergencyCooldownThreshold.GetInt() <= Utils.AllAlivePlayersCount)
            {
                opt.SetInt(
                    Int32OptionNames.EmergencyCooldown,
                    Options.AdditionalEmergencyCooldownTime.GetInt());
            }
            if (Options.SyncButtonMode.GetBool() && Options.SyncedButtonCount.GetValue() <= Options.UsedButtonCount)
            {
                opt.SetInt(Int32OptionNames.EmergencyCooldown, 3600);
            }
            if ((Options.CurrentGameMode == CustomGameMode.HideAndSeek || Options.IsStandardHAS) && Options.HideAndSeekKillDelayTimer > 0)
            {
                opt.SetFloat(FloatOptionNames.ImpostorLightMod, 0f);
                if (player.Is(CountTypes.Impostor))
                {
                    AURoleOptions.PlayerSpeedMod = Main.MinSpeed;
                }
            }
            MeetingTimeManager.ApplyGameOptions(opt);

            AURoleOptions.ShapeshifterCooldown = Mathf.Max(1f, AURoleOptions.ShapeshifterCooldown);
            AURoleOptions.ProtectionDurationSeconds = 0f;

            // TASK_BAR_MODE_TYPED_SYNC_PERMANENT: IGameOptionsをInterop TryCastして公式値を型付き反映
            if (opt.TryCast<AmongUs.GameOptions.NormalGameOptionsV11>(out var normalOptions)
                && GameManager.Instance != null
                && GameManager.Instance.LogicOptions != null)
            {
                normalOptions.TaskBarMode =
                    (AmongUs.GameOptions.TaskBarMode)(int)
                    GameManager.Instance.LogicOptions.GetTaskBarMode();
            }
            return opt;
        }

        public override bool AmValid()
        {
            return base.AmValid() && player != null && !player.Data.Disconnected && Main.RealOptionsData != null;
        }
    }
}
