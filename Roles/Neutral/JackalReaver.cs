using AmongUs.GameOptions;
using Hazel;
using UnityEngine;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Core.Class;
using TownOfHostForE.Roles.Core.Interfaces;

namespace TownOfHostForE.Roles.Neutral;

public sealed class JackalReaver : RoleBase, IKiller, ISchrodingerCatOwner, ISharedBeamPetAbility
{
    public static readonly SimpleRoleInfo RoleInfo = SimpleRoleInfo.Create(
        typeof(JackalReaver), player => new JackalReaver(player), CustomRoles.JackalReaver,
        () => RoleTypes.Impostor, CustomRoleTypes.Neutral, 61100, SetupOptionItem,
        "ジャッカルリーヴァー", "#00b4eb", true, countType: CountTypes.Jackal);
    private static OptionItem KillCooldown = null!;
    private static OptionItem BeamUses = null!;
    private static OptionItem BeamCooldown = null!;
    private static OptionItem CanVent = null!;
    private static OptionItem CanSabotage = null!;
    private static OptionItem ImpostorVision = null!;
    private enum OptionName { JackalReaverBeamUses, JackalReaverBeamCooldown }
    private readonly SharedBeamController beam;
    private int remainingUses;
    private int cooldown;
    private int halfSeconds;
    private float nextUpdate;
    private int animation = -1;
    private int lastUiCooldown = -1;
    private int lastUiAnimation = -2;
    private int lastUiUses = -1;

    public JackalReaver(PlayerControl player) : base(RoleInfo, player, () => HasTask.False)
    {
        beam = new SharedBeamController(player);
        remainingUses = BeamUses.GetInt();
        cooldown = BeamCooldown.GetInt();
        CustomRoleManager.MarkOthers.Add(SharedBeamController.GetWarningMark);
    }
    private static void SetupOptionItem()
    {
        KillCooldown = FloatOptionItem.Create(RoleInfo, 10, GeneralOption.KillCooldown, new(1f, 180f, 1f), 30f, false).SetValueFormat(OptionFormat.Seconds);
        BeamUses = IntegerOptionItem.Create(RoleInfo, 11, OptionName.JackalReaverBeamUses, new(0, 10, 1), 5, false);
        BeamCooldown = IntegerOptionItem.Create(RoleInfo, 12, OptionName.JackalReaverBeamCooldown, new(0, 60, 1), 30, false).SetValueFormat(OptionFormat.Seconds);
        CanVent = BooleanOptionItem.Create(RoleInfo, 13, GeneralOption.CanVent, true, false);
        CanSabotage = BooleanOptionItem.Create(RoleInfo, 14, GeneralOption.CanUseSabotage, false, false);
        ImpostorVision = BooleanOptionItem.Create(RoleInfo, 15, GeneralOption.ImpostorVision, true, false);
        Options.SetUpAddOnOptions(RoleInfo.ConfigId + 20, RoleInfo.RoleName, RoleInfo.Tab);
    }
    public float CalculateKillCooldown() => KillCooldown.GetFloat();
    public bool CanUseImpostorVentButton() => CanVent.GetBool();
    public bool CanUseSabotageButton() => CanSabotage.GetBool();
    public override void ApplyGameOptions(IGameOptions opt) => opt.SetVision(ImpostorVision.GetBool());
    public void ApplySchrodingerCatOptions(IGameOptions opt) => ApplyGameOptions(opt);
    public SchrodingerCat.TeamType SchrodingerCatChangeTo => SchrodingerCat.TeamType.Jackal;
    PlayerControl ISharedBeamWarningSource.BeamWarningOwner => Player;
    bool ISharedBeamWarningSource.BeamWarningActive => beam.WarningActive;
    Vector3 ISharedBeamWarningSource.BeamWarningPosition => beam.WarningPosition;
    bool ISharedBeamPetAbility.IsBeamPetAbilityActive => beam.Active;
    string ISharedBeamPetAbility.BeamVisualMark => beam.VisualMark;
    void ISharedBeamPetAbility.ResetBeamVisuals() => ResetBeam();

