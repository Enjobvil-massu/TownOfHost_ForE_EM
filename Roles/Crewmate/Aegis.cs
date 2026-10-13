using AmongUs.GameOptions;
using Hazel;
using InnerNet;
using UnityEngine;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Class;
using TownOfHostForE.Roles.Neutral;

namespace TownOfHostForE.Roles.Crewmate;

public sealed class Aegis : RoleBase
{
    public static readonly SimpleRoleInfo RoleInfo = SimpleRoleInfo.Create(
        typeof(Aegis), player => new Aegis(player), CustomRoles.Aegis,
        () => RoleTypes.Crewmate, CustomRoleTypes.Crewmate, 41700,
        SetupOptionItem, "イージス", "#f8cd46");

    private static OptionItem Duration = null!;
    private static OptionItem Cooldown = null!;
    private static OptionItem Uses = null!;
    private enum OptionName { AegisBarrierDuration, AegisBarrierCooldown, AegisBarrierUses }
    private int remainingUses;
    private bool active;
    private bool taskStarted;
    private float endsAt;
    private float readyAt;
    private float nextPositionCheck;
    private float nextProgressUpdate;
    private string lastProgressText = "";
    private bool displayEnded;
    private Vector3 lockedPosition;
    private RoleEffectAnimation? localVisual;
    private bool visualRunning;
    private float visualClipLength;
    private float visualRefreshInterval;
    private float nextVisualPulse;
    private int visualPulseIndex;
    private string visualMode = "";

    public Aegis(PlayerControl player) : base(RoleInfo, player)
    {
        remainingUses = Uses.GetInt();
        readyAt = Time.realtimeSinceStartup + Cooldown.GetFloat();
    }

    internal bool IsBarrierActive => active && Time.realtimeSinceStartup < endsAt &&
        GameStates.IsInTask && Player != null && Player.Data != null &&
        !Player.Data.Disconnected && Player.IsAlive();
    internal float VisualDuration => Duration.GetFloat();
    private static void SetupOptionItem()
    {
        Duration = IntegerOptionItem.Create(RoleInfo, 10, OptionName.AegisBarrierDuration, new(1, 30, 1), 5, false).SetValueFormat(OptionFormat.Seconds);
        Cooldown = IntegerOptionItem.Create(RoleInfo, 11, OptionName.AegisBarrierCooldown, new(1, 180, 1), 30, false).SetValueFormat(OptionFormat.Seconds);
        Uses = IntegerOptionItem.Create(RoleInfo, 12, OptionName.AegisBarrierUses, new(1, 15, 1), 3, false);
    }

    public override void OnTouchPet(PlayerControl player)
    {
        if (!AmongUsClient.Instance.AmHost || player != Player || !GameStates.IsInTask ||
            Options.IsFirstTurnMeetingActive || AmongUsClient.Instance.IsGameOver ||
            Player.Data == null || Player.Data.Disconnected || !Player.IsAlive()) return;

        if (remainingUses <= 0)
        {
            Logger.Info($"Stage=ACTIVATE_REJECTED | PlayerId={Player.PlayerId} | Reason=NoUses", "AEGIS_REFLECTION_V1");
            return;
        }
        if (active) return;
        if (!taskStarted || Time.realtimeSinceStartup < readyAt)
        {
            Logger.Info($"Stage=ACTIVATE_REJECTED | PlayerId={Player.PlayerId} | Reason=Cooldown | RemainingCooldown={RemainingCooldownSeconds}", "AEGIS_REFLECTION_V1");
            return;
        }
        if (!Player.moveable || Player.inVent || Player.walkingToVent || Player.onLadder ||
            Player.inMovingPlat || Renegade.IsAwakeningActive()) return;

        var before = remainingUses--;
        active = true;
        lockedPosition = Player.transform.position;
        endsAt = Time.realtimeSinceStartup + Duration.GetFloat();
        nextPositionCheck = 0f;
        Player.SyncSettings();
        ShowPrivateVisual();
        SyncState();
        RefreshOwnerProgress();
        Logger.Info($"Stage=ACTIVATE | PlayerId={Player.PlayerId} | UsesBefore={before} | UsesAfter={remainingUses} | Duration={Duration.GetInt()} | Cooldown={Cooldown.GetInt()}", "AEGIS_REFLECTION_V1");
    }

