using System;

namespace TownOfHostForE;

// ROLEBUGTRACE_STAGE1A_PERMANENT
// ホスト側の共通イベント境界だけを記録する。
// FriendCode / PUID / ProductUserId 等の識別子は記録しない。
internal static class RoleBugTrace
{
    private static long sequence;
    private static int gameNumber;
    private static int meetingNumber;

    private static bool IsHost => AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;

    public static void GameStart()
    {
        if (!IsHost) return;

        sequence = 0;
        gameNumber++;
        meetingNumber = 0;

        foreach (var player in Main.AllPlayerControls)
        {
            if (player == null) continue;
            Write(
                "GameStart",
                player,
                null,
                "Snapshot",
                "GameStart",
                $"Alive={player.IsAlive()}");
        }
    }

    public static void MeetingStart()
    {
        if (!IsHost) return;

        meetingNumber++;
        Write("MeetingStart", null, null, "Started", "MeetingHud.Start", "-");
    }

    public static void KillAttempt(PlayerControl killer, PlayerControl target)
    {
        if (!IsHost) return;

        Write(
            "KillAttempt",
            killer,
            target,
            "Attempt",
            "CheckMurder",
            $"TargetAlive={target?.IsAlive()}");
    }

    public static void KillResult(PlayerControl killer, PlayerControl target, bool succeeded, string flags)
    {
        if (!IsHost) return;

        Write(
            "KillResult",
            killer,
            target,
            succeeded ? "Succeeded" : "Blocked",
            flags,
            $"TargetAliveBeforeApply={target?.IsAlive()}");
    }

    public static void Report(PlayerControl reporter, PlayerControl? target, bool emergency)
    {
        if (!IsHost) return;

        Write(
            "Report",
            reporter,
            target,
            "Accepted",
            emergency ? "EmergencyButton" : "DeadBody",
            "-");
    }

    public static void Task(PlayerControl player, bool vanillaProceed, int completed, int total)
    {
        if (!IsHost) return;

        Write(
            "Task",
            player,
            null,
            vanillaProceed ? "Proceed" : "Intercepted",
            "PlayerControl.CompleteTask",
            $"ObservedTasks={completed}/{total}");
    }

    public static void Pet(PlayerControl player, string source)
    {
        if (!IsHost) return;

        Write("Pet", player, null, "Dispatch", source, "-");
    }

    // ROLEBUGTRACE_PET_AFTER_PERMANENT
    // Pet能力本体の実行後状態を、既存Pet(Before)とは別イベントで記録する。
    public static void PetAfter(PlayerControl player, string source)
    {
        if (!IsHost) return;

        Write("PetAfter", player, null, "Applied", source, "-");
    }


    // ROLEBUGTRACE_STAGE1B_PERMANENT
    public static void Vote(byte voterId, byte voteFor, int numVotes, bool accepted, string reason)
    {
        if (!IsHost) return;

        var voter = Utils.GetPlayerById(voterId);
        var target = Utils.GetPlayerById(voteFor);
        Write(
            "Vote",
            voter,
            target,
            accepted ? "Accepted" : "Canceled",
            reason,
            $"VoteForId={voteFor};Votes={numVotes}");
    }

    public static void Exile(NetworkedPlayerInfo? exiled)
    {
        if (!IsHost) return;

        var target = exiled?.Object;
        Write(
            "Exile",
            null,
            target,
            target == null ? "None" : "Applied",
            "ExileController.WrapUp",
            target == null ? "ExiledId=-" : $"ExiledId={target.PlayerId}");
    }

    // ROLEBUGTRACE_EXECUTIONER_EXILE_AFTER_PERMANENT:
    // OnExileWrapUpによる役職状態更新後だけを中央dispatcherから記録する。
    // 役職ロジックは変更せず、ExecutionerのTargetExiled=False -> Trueを観測する。
    public static void ExileAfterRoleEffects(NetworkedPlayerInfo? exiled)
    {
        if (!IsHost) return;

        var target = exiled?.Object;
        if (target == null) return;

        foreach (var player in Main.AllPlayerControls)
        {
            if (TownOfHostForE.Roles.Core.CustomRoleManager.GetByPlayerId(player.PlayerId)
                is not TownOfHostForE.Roles.Neutral.Executioner executioner)
            {
                continue;
            }

            if (executioner.TargetId != target.PlayerId) continue;

            Write(
                "ExileAfter",
                player,
                target,
                "RoleEffectsApplied",
                "ExileController.WrapUp.AfterRoleEffects",
                $"ExiledId={target.PlayerId}");
        }
    }

    public static void MeetingEnd()
    {
        if (!IsHost) return;

        Write("MeetingEnd", null, null, "TaskPhase", "WrapUpFinalizer", "-");
    }

    public static void ShapeshiftAttempt(PlayerControl shapeshifter, PlayerControl? target)
    {
        if (!IsHost) return;

        Write(
            "ShapeshiftAttempt",
            shapeshifter,
            target,
            "ValidRequest",
            "CheckShapeshift",
            target == null ? "Target=-" : $"TargetId={target.PlayerId}");
    }

