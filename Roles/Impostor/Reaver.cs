using System.Collections.Generic;
using AmongUs.GameOptions;
using Hazel;
using UnityEngine;

using TownOfHostForE.Roles.Core.Interfaces;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Class;

namespace TownOfHostForE.Roles.Impostor
{
    public sealed class Reaver : RoleBase, IImpostor, ISharedBeamPetAbility
    {

        /// <summary>
        ///  20000:TOH4E役職
        ///   1000:陣営 1:crew 2:imp 3:Third 4:Animals
        ///    100:役職ID
        /// </summary>
        public static readonly SimpleRoleInfo RoleInfo =
            SimpleRoleInfo.Create(
                typeof(Reaver),
                player => new Reaver(player),
                CustomRoles.Reaver,
                () => RoleTypes.Impostor,
                CustomRoleTypes.Impostor,
                61000,
                SetupOptionItem,
                "リーヴァー",
                countType: CountTypes.Impostor
            );
        public Reaver(PlayerControl player)
        : base(
            RoleInfo,
            player,
            () => HasTask.False
        )
        {
            beam = new SharedBeamController(player);
            KillCooldown = OptionKillCooldown.GetFloat();
            SolarBeamCount = OptionSolarBeamCount.GetInt();
            fireDelay = OptionFireDelay.GetInt();

            //他視点用のMarkメソッド登録
            CustomRoleManager.MarkOthers.Add(SharedBeamController.GetWarningMark);

            UpdateTime = 0;
            ReaverVector = 0;
            BeamPostion = Vector3.zero;
            ReaverFireing = new ();
            firechk = false;
            reaverKawaiinePosition = new();
            fireCount = -1;
            AnimationIndex = -1;
            secTimer = 0;
        }

        private static OptionItem OptionKillCooldown = null!;
        private static OptionItem OptionSolarBeamCount = null!;
        private static OptionItem OptionFireDelay = null!;
        private static float KillCooldown;
        private readonly SharedBeamController beam;

        PlayerControl ISharedBeamWarningSource.BeamWarningOwner => Player;
        bool ISharedBeamWarningSource.BeamWarningActive => beam.WarningActive;
        Vector3 ISharedBeamWarningSource.BeamWarningPosition => beam.WarningPosition;
        bool ISharedBeamPetAbility.IsBeamPetAbilityActive => IsPetAbilityActive;
        string ISharedBeamPetAbility.BeamVisualMark => beam.VisualMark;
        void ISharedBeamPetAbility.ResetBeamVisuals() => ResetReaver();
        private int AnimationIndex = -1;

        //時間観測用
        private float UpdateTime;
        private int ReaverVector { get => beam.Direction; set => beam.Direction = value; }

        // REAVER_OWNER_UI_SYNC_PERMANENT
        // 同一MOD非ホスト本人へは能力権限ではなく表示専用状態だけを同期する。
        private const byte SyncBeamStateABN8 = 0;
        private const byte SyncOwnerUiStateABN8 = 1;
        private bool remoteUiValidABN8 = false;
        private int remoteFireDelayABN8 = 0;
        private int remoteSolarBeamCountABN8 = 0;
        private int remoteAnimationIndexABN8 = -1;
        private bool remoteVisualActiveABN8 = false;
        private bool ownerUiSendInitializedABN8 = false;
        private int lastSentFireDelayABN8 = int.MinValue;
        private int lastSentSolarBeamCountABN8 = int.MinValue;
        private int lastSentAnimationIndexABN8 = int.MinValue;
        private bool lastSentVisualActiveABN8 = false;
        private int SolarBeamCount = 0;
        private Vector3 BeamPostion { get => beam.Position; set => beam.Position = value; }
        private static List<ReaverFire> ReaverFireing = new();
        private bool firechk { get => beam.Active; set => beam.Active = value; }
        private Vector3 reaverKawaiinePosition = new ();
        private int fireCount { get => beam.ChargeCount; set => beam.ChargeCount = value; }
        private int fireDelay = 0;
        private int secTimer = 0;
        // ROLEBUGTRACE_STAGE2_PHASEB_PRIVATE_STATE_PERMANENT_ACCESSOR
        // RoleBugTrace専用のread-only診断アクセサ。役職ロジックは変更しない。
        internal int RoleBugTraceSolarBeamCount => SolarBeamCount;
        internal int RoleBugTraceFireCount => fireCount;

