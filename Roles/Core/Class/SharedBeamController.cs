using System;
using InnerNet;
using System.Linq;
using UnityEngine;

namespace TownOfHostForE.Roles.Core.Class
{
    public interface ISharedBeamWarningSource
    {
        PlayerControl BeamWarningOwner { get; }
        bool BeamWarningActive { get; }
        Vector3 BeamWarningPosition { get; }
    }

    public interface ISharedBeamPetAbility : ISharedBeamWarningSource
    {
        bool IsBeamPetAbilityActive { get; }
        string BeamVisualMark { get; }
        void ResetBeamVisuals();
    }

    // Per-owner runtime; role presentation and wire compatibility remain with the caller.
    internal sealed partial class SharedBeamController
    {
        private readonly PlayerControl Owner;
        private const float BeamRadius = 3f;
        private const float BeamHalfHeight = 1.0f;
        internal bool Active;
        internal Vector3 Position = Vector3.zero;
        internal int Direction;
        internal int ChargeCount = -1;

        private bool WarningActiveState = false;
        private Vector3 WarningPositionState = Vector3.zero;

        internal bool WarningActive => WarningActiveState;
        internal Vector3 WarningPosition => WarningPositionState;

        internal SharedBeamController(PlayerControl owner)
        {
            Owner = owner;
        }

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

        internal void TrackDirection()
        {
            var currentPosition = Owner.transform.position;

            if (!Active)
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

        internal void ResetDirection(bool resetHistory)
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
                LastDirectionPositionABN8 = Owner.transform.position;
                HasLastDirectionPositionABN8 = true;
            }

            ActivationFallbackDirectionABN8 =
                LastHorizontalDirectionABN8;
        }

        internal void BeginDirectionSelection()
        {
            Active = true;
            ActivationFallbackDirectionABN8 =
                LastHorizontalDirectionABN8;

            DirectionBaselineABN8 = Owner.transform.position;

            var nowABN8 = Time.realtimeSinceStartup;
            DirectionSettleUntilABN8 =
                nowABN8 + DirectionSettleSecondsABN8;
            DirectionDeadlineABN8 =
                nowABN8 + DirectionDeadlineSecondsABN8;
            DirectionCandidateABN8 = 0;
            DirectionCandidateSamplesABN8 = 0;
            LockedDirectionABN8 = 0;


        }