    public override void OnTouchPet(PlayerControl player)
    {
        if (!AmongUsClient.Instance.AmHost || !GameStates.IsInTask || !Player.IsAlive() ||
            Player.Data.Disconnected || remainingUses <= 0 || cooldown > 0 || beam.Active) return;
        beam.BeginDirectionSelection();
        beam.ChargeCount = 3;
        beam.Position = Player.transform.position;
        animation = 0;
        remainingUses--;
        cooldown = BeamCooldown.GetInt();
        SendOwnerUi();
        Utils.NotifyRoles(SpecifySeer: Player);
    }
    public override void OnFixedUpdate(PlayerControl player)
    {
        if (Player.Data == null || Player.Data.Disconnected || !Player.IsAlive()) { ResetBeam(); return; }
        if (!AmongUsClient.Instance.AmHost) return;
        beam.TrackDirection();
        if (Time.realtimeSinceStartup < nextUpdate) return;
        nextUpdate = Time.realtimeSinceStartup + 0.5f;
        if (cooldown > 0 && ++halfSeconds >= 2)
        {
            halfSeconds = 0;
            cooldown--;
            Utils.NotifyRoles(SpecifySeer: Player);
        }
        if (beam.Active) animation++;
        // Charge, direction convergence, hit geometry, warnings and reflection all have one implementation.
        beam.FireBeam(ResetBeam, () => { }, _ => { }, SendBeamState);
        SendOwnerUi();
    }
    private void ResetBeam()
    {
        if (!beam.Active && !beam.WarningActive && animation < 0) { beam.RemoveArrows(); return; }
        beam.ResetActive();
        beam.ResetDirection(false);
        beam.RemoveArrows();
        animation = -1;
        SendBeamState();
        SendOwnerUi();
        Utils.NotifyRoles();
    }
    public override void OnStartMeeting()
    {
        cooldown = BeamCooldown.GetInt();
        halfSeconds = 0;
        beam.ChargeCount = -1;
        ResetBeam();
        beam.ResetDirection(true);
    }
    public override void AfterMeetingTasks() => cooldown = BeamCooldown.GetInt();
    public override void OnDestroy() => ResetBeam();
    public override string GetProgressText(bool comms = false) =>
        remainingUses > 0
            ? Utils.ColorString(Color.yellow, (cooldown > 0 ? $"「{cooldown}」" : "『GO!!』") + $"({remainingUses})")
            : Utils.ColorString(Color.gray, "「もう力が出なそうだ...」");
    public override string OverrideSpecialText()
    {
        if (!GameStates.IsInTask || GameStates.IsMeeting || !Player.IsAlive() || !beam.Active || animation < 0) return "";
        if (!beam.WarningActive) return SharedBeamController.ChargeMark(animation);
        return beam.Direction < 0 ? "\n◀━━━●" : "\n●━━━▶";
    }
    private void SendBeamState() => beam.SyncVisual(animation);
    private void SendOwnerUi()
    {
        beam.SyncVisual(animation);
        if (!AmongUsClient.Instance.AmHost || Player.AmOwner || !Player.IsModClient() ||
            !Player.TryGetClientId(out var clientId)) return;
        if (lastUiCooldown == cooldown && lastUiAnimation == animation && lastUiUses == remainingUses) return;
        var writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId,
            (byte)CustomRPC.CustomRoleSync, SendOption.Reliable, clientId);
        writer.Write(Player.PlayerId);
        writer.Write((byte)1);
        writer.Write(remainingUses);
        writer.Write(cooldown);
        writer.Write(animation);
        writer.Write(beam.Active);
        writer.Write(beam.Direction);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
        lastUiCooldown = cooldown;
        lastUiAnimation = animation;
        lastUiUses = remainingUses;
    }
    public override void ReceiveRPC(MessageReader reader)
    {
        if (AmongUsClient.Instance.AmHost) return;
        if (reader.ReadByte() == 0)
        {
            beam.ReceiveVisual(reader);
        }
        else
        {
            remainingUses = reader.ReadInt32();
            cooldown = reader.ReadInt32();
            animation = reader.ReadInt32();
            beam.Active = reader.ReadBoolean();
            beam.Direction = reader.ReadInt32();
        }
    }
}
