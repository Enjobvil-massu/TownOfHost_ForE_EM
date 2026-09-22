using System.Collections.Generic;
using AmongUs.GameOptions;
using Hazel;
using UnityEngine;

using TownOfHostForE.Roles.Core.Interfaces;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Neutral;

namespace TownOfHostForE.Roles.Animals
{
    public sealed class Nyaoha : RoleBase, IKiller, ISchrodingerCatOwner
    {

        /// <summary>
        ///  20000:TOH4E役職
        ///   1000:陣営 1:crew 2:imp 3:Third 4:Animals
        ///    100:役職ID
        /// </summary>
        public static readonly SimpleRoleInfo RoleInfo =
            SimpleRoleInfo.Create(
                typeof(Nyaoha),
                player => new Nyaoha(player),
                CustomRoles.Nyaoha,
                () => RoleTypes.Impostor,
                CustomRoleTypes.Animals,
                24700,
                SetupOptionItem,
                "クサネコ",
                "#FF8C00",
                true,
                countType: CountTypes.Animals
            );
        public Nyaoha(PlayerControl player)
        : base(
            RoleInfo,
            player,
            () => HasTask.False
        )
        {
            KillCooldown = OptionKillCooldown.GetFloat();
            SolarBeamCount = OptionSolarBeamCount.GetInt();
            fireDelay = OptionFireDelay.GetInt();

            //他視点用のMarkメソッド登録
            CustomRoleManager.MarkOthers.Add(GetMarkOthers);

            UpdateTime = 0;
            NyaohaVector = 0;
            BeamPostion = Vector3.zero;
            NyaohaFireing = new ();
            firechk = false;
            nyaohaKawaiinePosition = new();
            fireCount = -1;
            AnimationIndex = -1;
            secTimer = 0;
        }
        public SchrodingerCat.TeamType SchrodingerCatChangeTo => SchrodingerCat.TeamType.Animals;

        private static OptionItem OptionKillCooldown;
        private static OptionItem OptionSolarBeamCount;
        private static OptionItem OptionFireDelay;
        private static float KillCooldown;
        private float BeamRadius = 3f;
        private int AnimationIndex = -1;

        //時間観測用
        private float UpdateTime;
        private int NyaohaVector = 0;

        // NYAOHA_DIRECTION_CONVERGENCE_PERMANENT
        // Pet直後の強制移動を捨て、全クライアントを同じホスト権限の方向選択へ統一する。
        private const float DirectionHistoryEpsilonABN8 = 0.05f;
        private const float DirectionSelectEpsilonABN8 = 0.12f;
        private const float DirectionSettleSecondsABN8 = 0.75f;
        private const float DirectionDeadlineSecondsABN8 = 2.50f;
        private Vector3 LastDirectionPositionABN8 = Vector3.zero;
        private bool HasLastDirectionPositionABN8 = false;
        private int LastHorizontalDirectionABN8 = -1;
        private int ActivationFallbackDirectionABN8 = -1;
        private Vector3 DirectionBaselineABN8 = Vector3.zero;
        private float DirectionSettleUntilABN8 = 0f;
        private float DirectionDeadlineABN8 = 0f;
        private int DirectionCandidateABN8 = 0;
        private int DirectionCandidateSamplesABN8 = 0;
        private int LockedDirectionABN8 = 0;

        // NYAOHA_OWNER_UI_SYNC_PERMANENT
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
        private Vector3 BeamPostion = new();
        private static List<NyaohaFire> NyaohaFireing = new();
        private bool firechk = false;
        private Vector3 nyaohaKawaiinePosition = new ();
        private int fireCount = -1;
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
        // NYAOHA_REMOTE_VANILLA_SAFE_BEAM_ANIMATION_AB_V2:
        // Vanilla本人だけ発射方向フレームを小さいSetName payloadへ置換する。
        // 充填フレーム0-5、ゲームロジック、Host/MOD表示は変更しない。
        private readonly Dictionary<int, string> nyaohaVanillaSafeBeamAnimationABV2 = new()
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
            NyaohaSolarBeamCount,
            NyaohaFireDelay
        }