        internal void FireBeam(Action reset, Action markFired, Action<Vector3> positionChanged, Action sync)
        {
            //撫でたかチェック
            if (!Active) return;

            //ビーム座標が初期値なら通さない()
            if (Position == Vector3.zero) return;

            //カウント中は通さない。
            if (ChargeCount > 0)
            {
                ChargeCount--;
                // NYAOHA_FIRECOUNT_NOTIFY_TARGET_AB_V1:
                // Playerを第1引数へ渡すとisForMeetingとしてbool評価され、全Vanilla viewer更新になる。
                // 他のクサネコ更新経路と同様に能力所有者だけを明示指定する。
                Utils.NotifyRoles(SpecifySeer: Owner);
                return;
            }
            else if (ChargeCount == 0)
            {
                var nowABN8 = Time.realtimeSinceStartup;

                // 入力が未確定なら短い選択猶予だけ待ち、期限後は発動前方向へ戻す。
                if (LockedDirectionABN8 == 0 &&
                    nowABN8 < DirectionDeadlineABN8)
                {
                    return;
                }

                ChargeCount = -1;
                Position = Owner.transform.position;

                var usedFallbackABN8 =
                    LockedDirectionABN8 == 0;

                Direction =
                    usedFallbackABN8
                        ? ActivationFallbackDirectionABN8
                        : LockedDirectionABN8;

                if (Direction == 0)
                {
                    Direction = -1;
                    usedFallbackABN8 = true;
                }

                LastHorizontalDirectionABN8 = Direction;

                var selectionDeltaXABN8 =
                    Owner.transform.position.x -
                    DirectionBaselineABN8.x;


                WarningActiveState = true;
                WarningPositionState = Position;

                markFired();
                sync();

                Utils.NotifyRoles(SpecifySeer: Owner);
                return;
            }

            //3ずつ増やす
            Position.x += Direction * 3;
            if (Position.x <= -50 || Position.x >= 50)
            {
                reset();
                return;
            }

            WarningPositionState = Position;

            // NYAOHA_GHOST_BEAM_ARROW_AB_V1
            // 既存の生存者用ビーム処理・キル判定には触れず、死亡viewer用の表示データだけを追加する。
            foreach (var ghostViewerAB1 in Main.AllPlayerControls)
            {
                if (ghostViewerAB1 == null ||
                    ghostViewerAB1.Data == null ||
                    ghostViewerAB1.Data.Disconnected ||
                    ghostViewerAB1.IsAlive() ||
                    ghostViewerAB1.PlayerId == Owner.PlayerId)
                {
                    continue;
                }

                TargetArrow.AddSourcePosition(
                    ghostViewerAB1.PlayerId,
                    Owner.PlayerId,
                    Position);
                positionChanged(Position);
            }
            // Front-to-back along the beam; a reflected beam must not reach players beyond the barrier.
            foreach (var pc in Main.AllAlivePlayerControls.OrderBy(pc => Direction * (pc.transform.position.x - Owner.transform.position.x)).ThenBy(pc => pc.PlayerId).ToArray())
            {
                if (pc.PlayerId == Owner.PlayerId) continue;
                if (pc.Data == null || pc.Data.Disconnected || !pc.IsAlive()) continue;

                TargetArrow.AddSourcePosition(pc.PlayerId, Owner.PlayerId, Position);
                positionChanged(Position);

                var dis = Vector2.Distance(Position, pc.transform.position);
                if (Position == Vector3.zero ||
                    Mathf.Abs(Position.y - pc.transform.position.y) > BeamHalfHeight ||
                    dis > BeamRadius) continue;

                var beamOwnerWasAliveBeforeReflection =
                    Owner.Data != null && !Owner.Data.Disconnected && Owner.IsAlive();
                if (AegisReflectionGuard.TryReflect(Owner, pc, "Beam"))
                {
                    var reflectedOwnerState = PlayerState.GetByPlayerId(Owner.PlayerId);
                    if (beamOwnerWasAliveBeforeReflection &&
                        Owner.Data != null && !Owner.Data.Disconnected && !Owner.IsAlive() &&
                        reflectedOwnerState.DeathReason == CustomDeathReason.Reflection &&
                        reflectedOwnerState.RealKiller.Item2 == pc.PlayerId)
                    {
                        Logger.Info($"Stage=SOUND_TRIGGER | AegisId={pc.PlayerId} | AttackerId={Owner.PlayerId} | BeamOwnerRole={Owner.GetCustomRole()}",
                            "AEGIS_BEAM_REFLECTION_GUARD_SOUND_TRIGGER_V2");
                        BroadcastAegisBeamReflectionGuardSoundAbV1(Owner, pc);
                    }
                    Position = pc.transform.position;
                    positionChanged(Position);
                    Logger.Info($"Stage=BEAM_STOP | AegisId={pc.PlayerId} | AttackerId={Owner.PlayerId} | BeamOwnerRole={Owner.GetCustomRole()}", "AEGIS_REFLECTION_V1");
                    reset();
                    return;
                }
                ResolveHit(Owner, pc);
            }
            sync();
            Utils.NotifyRoles(SpecifySeer: Owner);
        }

        internal void ApplyWarningState(bool active, Vector3 position)
        {
            WarningActiveState = active;
            WarningPositionState = active ? position : Vector3.zero;
        }

        internal void ResetActive()
        {
            Position = Vector3.zero;
            Active = false;
            Direction = 0;
            ChargeCount = -1;
            VisualStep = -1;
            WarningActiveState = false;
            WarningPositionState = Vector3.zero;
        }

        internal void RemoveArrows()
        {
            TargetArrow.RemoveSource(Owner.PlayerId);
        }

        private static readonly string[] SharedWarningArrows =
        {
            "↑",
            "↗",
            "→",
            "↘",
            "↓",
            "↙",
            "←",
            "↖",
            "・"
        };

        private static readonly Color SharedWarningColor =
            new Color32(255, 140, 0, 255);