    public override bool OnCheckMurderAsTarget(MurderInfo info)
    {
        if (!AegisReflectionGuard.TryReflect(info.AttemptKiller, Player, "CheckMurder")) return true;
        info.DoKill = false;
        return false;
    }

    public override void OnFixedUpdate(PlayerControl player)
    {
        if (!AmongUsClient.Instance.AmHost)
        {
            if (Player.AmOwner) UpdatePrivateVisual();
            return;
        }
        if (Player.Data == null || Player.Data.Disconnected || !Player.IsAlive())
        {
            StopPrivateVisual(Player.Data == null || Player.Data.Disconnected ? "DISCONNECT" : "DEATH");
            Deactivate("DEATH_CLEANUP");
            RefreshOwnerProgress();
            return;
        }
        if (!GameStates.IsInTask)
        {
            StopPrivateVisual("MEETING");
            return;
        }
        var now = Time.realtimeSinceStartup;
        if (!taskStarted)
        {
            taskStarted = true;
            readyAt = now + Cooldown.GetFloat();
            SyncState();
        }
        if (active && now >= endsAt) Deactivate("DEACTIVATE");
        UpdatePrivateVisual();
        if (active && now >= nextPositionCheck)
        {
            nextPositionCheck = now + 0.2f;
            Player.rigidbody2D.velocity = Vector2.zero;
            if (Vector2.Distance(Player.transform.position, lockedPosition) > 0.03f)
                Player.RpcSnapToForced(lockedPosition, SendOption.None);
        }
        // Same 0.5-second display cadence and owner NotifyRoles path as Nyaoha.
        if (now >= nextProgressUpdate) RefreshOwnerProgress();
    }

    private void Deactivate(string stage)
    {
        var wasActive = active;
        active = false;
        endsAt = 0f;
        nextPositionCheck = 0f;
        lockedPosition = Vector3.zero;
        StopPrivateVisual(stage == "DEACTIVATE" ? "TIMEOUT" : stage == "MEETING_RESET" ? "MEETING" : "DEATH");
        if (!wasActive) return;
        readyAt = Time.realtimeSinceStartup + Cooldown.GetFloat();
        if (AmongUsClient.Instance.AmHost && Player != null && Player.Data != null)
        {
            // No saved-speed writeback: the normal option pipeline was never modified.
            if (!Player.Data.Disconnected) Player.SyncSettings();
            SyncState();
            RefreshOwnerProgress();
        }
        Logger.Info($"Stage={stage} | PlayerId={Player?.PlayerId}", "AEGIS_REFLECTION_V1");
    }

    public override void OnStartMeeting()
    {
        var wasActive = active;
        Deactivate("MEETING_RESET");
        if (!wasActive && AmongUsClient.Instance.AmHost) SyncState();
        RefreshOwnerProgress();
    }
    public override void AfterMeetingTasks()
    {
        if (!Player.IsAlive() || remainingUses <= 0)
        {
            RefreshOwnerProgress();
            return;
        }
        readyAt = Time.realtimeSinceStartup + Cooldown.GetFloat();
        if (AmongUsClient.Instance.AmHost) SyncState();
        RefreshOwnerProgress();
    }
    public override void OnMurderPlayerAsTarget(MurderInfo info) => EndProgressDisplay("DEATH");
    public override void OnDestroy() => EndProgressDisplay(
        Player == null || Player.Data == null || Player.Data.Disconnected ? "DISCONNECT" : "DESTROY");