    public static void Shapeshift(PlayerControl shapeshifter, PlayerControl? target, bool shapeshifting)
    {
        if (!IsHost) return;

        Write(
            "Shapeshift",
            shapeshifter,
            target,
            shapeshifting ? "Applied" : "Reverted",
            "PlayerControl.Shapeshift",
            target == null ? "Target=-" : $"TargetId={target.PlayerId}");
    }

    // ROLEBUGTRACE_SHAPESHIFT_AFTER_PERMANENT
    // Shapeshift共通イベントの役職処理完了後状態を別イベントで観測する。
    // 既存Shapeshiftイベント・役職ロジック・同期順序は変更しない。
    public static void ShapeshiftAfter(PlayerControl shapeshifter, PlayerControl? target, bool shapeshifting)
    {
        if (!IsHost) return;

        Write(
            "ShapeshiftAfter",
            shapeshifter,
            target,
            shapeshifting ? "Applied" : "Reverted",
            "PlayerControl.Shapeshift.AfterRoleEffects",
            target == null ? "Target=-" : $"TargetId={target.PlayerId}");
    }

    public static void VentEnter(PlayerControl player, int ventId, bool accepted, string reason)
    {
        if (!IsHost) return;

        Write(
            "VentEnter",
            player,
            null,
            accepted ? "Accepted" : "BootScheduled",
            reason,
            $"VentId={ventId}");
    }

    public static void Sabotage(PlayerControl player, SystemTypes systemType, bool accepted, string reason)
    {
        if (!IsHost) return;

        Write(
            "Sabotage",
            player,
            null,
            accepted ? "Accepted" : "Blocked",
            reason,
            $"SystemType={systemType}");
    }

    public static void Winner(string action, CustomWinner before, CustomWinner after, CustomWinner requested)
    {
        if (!IsHost) return;

        Write(
            "Winner",
            null,
            null,
            action,
            $"Requested={requested}",
            $"Before={before};After={after}");
    }

    private static void Write(
        string eventName,
        PlayerControl? actor,
        PlayerControl? target,
        string result,
        string reason,
        string data)
    {
        if (!IsHost) return;

        var seq = ++sequence;
        var actorId = actor == null ? "-" : actor.PlayerId.ToString();
        var targetId = target == null ? "-" : target.PlayerId.ToString();

        data = AppendDirectRoleState(data, actor, target);

        var line =
            $"Seq={seq}" +
            $" | Game={gameNumber}" +
            $" | Meeting={meetingNumber}" +
            $" | Phase={Safe(GetPhase())}" +
            $" | Event={Safe(eventName)}" +
            $" | ActorId={actorId}" +
            $" | ActorName={Safe(GetName(actor))}" +
            $" | Role={Safe(GetRole(actor))}" +
            $" | SubRoles={Safe(GetSubRoles(actor))}" +
            $" | TargetId={targetId}" +
            $" | TargetName={Safe(GetName(target))}" +
            $" | TargetRole={Safe(GetRole(target))}" +
            $" | Result={Safe(result)}" +
            $" | Reason={Safe(reason)}" +
            $" | Data={Safe(data)}";

        Logger.Info(line, "RoleBugTrace");
    }