        private Dictionary<int, string> nyaonyaoAnimation = new()
        {
            { 5, "\n　　　\n　│　\n─〇─\n　│　"},
            { 4, "\n　　　　　\n　　│　　\n　＼　／　\n─　〇　─\n　／　＼　\n　　│　　"},
            { 3, "\n　　　　　　　\n　　　│　　　\n　＼　　　／　\n　　　│　　　\n─　─〇─　─\n　　　│　　　\n　／　　　＼　\n　　　│　　　"},
            { 2, "\n　　　　　　　　　\n　　　　│　　　　\n　＼＼│　│／／　\n　＼　　│　　／　\n　─　＼　／　─　\n─　─　〇　─　─\n　─　／　＼　─　\n　／　　│　　＼　\n　／／│　│＼＼　\n　　　　│　　　　"},
            { 1, "\n　　　　　　　　　　　\n　　　　　　　　　　　\n　　　　　│　　　　　\n　＼＼　│　│　／／　\n　＼　　　│　　　／　\n　　　＼│　│／　　　\n　─　─　│　─　─　\n─　─　─〇─　─　─\n　─　─　│　─　─　\n　　　／│　│＼　　　\n　／　　　│　　　＼　\n　／／　│　│　＼＼　\n　　　　　│　　　　　"},
            { 0, "\n　　　　　　　　　　　　　\n　　　　│　│　│　　　　\n　＼＼│　│　│　│／／　\n　＼　　　　│　　　　／　\n　─　＼＼│　│／／　─　\n─　　＼　　│　　／　　─\n　─　─　＼　／　─　─　\n─　─　─　〇　─　─　─\n　─　─　／　＼　─　─　\n─　　／　　│　　＼　　─\n　─　／／│　│＼＼　─　\n　／　　　　│　　　　＼　\n　／／│　│　│　│＼＼　\n　　　　│　│　│　　　　"},
            { 6, "\n　　　　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　／　　　　　　　　　　　\n　　　　　　　　　／　　　　　　　　　　　　\n　　　　　　　　／　　　　　　　　　　　　　\n　　　　　　　／／／　　　　　　　　　　　　\n　　　　　　│／　　　　　　　　　　　　　　\n　　　　／　　／─　　　　　　　　　　　　　\n　　　　　─│─　─　　　　　　　　　　　　\n　　　　＼　　＼─　　　　　　　　　　　　　\n　　　　　　│＼　　　　　　　　　　　　　　\n　　　　　　　＼＼＼　　　　　　　　　　　　\n　　　　　　　　＼　　　　　　　　　　　　　\n　　　　　　　　　＼　　　　　　　　　　　　\n　　　　　　　　　　＼　　　　　　　　　　　"},
            { 7, "\n　　　　　　　　　　　　　　　　　　　　　　\n　　　　　　／／　　　　　　　　　　　　　　\n　　　　　　／　　　　　　　　　　　　　　　\n　　　／　／　　　　　　　　　　　　　　　　\n　　　　／／／　　　　　　　　　　　　　　　\n　　　│／　─　─　　　　　　　　　　　　　\n　／　　／─　─　─　　　　　　　　　　　　\n　　─│─　─　─　　　　　　　　　　　　　\n　＼　　＼─　─　─　　　　　　　　　　　　\n　　　│＼　─　─　　　　　　　　　　　　　\n　　　　＼＼＼　　　　　　　　　　　　　　　\n　　　＼　＼　　　　　　　　　　　　　　　　\n　　　　　　＼　　　　　　　　　　　　　　　\n　　　　　　＼＼　　　　　　　　　　　　　　"},
            { 8, "\n　　　　　　　　　　　　　　　　　　　　　　\n　　／／　　　　　　　　　　　　　　　　　　\n　　／　　　　　　　　　　　　　　　　　　　\n　／　　　　　　─　　　　　　　　　　　　　\n／／／　　─　　　　　　　　　　　　　　　　\n／　─　─　─　─　　　　　　　　　　　　　\n／─　─　─　─　　　　　　　　　　　　　　\n─　─　─　　　　　　　　　　　　　　　　　\n＼─　─　─　─　─　　　　　　　　　　　　\n＼　─　─　─　─　　　　　　　　　　　　　\n＼＼＼　　─　　　　　　　　　　　　　　　　\n　＼　　　　　　─　　　　　　　　　　　　　\n　　＼　　　　　　　　　　　　　　　　　　　\n　　＼＼　　　　　　　　　　　　　　　　　　"},
            { 9, "\n　　　　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　　　　　　　　　　　　　\n　　─　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　　　　　　　　　　　　　\n─　─　　　　　　　　　　　　　　　　　　　\n　─　　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　　　　　　　　　　　　　\n　─　─　　　　　　　　　　　　　　　　　　\n─　─　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　　　　　　　　　　　　　\n　　─　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　　　　　　　　　　　　　"},
            { 10, "\n　　　　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　＼　　　　　　　　　　　\n　　　　　　　　　　　＼　　　　　　　　　　\n　　　　　　　　　　　　＼　　　　　　　　　\n　　　　　　　　　　　＼＼＼　　　　　　　　\n　　　　　　　　　　　　　＼│　　　　　　　\n　　　　　　　　　　　　─＼　　＼　　　　　\n　　　　　　　　　　　─　─│─　　　　　　\n　　　　　　　　　　　　─／　　／　　　　　\n　　　　　　　　　　　　　／│　　　　　　　\n　　　　　　　　　　／／／　　　　　　　　　\n　　　　　　　　　　　／　　　　　　　　　　\n　　　　　　　　　　／　　　　　　　　　　　\n　　　　　　　　　／　　　　　　　　　　　　"},
            { 11, "\n　　　　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　　　　　＼＼　　　　　　\n　　　　　　　　　　　　　　　＼　　　　　　\n　　　　　　　　　　　　　　　　＼　＼　　　\n　　　　　　　　　　　　　　　＼＼＼　　　　\n　　　　　　　　　　　　　─　─　＼│　　　\n　　　　　　　　　　　　─　─　─＼　　＼　\n　　　　　　　　　　　　　─　─　─│─　　\n　　　　　　　　　　　　─　─　─／　　／　\n　　　　　　　　　　　　　─　─　／│　　　\n　　　　　　　　　　　　　　　／／／　　　　\n　　　　　　　　　　　　　　　　／　／　　　\n　　　　　　　　　　　　　　　／　　　　　　\n　　　　　　　　　　　　　　／／　　　　　　"},
            { 12, "\n　　　　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　　　　　　　　　＼＼　　\n　　　　　　　　　　　　　　　　　　　＼　　\n　　　　　　　　　　　　　─　　　　　　＼　\n　　　　　　　　　　　　　　　　─　　＼＼＼\n　　　　　　　　　　　　　─　─　─　─　＼\n　　　　　　　　　　　　　　─　─　─　─＼\n　　　　　　　　　　　　　　　　　─　─　─\n　　　　　　　　　　─　─　─　─　─　─／\n　　　　　　　　　　　─　─　─　─　─　／\n　　　　　　　　　　　　　　　　─　　／／／\n　　　　　　　　　　　　　─　　　　　　／　\n　　　　　　　　　　　　　　　　　　　／　　\n　　　　　　　　　　　　　　　　　　／／　　"},
            { 13, "\n　　　　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　　　　　　　　　　─　　\n　　　　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　　　　　　　　　　─　─\n　　　　　　　　　　　　　　　　　　　　─　\n　　　　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　　　　　　　　　─　─　\n　　　　　　　　　　　　　　　　　　　─　─\n　　　　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　　　　　　　　　　─　　\n　　　　　　　　　　　　　　　　　　　　　　\n　　　　　　　　　　　　　　　　　　　　　　"},
        };
        // REAVER_REMOTE_VANILLA_SAFE_BEAM_ANIMATION_AB_V2:
        // Vanilla本人だけ発射方向フレームを小さいSetName payloadへ置換する。
        // 充填フレーム0-5、ゲームロジック、Host/MOD表示は変更しない。
        private readonly Dictionary<int, string> reaverVanillaSafeBeamAnimationABV2 = new()
        {
            { 6, "\n◀━●" },
            { 7, "\n◀━━●" },
            { 8, "\n◀━━━●" },
            { 9, "\n◀━━━━●" },
            { 10, "\n●━▶" },
            { 11, "\n●━━▶" },
            { 12, "\n●━━━▶" },
            { 13, "\n●━━━━▶" }
        };