        internal static string GetWarningMark(
            PlayerControl seer,
            PlayerControl seen,
            bool isForMeeting = false)
        {
            if (seer == null) return "";

            seen ??= seer;

            if (isForMeeting || !GameStates.IsInTask || GameStates.IsMeeting) return "";
            if (seer.PlayerId != seen.PlayerId)
                return seen.GetRoleClass() is ISharedBeamPetAbility beamSource
                    ? beamSource.BeamVisualMark : "";

            var arrows = "";
            var nearestDistance = float.MaxValue;
            var sourceCount = 0;

            foreach (var role in
                TownOfHostForE.Roles.Core.CustomRoleManager.AllActiveRoles.Values)
            {
                if (role is not ISharedBeamWarningSource source)
                    continue;

                var owner = source.BeamWarningOwner;

                if (owner == null ||
                    owner.Data == null ||
                    owner.Data.Disconnected ||
                    !owner.IsAlive() ||
                    owner.PlayerId == seer.PlayerId ||
                    !source.BeamWarningActive)
                {
                    continue;
                }

                var beamPosition = source.BeamWarningPosition;

                if (beamPosition == Vector3.zero)
                    continue;

                arrows += GetSharedWarningArrow(
                    seer.transform.position,
                    beamPosition);

                var distance = Vector2.Distance(
                    beamPosition,
                    seer.transform.position);

                if (distance < nearestDistance)
                    nearestDistance = distance;

                sourceCount++;
            }

            if (sourceCount <= 0) return "";

            var targetSet = $"ｺﾞ{arrows}ｺﾞ";

            if (nearestDistance < 4f)
            {
                targetSet =
                    $"<size=400%>ｺﾞｺﾞｺﾞ{targetSet}ｺﾞｺﾞｺﾞ</size>";
            }
            else if (nearestDistance < 9f)
            {
                targetSet =
                    $"<size=300%>ｺﾞｺﾞ{targetSet}ｺﾞｺﾞ</size>";
            }
            else if (nearestDistance < 15f)
            {
                targetSet =
                    $"<size=200%>ｺﾞ{targetSet}ｺﾞ</size>";
            }

            return Utils.ColorString(
                SharedWarningColor,
                $"\n{targetSet}");
        }

        private static string GetSharedWarningArrow(
            Vector3 viewerPosition,
            Vector3 beamPosition)
        {
            var dir = beamPosition - viewerPosition;
            int index;

            if (dir.magnitude < 2)
            {
                index = 8;
            }
            else
            {
                var angle =
                    Vector3.SignedAngle(
                        Vector3.down,
                        dir,
                        Vector3.back)
                    + 180
                    + 22.5f;

                index = ((int)(angle / 45f)) % 8;
            }

            return SharedWarningArrows[index];
        }

        // Single hit-resolution boundary for future interception. No team exclusions.
        private static void ResolveHit(PlayerControl owner, PlayerControl target)
        {
            var ownerWasAliveBeforeHit =
                owner != null && owner.Data != null &&
                !owner.Data.Disconnected && owner.IsAlive();
            var aegisBarrierActiveBeforeHit =
                TownOfHostForE.Roles.Core.Class.AegisReflectionGuard.IsBarrierActive(target);

            var before = PlayerState.GetByPlayerId(target.PlayerId);
            Logger.Info($"Stage=HIT | OwnerId={owner!.PlayerId} | OwnerRole={owner!.GetCustomRole()} | TargetId={target.PlayerId} | TargetRole={target.GetCustomRole()} | " +
                $"DeathReasonBefore={before.DeathReason} | RealKillerBefore={before.RealKiller.Item2}", "SHARED_BEAM_ATTRIBUTION_V1");
            PlayerState.GetByPlayerId(target.PlayerId).DeathReason = CustomDeathReason.Beam;
            target.SetRealKiller(owner);
            TownOfHostForE.Roles.Core.Class.AegisReflectionGuard.RunAttack(owner, target, () => target.RpcMurderPlayer(target));

            var attackerAfter = PlayerState.GetByPlayerId(owner.PlayerId);
            var beamReflectionKillSucceeded =
                ownerWasAliveBeforeHit &&
                aegisBarrierActiveBeforeHit &&
                owner.Data != null &&
                !owner.Data.Disconnected &&
                !owner.IsAlive() &&
                target.Data != null &&
                !target.Data.Disconnected &&
                target.IsAlive() &&
                target.GetCustomRole() == CustomRoles.Aegis &&
                attackerAfter.DeathReason == CustomDeathReason.Reflection &&
                attackerAfter.RealKiller.Item2 == target.PlayerId;

            if (beamReflectionKillSucceeded)
            {
                BroadcastAegisBeamReflectionGuardSoundAbV1(owner, target);
            }

            var after = PlayerState.GetByPlayerId(target.PlayerId);
            Logger.Info($"Stage=POST_HIT | TargetId={target.PlayerId} | FinalDeathReason={after.DeathReason} | FinalRealKillerId={after.RealKiller.Item2}", "SHARED_BEAM_ATTRIBUTION_V1");
        }