    // ROLEBUGTRACE_STAGE2_PHASEA_DIRECT_STATE_PERMANENT
    // Stage2 Phase-A: event-drivenの共通Write時だけ、公開runtime状態を中央取得する。
    // 役職ロジック・設定値・private状態には触れない。
    private static string AppendDirectRoleState(string data, PlayerControl? actor, PlayerControl? target)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(data) && data != "-") parts.Add(data);

        var actorState = GetDirectRoleState(actor);
        if (!string.IsNullOrEmpty(actorState)) parts.Add($"ActorState=[{actorState}]");

        if (target != null && (actor == null || target.PlayerId != actor.PlayerId))
        {
            var targetState = GetDirectRoleState(target);
            if (!string.IsNullOrEmpty(targetState)) parts.Add($"TargetState=[{targetState}]");
        }

        return parts.Count == 0 ? "-" : string.Join(";", parts);
    }

    private static string GetDirectRoleState(PlayerControl? player)
    {
        if (player == null) return "";

        var roleClass = TownOfHostForE.Roles.Core.CustomRoleManager.GetByPlayerId(player.PlayerId);
        if (roleClass == null) return "";

        return roleClass switch
        {
            TownOfHostForE.Roles.Neutral.DarkGameMaster role => $"Role=DarkGameMaster;CurrentKillCooldown={Invariant(role.CurrentKillCooldown)};IsDeathGameOwner={TownOfHostForE.Roles.Neutral.DarkGameMaster.IsDeathGameOwner};IsDeathGameTime={TownOfHostForE.Roles.Neutral.DarkGameMaster.IsDeathGameTime};DeathGameFesCount={role.RoleBugTraceDeathGameFesCount}",
            TownOfHostForE.Roles.Neutral.Oniichan role => $"Role=Oniichan;TargetId={role.TargetId};State={role.nowState}",
            TownOfHostForE.Roles.Neutral.Arsonist role => $"Role=Arsonist;CanKill={role.CanKill}",
            TownOfHostForE.Roles.Impostor.JapPup role => $"Role=JapPup;TargetId={role.TargetId};GlobalTarget={TownOfHostForE.Roles.Impostor.JapPup.Target};ShapeTarget={TownOfHostForE.Roles.Impostor.JapPup.shapeTarget};CanSetTarget={TownOfHostForE.Roles.Impostor.JapPup.CanSetTarget};CanKillFlag={TownOfHostForE.Roles.Impostor.JapPup.CanKillFlag};TargetKillTime={Invariant(TownOfHostForE.Roles.Impostor.JapPup.japPupTargetKillTime)}",
            TownOfHostForE.Roles.Impostor.BountyHunter role => $"Role=BountyHunter;TargetId={PlayerIdOrDash(role.Target)};ChangeTimer={Invariant(role.ChangeTimer)}",
            TownOfHostForE.Roles.Neutral.Executioner role => $"Role=Executioner;TargetId={role.TargetId};TargetExiled={role.RoleBugTraceTargetExiled}",
            TownOfHostForE.Roles.Crewmate.DogSheriff role => $"Role=DogSheriff;ShotLimit={role.ShotLimit};CurrentKillCooldown={Invariant(role.CurrentKillCooldown)}",
            TownOfHostForE.Roles.Crewmate.Sheriff role => $"Role=Sheriff;ShotLimit={role.ShotLimit};CurrentKillCooldown={Invariant(role.CurrentKillCooldown)}",
            TownOfHostForE.Roles.Crewmate.SillySheriff role => $"Role=SillySheriff;ShotLimit={role.ShotLimit};CurrentKillCooldown={Invariant(role.CurrentKillCooldown)}",
            TownOfHostForE.Roles.Impostor.Witch role => $"Role=Witch;IsSpellMode={role.IsSpellMode};SwitchTrigger={role.NowSwitchTrigger}",
            TownOfHostForE.Roles.Crewmate.Chairman role => $"Role=Chairman;LeftButtonCount={role.LeftButtonCount}",
            TownOfHostForE.Roles.Neutral.Tuna role => $"Role=Tuna;Animals={TownOfHostForE.Roles.Neutral.Tuna.TunaTypeAnimals}",
            // ROLEBUGTRACE_STAGE2_PHASEB_PRIVATE_STATE_PERMANENT_PROVIDER
            // private runtime状態はread-only accessor経由で、既存event snapshot時だけ取得する。
            TownOfHostForE.Roles.Animals.Kraken role => $"Role=Kraken;KillCount={role.RoleBugTraceKillCount};TargetId={role.RoleBugTraceTargetId};ShokushuTargetActive={role.RoleBugTraceHasShokushuTarget}",
            TownOfHostForE.Roles.Impostor.Sniper role => $"Role=Sniper;BulletCount={role.RoleBugTraceBulletCount};MeetingReset={role.RoleBugTraceMeetingReset}",
            TownOfHostForE.Roles.Crewmate.GreatDetective role => $"Role=GreatDetective;TargetId={role.RoleBugTraceTargetId};TargetState={role.RoleBugTraceTargetState};SuiriCount={role.RoleBugTraceSuiriCount}",
            TownOfHostForE.Roles.Animals.Nyaoha role => $"Role=Nyaoha;SolarBeamCount={role.RoleBugTraceSolarBeamCount};FireCount={role.RoleBugTraceFireCount};PetAbilityActive={role.IsPetAbilityActive}",
            TownOfHostForE.Roles.Impostor.CursedWolf role => $"Role=CursedWolf;SpellCount={role.RoleBugTraceSpellCount}",
            TownOfHostForE.Roles.Impostor.Warlock role => $"Role=Warlock;Shapeshifting={role.RoleBugTraceShapeshifting}",
            TownOfHostForE.Roles.Neutral.LawTracker role => $"Role=LawTracker;GuardCount={role.RoleBugTraceGuardCount}",
            _ => ""
        };
    }

    private static string PlayerIdOrDash(PlayerControl? player)
        => player == null ? "-" : player.PlayerId.ToString();

    private static string Invariant(float value)
        => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string GetPhase()
    {
        if (GameStates.IsMeeting) return "Meeting";
        if (GameStates.IsInTask) return "Task";
        if (GameStates.IsInGame) return "Game";
        if (GameStates.IsLobby) return "Lobby";
        return "Other";
    }

    private static string GetName(PlayerControl? player)
    {
        if (player == null) return "-";
        return player.Data?.PlayerName ?? player.name ?? "-";
    }

    private static string GetRole(PlayerControl? player)
    {
        if (player == null) return "-";
        return player.GetCustomRole().ToString();
    }

    private static string GetSubRoles(PlayerControl? player)
    {
        if (player == null) return "-";

        var state = PlayerState.GetByPlayerId(player.PlayerId);
        if (state == null || state.SubRoles == null || state.SubRoles.Count == 0)
            return "-";

        return string.Join(",", state.SubRoles);
    }

    private static string Safe(string value)
    {
        if (string.IsNullOrEmpty(value)) return "-";

        return value
            .Replace("|", "/")
            .Replace("\r", " ")
            .Replace("\n", " ");
    }
}