        private enum OptionName
        {
            ReaverSolarBeamCount,
            ReaverFireDelay
        }

        private static void SetupOptionItem()
        {
            OptionKillCooldown = FloatOptionItem.Create(RoleInfo, 10, GeneralOption.KillCooldown, new(1f, 180f, 1f), 30f, false)
                .SetLegacySavedIndexMultiplier(5)
                .SetValueFormat(OptionFormat.Seconds);
            OptionSolarBeamCount = IntegerOptionItem.Create(RoleInfo, 11, OptionName.ReaverSolarBeamCount, new(0, 10, 1), 5, false)
                .SetValueFormat(OptionFormat.None);
            OptionFireDelay = IntegerOptionItem.Create(RoleInfo, 12, OptionName.ReaverFireDelay, new(0, 60, 1), 30, false)
                .SetValueFormat(OptionFormat.Seconds);
        }
        public override void Add()
        {
            var playerId = Player.PlayerId;
            var MyReaver = new ReaverFire { ReaverID = playerId, Fired = false};
            ReaverFireing.Add(MyReaver);
        }
        public float CalculateKillCooldown() => KillCooldown;
        public override string GetProgressText(bool comms = false)
        {
            var useRemoteUiABN8 =
                !AmongUsClient.Instance.AmHost &&
                Player.AmOwner &&
                remoteUiValidABN8;

            var displayFireDelayABN8 =
                useRemoteUiABN8 ? remoteFireDelayABN8 : fireDelay;

            var displaySolarBeamCountABN8 =
                useRemoteUiABN8 ? remoteSolarBeamCountABN8 : SolarBeamCount;

            string returnString = "";

            if(displaySolarBeamCountABN8 > 0)
            {
                returnString = displayFireDelayABN8 > 0
                    ? $"「{displayFireDelayABN8}」"
                    : "『GO!!』";

                returnString += $"({displaySolarBeamCountABN8})";
                returnString = Utils.ColorString(Color.yellow,returnString);
            }
            else
            {
                returnString = Utils.ColorString(Color.gray, $"「もう力が出なそうだ...」");
            }

            return returnString;
        }
        public bool CanUseImpostorVentButton() => true;
        public bool CanUseSabotageButton() => true;