        // AEGIS_BEAM_REFLECTION_GUARD_SOUND_AB_V1
        // AEGIS_REFLECTION_SOUND_TARGETED_V6190418
        // Host plays the native Guardian protect sound locally.
        // Every connected remote viewer receives its own vanilla FailedProtected
        // wire-only RPC. Dead/ghost viewers are intentionally included.
        // No authoritative MurderPlayer call is made for the sound event.
        private static void BroadcastAegisBeamReflectionGuardSoundAbV1(
            PlayerControl attacker,
            PlayerControl aegis)
        {
            var client = AmongUsClient.Instance;
            if (client == null || !client.AmHost || aegis == null)
                return;

            var localSoundPlayed = false;
            var remoteConnected = 0;
            var remoteWireSent = 0;
            var remoteGhostRecipients = 0;

            var roleManager = DestroyableSingleton<RoleManager>.Instance;
            var protectAnim = roleManager == null ? null : roleManager.protectAnim;
            if (protectAnim != null)
            {
                protectAnim.PlaySound();
                localSoundPlayed = true;
            }

            foreach (var viewer in Main.AllPlayerControls)
            {
                if (viewer == null || viewer.Data == null ||
                    viewer.Data.Disconnected || viewer.AmOwner)
                {
                    continue;
                }

                remoteConnected++;
                var viewerClientId = viewer.GetClientId();
                var viewerDead = !viewer.IsAlive();
                if (viewerDead) remoteGhostRecipients++;

                if (viewerClientId < 0)
                {
                    Logger.Warn(
                        $"Stage=SOUND_RECIPIENT_SKIPPED | AegisId={aegis.PlayerId} | " +
                        $"RecipientPlayerId={viewer.PlayerId} | RecipientClientId={viewerClientId} | RecipientDead={viewerDead}",
                        "AEGIS_BEAM_REFLECTION_GUARD_SOUND_AB_V1");
                    continue;
                }

                try
                {
                    // Vanilla-compatible wire-only failed protection result.
                    // Source/target remain the living Aegis; only the recipient varies.
                    var writer = client.StartRpcImmediately(
                        aegis.NetId,
                        (byte)RpcCalls.MurderPlayer,
                        Hazel.SendOption.Reliable,
                        viewerClientId);
                    writer.WriteNetObject(aegis);
                    writer.Write((int)MurderResultFlags.FailedProtected);
                    client.FinishRpcImmediately(writer);
                    remoteWireSent++;

                    Logger.Info(
                        $"Stage=SOUND_RECIPIENT | AegisId={aegis.PlayerId} | AttackerId={attacker?.PlayerId.ToString() ?? "null"} | " +
                        $"RecipientPlayerId={viewer.PlayerId} | RecipientClientId={viewerClientId} | " +
                        $"RecipientDead={viewerDead} | WireSent=True",
                        "AEGIS_BEAM_REFLECTION_GUARD_SOUND_AB_V1");
                }
                catch (Exception ex)
                {
                    Logger.Error(
                        $"Stage=SOUND_RECIPIENT_ERROR | AegisId={aegis.PlayerId} | " +
                        $"RecipientPlayerId={viewer.PlayerId} | RecipientClientId={viewerClientId} | " +
                        $"RecipientDead={viewerDead} | Error={ex}",
                        "AEGIS_BEAM_REFLECTION_GUARD_SOUND_AB_V1",
                        false);
                }
            }

            Logger.Info(
                $"Stage=SOUND_SENT | AegisId={aegis.PlayerId} | AttackerId={attacker?.PlayerId.ToString() ?? "null"} | " +
                $"LocalSound={localSoundPlayed} | RemoteConnected={remoteConnected} | " +
                $"RemoteWireSent={remoteWireSent} | RemoteGhostRecipients={remoteGhostRecipients}",
                "AEGIS_BEAM_REFLECTION_GUARD_SOUND_AB_V1");
        }
    }
}