        private static void SetupOptionItem()
        {
            OptionKillCooldown = FloatOptionItem.Create(RoleInfo, 10, GeneralOption.KillCooldown, new(2.5f, 180f, 0.5f), 30f, false)
                .SetUiStepMultiplier(2)
                .SetLegacySavedIndexMultiplier(5)
                .SetValueFormat(OptionFormat.Seconds);
            OptionSolarBeamCount = IntegerOptionItem.Create(RoleInfo, 11, OptionName.NyaohaSolarBeamCount, new(0, 10, 1), 1, false)
                .SetValueFormat(OptionFormat.None);
            OptionFireDelay = IntegerOptionItem.Create(RoleInfo, 12, OptionName.NyaohaFireDelay, new(0, 60, 5), 30, false)
                .SetValueFormat(OptionFormat.Seconds);
        }
        public override void Add()
        {
            var playerId = Player.PlayerId;
            var MyNyaoha = new NyaohaFire { NyaohaID = playerId, Fired = false};
            NyaohaFireing.Add(MyNyaoha);
        }
        public float CalculateKillCooldown() => KillCooldown;
        public override void ApplyGameOptions(IGameOptions opt) => opt.SetVision(false);
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
        public bool CanUseSabotageButton() => false;

        public override void AfterMeetingTasks()
        {
            fireDelay = OptionFireDelay.GetInt();
        }
        public void ApplySchrodingerCatOptions(IGameOptions option) => ApplyGameOptions(option);