        public override void OnDestroy() => ResetReaver();

        public override void AfterMeetingTasks()
        {
            fireDelay = OptionFireDelay.GetInt();
        }

        public override void OnFixedUpdate(PlayerControl player)
        {
            if (Player.Data == null || Player.Data.Disconnected || !Player.IsAlive()) { ResetReaver(); return; }
            if (!AmongUsClient.Instance.AmHost) return;
            beam.TrackDirection();

            UpdateTime -= Time.fixedDeltaTime;
            if (UpdateTime < 0) UpdateTime = 0.5f; //0.5秒ごとの更新

            if (UpdateTime == 0.5f)
            {
                DelayCheck();
                AnimationCheck();
                FireBeam();
                SendOwnerUiStateRPCABN8();
            }
        }

        private void SetOwnerUiFallbackABN8()
        {
            if (AmongUsClient.Instance.AmHost || !Player.AmOwner) return;

            remoteFireDelayABN8 = fireDelay;
            remoteSolarBeamCountABN8 = SolarBeamCount;
            remoteAnimationIndexABN8 = AnimationIndex;
            remoteVisualActiveABN8 = firechk;
            remoteUiValidABN8 = true;
        }

        public override void OnStartMeeting()
        {
            // 会議開始時点から再チャージさせ、各mod clientのlocal表示も揃える。
            fireDelay = OptionFireDelay.GetInt();
            secTimer = 0;
            fireCount = -1;
            ReaverVector = 0;
            reaverKawaiinePosition = Vector3.zero;

            // 発射・アニメーション・矢印を停止し、既存Beam状態を同期する。
            ResetReaver();
            beam.ResetDirection(resetHistory: true);

        }

        public bool IsPetAbilityActive => firechk;