    private void EndProgressDisplay(string reason)
    {
        StopPrivateVisual(reason);
        // OnMurder runs before PlayerState.SetDead; hide immediately, without changing timers.
        displayEnded = true;
        Deactivate("DEATH_CLEANUP");
        RefreshOwnerProgress();
    }

    private int RemainingCooldownSeconds =>
        Mathf.CeilToInt(Mathf.Max(0f, readyAt - Time.realtimeSinceStartup));

    public override string GetProgressText(bool comms = false)
    {
        if (displayEnded || !GameStates.IsInTask || Player == null || Player.Data == null ||
            Player.Data.Disconnected || !Player.IsAlive()) return "";
        var text = remainingUses <= 0
            ? "(残0) 使用不可"
            : IsBarrierActive
                ? $"バリア「{Mathf.Max(0f, endsAt - Time.realtimeSinceStartup):0.0}」({remainingUses})"
                : RemainingCooldownSeconds > 0
                    ? $"「{RemainingCooldownSeconds}」({remainingUses})"
                    : $"『GO!!』({remainingUses})";
        return Utils.ColorString(RoleInfo.RoleColor, text);
    }

    private void RefreshOwnerProgress()
    {
        if (!AmongUsClient.Instance.AmHost || Player == null || Player.Data == null) return;
        nextProgressUpdate = Time.realtimeSinceStartup + 0.5f;
        var text = GetProgressText();
        if (text == lastProgressText) return;
        lastProgressText = text;
        if (!Player.Data.Disconnected) Utils.NotifyRoles(SpecifySeer: Player);
    }

    private const string VisualLog = "AEGIS_PRIVATE_GUARD_HIT_VISUAL_V3";

    private void ShowPrivateVisual()
    {
        if (!IsBarrierActive || visualRunning) return;
        // Remote MOD owners create their own prefab from the existing state RPC.
        if (!Player.AmOwner && (!AmongUsClient.Instance.AmHost || Player.IsModClient())) return;
        if (!Player.AmOwner && (!Player.TryGetClientId(out var clientId) || clientId < 0)) return;

        var prefab = DestroyableSingleton<RoleManager>.Instance.protectAnim;
        var clip = prefab != null ? prefab.Clip : null;
        visualClipLength = clip != null ? clip.length : 0f;
        visualRefreshInterval = float.IsFinite(visualClipLength) && visualClipLength > 0.1f
            ? Mathf.Max(0.1f, visualClipLength - 0.05f) : 0f;
        visualMode = Player.AmOwner
            ? (AmongUsClient.Instance.AmHost ? "HOST_LOCAL_PROTECT_ANIM" : "REMOTE_MOD_PROTECT_ANIM")
            : "REMOTE_VANILLA_FAILED_PROTECTED";
        visualPulseIndex = 0;
        visualRunning = true;
        Logger.Info($"Stage=START | PlayerId={Player.PlayerId} | ClientId={Player.GetClientId()} | Mode={visualMode} | " +
            $"ClipLength={visualClipLength} | RefreshInterval={visualRefreshInterval} | Duration={Mathf.Max(0f, endsAt - Time.realtimeSinceStartup)} | Broadcast=False", VisualLog);
        if (visualRefreshInterval <= 0f)
            Logger.Info($"Stage=CLIP_INVALID | PlayerId={Player.PlayerId} | ClipLength={visualClipLength} | InitialPulseOnly=True", VisualLog);
        PulsePrivateVisual();
    }

    private void UpdatePrivateVisual()
    {
        if (!visualRunning) return;
        if (Player == null || Player.Data == null || Player.Data.Disconnected)
        {
            StopPrivateVisual("DISCONNECT");
            return;
        }
        if (!Player.IsAlive() || Player.Data.IsDead)
        {
            StopPrivateVisual("DEATH");
            return;
        }
        if (!GameStates.IsInTask)
        {
            StopPrivateVisual("MEETING");
            return;
        }
        if (!active || Time.realtimeSinceStartup >= endsAt)
        {
            StopPrivateVisual("TIMEOUT");
            return;
        }
        if (visualRefreshInterval > 0f && Time.realtimeSinceStartup >= nextVisualPulse)
            PulsePrivateVisual();
    }