        public override void OnFixedUpdate(PlayerControl player)
        {
            TrackDirectionSelectionABN8();

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

        private void TrackDirectionSelectionABN8()
        {
            var currentPosition = Player.transform.position;

            if (!firechk)
            {
                if (!HasLastDirectionPositionABN8)
                {
                    LastDirectionPositionABN8 = currentPosition;
                    HasLastDirectionPositionABN8 = true;
                    return;
                }

                var historyDeltaXABN8 =
                    currentPosition.x - LastDirectionPositionABN8.x;

                if (historyDeltaXABN8 > DirectionHistoryEpsilonABN8)
                {
                    LastHorizontalDirectionABN8 = 1;
                }
                else if (historyDeltaXABN8 < -DirectionHistoryEpsilonABN8)
                {
                    LastHorizontalDirectionABN8 = -1;
                }

                LastDirectionPositionABN8 = currentPosition;
                return;
            }

            if (LockedDirectionABN8 != 0) return;

            var nowABN8 = Time.realtimeSinceStartup;

            // Pet終了直後の自動補正・強制左移動は基準点へ吸収する。
            if (nowABN8 < DirectionSettleUntilABN8)
            {
                DirectionBaselineABN8 = currentPosition;
                DirectionCandidateABN8 = 0;
                DirectionCandidateSamplesABN8 = 0;
                return;
            }

            var selectDeltaXABN8 =
                currentPosition.x - DirectionBaselineABN8.x;

            var candidateABN8 =
                selectDeltaXABN8 > DirectionSelectEpsilonABN8
                    ? 1
                    : selectDeltaXABN8 < -DirectionSelectEpsilonABN8
                        ? -1
                        : 0;

            if (candidateABN8 == 0)
            {
                DirectionCandidateABN8 = 0;
                DirectionCandidateSamplesABN8 = 0;
                return;
            }

            if (DirectionCandidateABN8 == candidateABN8)
            {
                DirectionCandidateSamplesABN8++;
            }
            else
            {
                DirectionCandidateABN8 = candidateABN8;
                DirectionCandidateSamplesABN8 = 1;
            }

            // 一時的な座標揺れではなく、連続2物理フレームの同方向入力で確定する。
            if (DirectionCandidateSamplesABN8 < 2) return;

            LockedDirectionABN8 = candidateABN8;

        }

        private void ResetDirectionStateABN8(bool resetHistory)
        {
            DirectionBaselineABN8 = Vector3.zero;
            DirectionSettleUntilABN8 = 0f;
            DirectionDeadlineABN8 = 0f;
            DirectionCandidateABN8 = 0;
            DirectionCandidateSamplesABN8 = 0;
            LockedDirectionABN8 = 0;

            if (resetHistory)
            {
                LastDirectionPositionABN8 = Vector3.zero;
                HasLastDirectionPositionABN8 = false;
                LastHorizontalDirectionABN8 = -1;
            }
            else
            {
                LastDirectionPositionABN8 = Player.transform.position;
                HasLastDirectionPositionABN8 = true;
            }

            ActivationFallbackDirectionABN8 =
                LastHorizontalDirectionABN8;
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
            NyaohaVector = 0;
            nyaohaKawaiinePosition = Vector3.zero;

            // 発射・アニメーション・矢印を停止し、既存Beam状態を同期する。
            ResetNyaoha();
            ResetDirectionStateABN8(resetHistory: true);

        }

        public bool IsPetAbilityActive => firechk;

        public override void OnTouchPet(PlayerControl player)
        {
            if (SolarBeamCount <= 0) return;
            if (fireDelay > 0) return;
            if (firechk) return;

            firechk = true;
            ActivationFallbackDirectionABN8 =
                LastHorizontalDirectionABN8;

            nyaohaKawaiinePosition = Player.transform.position;
            DirectionBaselineABN8 = Player.transform.position;

            var nowABN8 = Time.realtimeSinceStartup;
            DirectionSettleUntilABN8 =
                nowABN8 + DirectionSettleSecondsABN8;
            DirectionDeadlineABN8 =
                nowABN8 + DirectionDeadlineSecondsABN8;
            DirectionCandidateABN8 = 0;
            DirectionCandidateSamplesABN8 = 0;
            LockedDirectionABN8 = 0;


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
            // NYAOHA_LAST_SHOT_ANIMATION_AB_V1:
            // 残弾チェックはOnTouchPet側で完了している。
            // 発射開始後はSolarBeamCountが0でも現在の1発の描画を最後まで進める。
            if (!firechk) return;

            if (AnimationIndex <= nyaonyaoAnimation.Count -1)
            {
                //右向きか
                if (NyaohaVector> 0 && AnimationIndex == 5)
                {
                    AnimationIndex = 10;
                }
                else if(NyaohaVector < 0 && AnimationIndex == 9)
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
            //撫でたかチェック
            if (!firechk) return;

            //ビーム座標が初期値なら通さない()
            if (BeamPostion == Vector3.zero) return;

            //カウント中は通さない。
            if (fireCount > 0)
            {
                fireCount--;
                // NYAOHA_FIRECOUNT_NOTIFY_TARGET_AB_V1:
                // Playerを第1引数へ渡すとisForMeetingとしてbool評価され、全Vanilla viewer更新になる。
                // 他のクサネコ更新経路と同様に能力所有者だけを明示指定する。
                Utils.NotifyRoles(SpecifySeer: Player);
                return;
            }
            else if (fireCount == 0)
            {
                var nowABN8 = Time.realtimeSinceStartup;

                // 入力が未確定なら短い選択猶予だけ待ち、期限後は発動前方向へ戻す。
                if (LockedDirectionABN8 == 0 &&
                    nowABN8 < DirectionDeadlineABN8)
                {
                    return;
                }

                fireCount = -1;
                BeamPostion = Player.transform.position;

                var usedFallbackABN8 =
                    LockedDirectionABN8 == 0;

                NyaohaVector =
                    usedFallbackABN8
                        ? ActivationFallbackDirectionABN8
                        : LockedDirectionABN8;

                if (NyaohaVector == 0)
                {
                    NyaohaVector = -1;
                    usedFallbackABN8 = true;
                }

                LastHorizontalDirectionABN8 = NyaohaVector;

                var selectionDeltaXABN8 =
                    Player.transform.position.x -
                    DirectionBaselineABN8.x;


                nyaohaCheck(Player.PlayerId,true);
                SendBeamStateRPC();

                Utils.NotifyRoles(SpecifySeer: Player);
                return;
            }

            //3ずつ増やす
            BeamPostion.x += NyaohaVector * 3;
            if (BeamPostion.x <= -50 || BeamPostion.x >= 50)
            {
                ResetNyaoha();
                return;
            }

            // NYAOHA_GHOST_BEAM_ARROW_AB_V1
            // 既存の生存者用ビーム処理・キル判定には触れず、死亡viewer用の表示データだけを追加する。
            foreach (var ghostViewerAB1 in Main.AllPlayerControls)
            {
                if (ghostViewerAB1 == null ||
                    ghostViewerAB1.Data == null ||
                    ghostViewerAB1.Data.Disconnected ||
                    ghostViewerAB1.IsAlive() ||
                    ghostViewerAB1.PlayerId == Player.PlayerId)
                {
                    continue;
                }

                TargetArrow.Add(
                    ghostViewerAB1.PlayerId,
                    Player.PlayerId,
                    BeamPostion);
                nyaohaCheckP(Player.PlayerId, BeamPostion);
            }
            foreach (var pc in Main.AllAlivePlayerControls)
            {
                if (pc.PlayerId == Player.PlayerId) continue;

                TargetArrow.Add(pc.PlayerId, Player.PlayerId, BeamPostion);
                nyaohaCheckP(Player.PlayerId,BeamPostion);

                var dis = Vector2.Distance(BeamPostion, pc.transform.position);
                if (BeamPostion == Vector3.zero || dis > BeamRadius) continue;

                PlayerState.GetByPlayerId(pc.PlayerId).DeathReason = CustomDeathReason.Beam;
                pc.SetRealKiller(Player);
                pc.RpcMurderPlayer(pc);
            }
            SendBeamStateRPC();
            Utils.NotifyRoles(SpecifySeer: Player);
        }

        private void ResetNyaoha()
        {
            BeamPostion = Vector3.zero;
            firechk = false;
            AnimationIndex = -1;

            ResetDirectionStateABN8(resetHistory: false);
            SetOwnerUiFallbackABN8();

            nyaohaCheck(Player.PlayerId, false);
            nyaohaCheckP(Player.PlayerId, BeamPostion);

            // NYAOHA_GHOST_BEAM_ARROW_AB_V1
            // 発射中に死亡したviewerを含め、幽霊向け矢印だけを対称に破棄する。
            foreach (var ghostViewerAB1 in Main.AllPlayerControls)
            {
                if (ghostViewerAB1 == null ||
                    ghostViewerAB1.Data == null ||
                    ghostViewerAB1.Data.Disconnected ||
                    ghostViewerAB1.IsAlive() ||
                    ghostViewerAB1.PlayerId == Player.PlayerId)
                {
                    continue;
                }

                TargetArrow.RemoveP(
                    ghostViewerAB1.PlayerId,
                    Player.PlayerId);
            }
            foreach (var pc in Main.AllAlivePlayerControls)
            {
                TargetArrow.RemoveP(pc.PlayerId, Player.PlayerId);
            }

            SendBeamStateRPC();
            SendOwnerUiStateRPCABN8(force: true);
            Utils.NotifyRoles();
        }

        // 同一MOD参加者向けのクサネコビーム表示状態を同期する。
        // バニラ参加者にはCustom RPCを送信しない。
        private void SendBeamStateRPC()
        {
            if (!AmongUsClient.Instance.AmHost) return;
            if (PlayerControl.LocalPlayer == null || Main.AllPlayerControls == null) return;

            foreach (var target in Main.AllPlayerControls)
            {
                if (target == null || target.AmOwner || !target.IsModClient()) continue;

                var clientId = target.GetClientId();
                if (clientId < 0) continue;

                var writer = AmongUsClient.Instance.StartRpcImmediately(
                    PlayerControl.LocalPlayer.NetId,
                    (byte)CustomRPC.CustomRoleSync,
                    SendOption.Reliable,
                    clientId);

                writer.Write(Player.PlayerId);
                writer.Write(SyncBeamStateABN8);
                writer.Write(firechk);
                writer.Write(BeamPostion.x);
                writer.Write(BeamPostion.y);
                writer.Write(BeamPostion.z);

                AmongUsClient.Instance.FinishRpcImmediately(writer);

            }
        }

        private void SendOwnerUiStateRPCABN8(bool force = false)
        {
            if (!AmongUsClient.Instance.AmHost) return;
            if (PlayerControl.LocalPlayer == null || Player == null) return;
            if (Player.AmOwner || !Player.IsModClient()) return;

            if (!force &&
                ownerUiSendInitializedABN8 &&
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
                var fired = reader.ReadBoolean();
                var beamPosition = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle());

                nyaohaCheck(Player.PlayerId, fired);
                nyaohaCheckP(Player.PlayerId, beamPosition);

                var localPlayer = PlayerControl.LocalPlayer;
                if (localPlayer != null && localPlayer.PlayerId != Player.PlayerId)
                {
                    if (fired)
                    {
                        TargetArrow.Add(
                            localPlayer.PlayerId,
                            Player.PlayerId,
                            beamPosition);
                    }
                    else
                    {
                        TargetArrow.RemoveP(
                            localPlayer.PlayerId,
                            Player.PlayerId);
                    }
                }

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

        private void nyaohaCheck(byte id,bool change)
        {
            foreach (var nyaoha in NyaohaFireing)
            {
                if (nyaoha.NyaohaID == id)
                {
                    nyaoha.Fired = change;
                    break;
                }
            }
        }
        private void nyaohaCheckP(byte id,Vector3 postion)
        {
            foreach (var nyaoha in NyaohaFireing)
            {
                if (nyaoha.NyaohaID == id)
                {
                    nyaoha.BeamPosition = postion;
                    break;
                }
            }
        }

        private static Vector3 SearchBeamPos(byte nyaohaId)
        {
            Vector3 pos = new();

            foreach (var nya in NyaohaFireing)
            {
                if (nya.NyaohaID == nyaohaId)
                {
                    pos = nya.BeamPosition;
                    break;
                }
            }

            return pos;
        }

        public static string GetMarkOthers(PlayerControl seer, PlayerControl seen, bool isForMeeting = false)
        {
            //seenが省略の場合seer
            seen ??= seer;
            //if (GameStates.IsMeeting) return "";
            if (isForMeeting) return "";

            if (seer.PlayerId != seen.PlayerId) return "";

            //ニャオハは自分のビームをみなくていい。
            var cRole = seer.GetCustomRole();
            if (cRole == CustomRoles.Nyaoha) return "";

            // NYAOHA_GHOST_BEAM_ARROW_AB_V1
            // 死亡viewerにも既存と同じビーム方向Markを表示する。

            List<byte> NyaohaKawaiine = new();
            //
            foreach (var nyaoha in NyaohaFireing)
            {
                if (nyaoha.Fired) NyaohaKawaiine.Add(nyaoha.NyaohaID);
            }

            //誰一人撃ってない
            if (NyaohaKawaiine.Count <= 0) return "";

            string targetSet = "";

            foreach (var nyaoha in NyaohaKawaiine)
            {
                var Nyaoha = Utils.GetPlayerById(nyaoha);

                // NYAOHA_DISCONNECTED_ARROW_GUARD_PERMANENT
                // 離脱済みクサネコはPlayerControlを取得できないため描画対象外にする。
                if (Nyaoha == null) continue;

                targetSet = TargetArrow.GetArrowsP(Nyaoha, seer.PlayerId);
                targetSet = $"ｺﾞ{targetSet}ｺﾞ";
                //サイズ変更
                var dis = Vector2.Distance(SearchBeamPos(nyaoha), seer.transform.position);
                if (dis < 4f)
                {
                    targetSet = $"<size=400%>ｺﾞｺﾞｺﾞ{targetSet}ｺﾞｺﾞｺﾞ</size>";
                }
                else if (dis < 9f)
                {
                    targetSet = $"<size=300%>ｺﾞｺﾞ{targetSet}ｺﾞｺﾞ</size>";
                }
                else if (dis < 15f)
                {
                    targetSet = $"<size=200%>ｺﾞ{targetSet}ｺﾞ</size>";
                }
            }

            return Utils.ColorString(RoleInfo.RoleColor, $"\n{targetSet}");
        }

        public override string OverrideSpecialText()
        {
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

            if (!displayVisualActiveABN8 ||
                displayAnimationIndexABN8 == -1 ||
                nyaonyaoAnimation.Count <= displayAnimationIndexABN8)
            {
                return "";
            }

            // NYAOHA_REMOTE_VANILLA_SAFE_CHARGE_AND_BEAM_AB_V4:
            // Vanillaクサネコ本人には小型チャージ光点と小型ビームだけを送る。
            // Host local / Remote MOD / ビームゲームロジックは変更しない。
            var useRemoteVanillaAnimationABV4 =
                AmongUsClient.Instance.AmHost &&
                !Player.AmOwner &&
                !Player.IsModClient();

            if (useRemoteVanillaAnimationABV4)
            {
                if (displayAnimationIndexABN8 < 6)
                {
                    return displayAnimationIndexABN8 switch
                    {
                        0 => "\n<color=#FF8C00><size=40%>●</size></color>",
                        1 => "\n<color=#FF8C00><size=60%>●</size></color>",
                        2 => "\n<color=#FF8C00><size=80%>●</size></color>",
                        3 => "\n<color=#FF8C00><size=100%>●</size></color>",
                        4 => "\n<color=#FF8C00><size=130%>●</size></color>",
                        5 => "\n<color=#FF8C00><size=160%>●</size></color>",
                        _ => ""
                    };
                }

                if (nyaohaVanillaSafeBeamAnimationABV2.TryGetValue(
                    displayAnimationIndexABN8,
                    out var vanillaSafeBeamFrameABV2))
                {
                    return vanillaSafeBeamFrameABV2;
                }
            }

            return nyaonyaoAnimation[displayAnimationIndexABN8];
        }

        private class NyaohaFire
        {
            public byte NyaohaID;
            public bool Fired;
            public Vector3 BeamPosition;
        }
    }
}