        public override void OnTouchPet(PlayerControl player)
        {
            if (SolarBeamCount <= 0) return;
            if (fireDelay > 0) return;
            if (firechk) return;

            beam.BeginDirectionSelection();
            reaverKawaiinePosition = Player.transform.position;

            fireCount = 3;
            AnimationIndex = 0;
            SolarBeamCount--;
            fireDelay = OptionFireDelay.GetInt();
            BeamPostion = Player.transform.position;

            SendOwnerUiStateRPCABN8(force: true);
            Utils.NotifyRoles(SpecifySeer: Player);
        }

        private void AnimationCheck()
        {
            // REAVER_LAST_SHOT_ANIMATION_AB_V1:
            // 残弾チェックはOnTouchPet側で完了している。
            // 発射開始後はSolarBeamCountが0でも現在の1発の描画を最後まで進める。
            if (!firechk) return;

            if (AnimationIndex <= nyaonyaoAnimation.Count -1)
            {
                //右向きか
                if (ReaverVector> 0 && AnimationIndex == 5)
                {
                    AnimationIndex = 10;
                }
                else if(ReaverVector < 0 && AnimationIndex == 9)
                {
                    AnimationIndex = 15;
                    return;
                }

                AnimationIndex++;
            }
        }

        private void DelayCheck()
        {
            if (fireDelay <= 0) return;
            secTimer++;
            //1秒に1回処理
            if (secTimer < 2) return;
            secTimer = 0;
            fireDelay--;
            Utils.NotifyRoles(SpecifySeer:Player);
        }

        private void FireBeam()
        {
            beam.FireBeam(
                ResetReaver,
                () => reaverCheck(Player.PlayerId, true),
                position => reaverCheckP(Player.PlayerId, position),
                SendBeamStateRPC);
        }

        private void ResetReaver()
        {
            if (!beam.Active && !beam.WarningActive && AnimationIndex < 0) { beam.RemoveArrows(); return; }
            beam.ResetActive();
            AnimationIndex = -1;

            beam.ResetDirection(resetHistory: false);
            SetOwnerUiFallbackABN8();

            reaverCheck(Player.PlayerId, false);
            reaverCheckP(Player.PlayerId, BeamPostion);

            beam.RemoveArrows();

            SendBeamStateRPC();
            SendOwnerUiStateRPCABN8(force: true);
            Utils.NotifyRoles();
        }

        // 同一MOD参加者向けのリーヴァービーム表示状態を同期する。
        // バニラ参加者にはCustom RPCを送信しない。
        private void SendBeamStateRPC() => beam.SyncVisual(AnimationIndex);

        private void SendOwnerUiStateRPCABN8(bool force = false)
        {
            beam.SyncVisual(AnimationIndex);
            if (!AmongUsClient.Instance.AmHost) return;
            if (PlayerControl.LocalPlayer == null || Player == null) return;
            if (Player.AmOwner || !Player.IsModClient()) return;

            if (ownerUiSendInitializedABN8 &&
                lastSentFireDelayABN8 == fireDelay &&
                lastSentSolarBeamCountABN8 == SolarBeamCount &&
                lastSentAnimationIndexABN8 == AnimationIndex &&
                lastSentVisualActiveABN8 == firechk)
            {
                return;
            }

            var clientIdABN8 = Player.GetClientId();
            if (clientIdABN8 < 0) return;

            var writerABN8 = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId,
                (byte)CustomRPC.CustomRoleSync,
                SendOption.Reliable,
                clientIdABN8);

            writerABN8.Write(Player.PlayerId);
            writerABN8.Write(SyncOwnerUiStateABN8);
            writerABN8.Write(fireDelay);
            writerABN8.Write(SolarBeamCount);
            writerABN8.Write(AnimationIndex);
            writerABN8.Write(firechk);

            AmongUsClient.Instance.FinishRpcImmediately(writerABN8);

            ownerUiSendInitializedABN8 = true;
            lastSentFireDelayABN8 = fireDelay;
            lastSentSolarBeamCountABN8 = SolarBeamCount;
            lastSentAnimationIndexABN8 = AnimationIndex;
            lastSentVisualActiveABN8 = firechk;
        }