    private void PulsePrivateVisual()
    {
        if (!visualRunning || !IsBarrierActive) return;
        if (Player.AmOwner)
        {
            var prefab = DestroyableSingleton<RoleManager>.Instance.protectAnim;
            if (prefab != null)
            {
                if (localVisual != null) Object.Destroy(localVisual.gameObject);
                localVisual = Object.Instantiate(prefab, Player.transform);
                // Native default duration=0 plays the one-shot clip. No repeating sound.
                localVisual.Play(Player, null, false, RoleEffectAnimation.SoundType.None, 0f, true, 0f);
                localVisual.SetMaskLayerBasedOnWhoShouldSee(true);
            }
        }
        else
        {
            if (!AmongUsClient.Instance.AmHost || Player.IsModClient() ||
                !Player.TryGetClientId(out var clientId) || clientId < 0) return;
            // Wire-only failed result: NEVER invoke MurderPlayer on the authoritative host.
            var writer = AmongUsClient.Instance.StartRpcImmediately(Player.NetId,
                (byte)RpcCalls.MurderPlayer, SendOption.Reliable, clientId);
            writer.WriteNetObject(Player);
            writer.Write((int)MurderResultFlags.FailedProtected);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        visualPulseIndex++;
        nextVisualPulse = Time.realtimeSinceStartup + visualRefreshInterval;
        Logger.Info($"Stage=PULSE | PlayerId={Player.PlayerId} | Mode={visualMode} | PulseIndex={visualPulseIndex} | " +
            $"Remaining={Mathf.Max(0f, endsAt - Time.realtimeSinceStartup)}", VisualLog);
    }

    private void StopPrivateVisual(string reason)
    {
        var wasRunning = visualRunning;
        visualRunning = false;
        nextVisualPulse = 0f;
        visualRefreshInterval = 0f;
        if (localVisual != null) Object.Destroy(localVisual.gameObject);
        localVisual = null;
        if (wasRunning)
            Logger.Info($"Stage=STOP | PlayerId={Player?.PlayerId} | Mode={visualMode} | Reason={reason}", VisualLog);
    }

    private void SyncState()
    {
        if (!AmongUsClient.Instance.AmHost || Player.AmOwner || !Player.IsModClient() ||
            Player.Data.Disconnected || !Player.TryGetClientId(out var clientId)) return;
        var writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId,
            (byte)CustomRPC.CustomRoleSync, SendOption.Reliable, clientId);
        writer.Write(Player.PlayerId);
        writer.Write(remainingUses);
        writer.Write(active);
        writer.Write(Mathf.Max(0f, endsAt - Time.realtimeSinceStartup));
        writer.Write(Mathf.Max(0f, readyAt - Time.realtimeSinceStartup));
        writer.Write(lockedPosition.x);
        writer.Write(lockedPosition.y);
        writer.Write(lockedPosition.z);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }
    public override void ReceiveRPC(MessageReader reader)
    {
        var uses = reader.ReadInt32();
        var enabled = reader.ReadBoolean();
        var duration = reader.ReadSingle();
        var cooldown = reader.ReadSingle();
        var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        if (AmongUsClient.Instance.AmHost || !Player.AmOwner) return;
        remainingUses = uses;
        active = enabled;
        endsAt = Time.realtimeSinceStartup + duration;
        readyAt = Time.realtimeSinceStartup + cooldown;
        lockedPosition = position;
        if (IsBarrierActive)
        {
            if (!visualRunning) ShowPrivateVisual();
        }
        else
        {
            var reason = Player.Data == null || Player.Data.Disconnected ? "DISCONNECT"
                : !Player.IsAlive() || Player.Data.IsDead ? "DEATH"
                : !GameStates.IsInTask ? "MEETING" : "TIMEOUT";
            StopPrivateVisual(reason);
        }
    }
}
