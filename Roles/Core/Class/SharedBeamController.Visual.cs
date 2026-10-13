using System;
using System.Collections.Generic;
using System.Linq;
using Hazel;
using UnityEngine;

namespace TownOfHostForE.Roles.Core.Class;

internal sealed partial class SharedBeamController
{
    private int VisualStep = -1;
    private string lastVanillaMark = "";
    private readonly Dictionary<int, (bool Warning, Vector3 Position, bool Active, int Step, int Direction)> viewerStates = new();

    internal static string ChargeMark(int step) =>
        $"\n<color=#FF8C00><size={100 + Math.Clamp(step, 0, 5) * 40}%>●</size></color>";

    internal string VisualMark =>
        Owner == null || Owner.Data == null || Owner.Data.Disconnected || !Owner.IsAlive() ||
        !GameStates.IsInTask || GameStates.IsMeeting || !Active ? "" :
        WarningActive ? (Direction == 0 ? "" : Utils.ColorString(SharedWarningColor, Direction < 0 ? "\n◀━━━●" : "\n●━━━▶")) :
        ChargeMark(VisualStep);

    // Existing CustomRoleSync kind 0, with display-only fields appended. Never broadcast to Vanilla.
    internal void SyncVisual(int animation)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        VisualStep = Active && !WarningActive ? Math.Clamp(animation, 0, 5) : -1;
        var state = (WarningActive, WarningPosition, Active, VisualStep, Direction);
        if (PlayerControl.LocalPlayer != null && Main.AllPlayerControls != null)
        {
            foreach (var viewer in Main.AllPlayerControls)
            {
                if (viewer == null || viewer.Data == null || viewer.Data.Disconnected || viewer.AmOwner ||
                    !viewer.IsModClient() || !viewer.TryGetClientId(out var clientId) || clientId < 0) continue;
                if (viewerStates.TryGetValue(clientId, out var previous) && previous == state) continue;
                // An unused/inactive beam has nothing to publish to a new viewer.
                if (!viewerStates.ContainsKey(clientId) && !Active && !WarningActive) continue;
                var writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId,
                    (byte)CustomRPC.CustomRoleSync, SendOption.Reliable, clientId);
                writer.Write(Owner.PlayerId);
                writer.Write((byte)0);
                writer.Write(WarningActive);
                writer.Write(WarningPosition.x);
                writer.Write(WarningPosition.y);
                writer.Write(WarningPosition.z);
                writer.Write(Active);
                writer.Write(VisualStep);
                writer.Write(Direction);
                AmongUsClient.Instance.FinishRpcImmediately(writer);
                viewerStates[clientId] = state;
            }
        }
        var mark = VisualMark;
        if (lastVanillaMark == mark) return;
        lastVanillaMark = mark;
        // Force the target-name loop for ordinary crew viewers; keep the existing per-viewer name cache.
        Utils.NotifyRoles(ForceLoop: true);
    }

    internal void ReceiveVisual(MessageReader reader)
    {
        var warning = reader.ReadBoolean();
        var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        // Older compatible hosts send only the original warning/position payload.
        var hasVisual = reader.BytesRemaining >= 9;
        var active = hasVisual ? reader.ReadBoolean() : warning;
        var step = hasVisual ? reader.ReadInt32() : -1;
        var direction = hasVisual ? reader.ReadInt32() : 0;
        if (!GameStates.IsInTask || GameStates.IsMeeting || Owner.Data == null ||
            Owner.Data.Disconnected || !Owner.IsAlive())
        {
            ResetActive();
            RemoveArrows();
            return;
        }
        Active = active;
        VisualStep = step;
        Direction = direction;
        ApplyWarningState(warning, position);
        var viewer = PlayerControl.LocalPlayer;
        if (viewer == null || viewer.PlayerId == Owner.PlayerId) return;
        if (warning) TargetArrow.AddSourcePosition(viewer.PlayerId, Owner.PlayerId, position);
        else TargetArrow.RemoveSourcePosition(viewer.PlayerId, Owner.PlayerId);
    }

    internal static bool CleanupAll()
    {
        var hadVisual = false;
        foreach (var source in CustomRoleManager.AllActiveRoles.Values.OfType<ISharedBeamPetAbility>().ToArray())
        {
            hadVisual |= source.IsBeamPetAbilityActive || source.BeamWarningActive;
            source.ResetBeamVisuals();
        }
        return hadVisual;
    }

    internal static void CleanupPlayer(byte playerId)
    {
        TargetArrow.RemoveSourceViewer(playerId);
        foreach (var source in CustomRoleManager.AllActiveRoles.Values.OfType<ISharedBeamPetAbility>().ToArray())
            if (source.BeamWarningOwner != null && source.BeamWarningOwner.PlayerId == playerId)
                source.ResetBeamVisuals();
    }
}