        public override void ReceiveRPC(MessageReader reader)
        {
            if (AmongUsClient.Instance.AmHost) return;

            var syncKindABN8 = reader.ReadByte();

            if (syncKindABN8 == SyncBeamStateABN8)
            {
                beam.ReceiveVisual(reader);
                return;
            }

            if (syncKindABN8 != SyncOwnerUiStateABN8) return;

            var syncedFireDelayABN8 = reader.ReadInt32();
            var syncedSolarBeamCountABN8 = reader.ReadInt32();
            var syncedAnimationIndexABN8 = reader.ReadInt32();
            var syncedVisualActiveABN8 = reader.ReadBoolean();

            if (!Player.AmOwner) return;

            remoteFireDelayABN8 = syncedFireDelayABN8;
            remoteSolarBeamCountABN8 = syncedSolarBeamCountABN8;
            remoteAnimationIndexABN8 = syncedAnimationIndexABN8;
            remoteVisualActiveABN8 = syncedVisualActiveABN8;
            remoteUiValidABN8 = true;

        }

        private void reaverCheck(byte id,bool change)
        {
            foreach (var reaver in ReaverFireing)
            {
                if (reaver.ReaverID == id)
                {
                    reaver.Fired = change;
                    break;
                }
            }
        }
        private void reaverCheckP(byte id,Vector3 postion)
        {
            foreach (var reaver in ReaverFireing)
            {
                if (reaver.ReaverID == id)
                {
                    reaver.BeamPosition = postion;
                    break;
                }
            }
        }

        private static Vector3 SearchBeamPos(byte reaverId)
        {
            Vector3 pos = new();

            foreach (var nya in ReaverFireing)
            {
                if (nya.ReaverID == reaverId)
                {
                    pos = nya.BeamPosition;
                    break;
                }
            }

            return pos;
        }

        public static string GetMarkOthers(
            PlayerControl seer,
            PlayerControl seen,
            bool isForMeeting = false)
        {
            return SharedBeamController.GetWarningMark(
                seer,
                seen,
                isForMeeting);
        }
        public override string OverrideSpecialText()
        {
            if (!GameStates.IsInTask || GameStates.IsMeeting || !Player.IsAlive()) return "";

            var useRemoteUiABN8 =
                !AmongUsClient.Instance.AmHost &&
                Player.AmOwner &&
                remoteUiValidABN8;

            var displayVisualActiveABN8 =
                useRemoteUiABN8 ? remoteVisualActiveABN8 : firechk;

            var displayAnimationIndexABN8 =
                useRemoteUiABN8
                    ? remoteAnimationIndexABN8
                    : AnimationIndex;

            if (displayVisualActiveABN8 && !beam.WarningActive)
                return SharedBeamController.ChargeMark(displayAnimationIndexABN8);

            // Keep the existing firing frames, but choose their side from the authoritative direction.
            if (displayVisualActiveABN8 && beam.WarningActive)
            {
                if (displayAnimationIndexABN8 < 6 || displayAnimationIndexABN8 >= 14)
                    return beam.VisualMark;
                displayAnimationIndexABN8 = 6 + (displayAnimationIndexABN8 - 6) % 4 + (beam.Direction > 0 ? 4 : 0);
            }
            if (!displayVisualActiveABN8 ||
                displayAnimationIndexABN8 == -1 ||
                nyaonyaoAnimation.Count <= displayAnimationIndexABN8)
            {
                return "";
            }

            // REAVER_REMOTE_VANILLA_SAFE_CHARGE_AND_BEAM_AB_V4:
            // Vanillaリーヴァー本人には小型チャージ光点と小型ビームだけを送る。
            // Host local / Remote MOD / ビームゲームロジックは変更しない。
            var useRemoteVanillaAnimationABV4 =
                AmongUsClient.Instance.AmHost &&
                !Player.AmOwner &&
                !Player.IsModClient();

            if (useRemoteVanillaAnimationABV4)
            {
                if (displayAnimationIndexABN8 < 6) return SharedBeamController.ChargeMark(displayAnimationIndexABN8);

                if (reaverVanillaSafeBeamAnimationABV2.TryGetValue(
                    displayAnimationIndexABN8,
                    out var vanillaSafeBeamFrameABV2))
                {
                    return vanillaSafeBeamFrameABV2;
                }
            }

            return nyaonyaoAnimation[displayAnimationIndexABN8];
        }

        private class ReaverFire
        {
            public byte ReaverID;
            public bool Fired;
            public Vector3 BeamPosition;
        }
    }
}