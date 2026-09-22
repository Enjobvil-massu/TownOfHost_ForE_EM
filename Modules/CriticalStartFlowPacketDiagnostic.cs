using System;
using System.Diagnostics;

namespace TownOfHostForE.Modules
{
    /// <summary>
    /// CRITICAL_STARTFLOW_PACKET_DIAGNOSTIC_V1:
    /// Game start ordering and packet-shape diagnostics only. This class must not
    /// mutate gameplay state or alter recipients, payloads, reliability, or timing.
    /// </summary>
    internal static class CriticalStartFlowPacketDiagnostic
    {
        private const string LogLabel = "CRITICAL_STARTFLOW_PACKET_DIAGNOSTIC_V1";
        private static readonly Stopwatch Clock = new();
        private static int flowId;
        private static int settingsCallId;
        private static int gameOptionsPassId;

        internal static bool RoleCommitReached { get; private set; }
        internal static bool ShipStatusBeginReturned { get; private set; }

        internal static void BeginFlow(string source)
        {
            flowId++;
            settingsCallId = 0;
            gameOptionsPassId = 0;
            RoleCommitReached = false;
            ShipStatusBeginReturned = false;
            Clock.Restart();
            Mark("FLOW_BEGIN", $"Source={source}");
        }

        internal static void Mark(string stage, string detail = "")
        {
            if (!Clock.IsRunning) Clock.Start();
            Logger.Info(
                $"Flow={flowId} Tms={Clock.ElapsedMilliseconds} Stage={stage}" +
                (string.IsNullOrEmpty(detail) ? string.Empty : $" {detail}"),
                LogLabel);
        }

        internal static void MarkRoleCommit(string detail = "")
        {
            RoleCommitReached = true;
            Mark("ROLE_COMMIT_RETURNED", detail);
        }

        internal static void MarkShipStatusBeginReturned()
        {
            ShipStatusBeginReturned = true;
            Mark("SHIPSTATUS_BEGIN_RETURNED", GlobalTaskSnapshot());
        }

        internal static int BeginSettingsCall(string origin, int requestedTarget, int totalOptions)
        {
            var callId = ++settingsCallId;
            Mark(
                "SETTINGS_CALL_BEGIN",
                $"Call={callId} Origin={origin} RequestedTarget={requestedTarget} TotalOptions={totalOptions}");
            return callId;
        }

        internal static void MarkSettingsTarget(
            int callId,
            string origin,
            int targetClientId,
            int chunkCount,
            int totalOptions)
        {
            Mark(
                "SETTINGS_TARGET_BEGIN",
                $"Call={callId} Origin={origin} Target={targetClientId} Chunks={chunkCount} TotalOptions={totalOptions}");
        }

        internal static void MarkSettingsChunk(
            int callId,
            int targetClientId,
            int chunkIndex,
            int startIndex,
            int optionCount,
            int rootLength)
        {
            Mark(
                "SETTINGS_CHUNK_SEND",
                $"Call={callId} Target={targetClientId} Chunk={chunkIndex} Start={startIndex} Count={optionCount} RootLength={rootLength} Reliable=True");
        }

        internal static int BeginGameOptionsPass(string origin, int senderCount)
        {
            var passId = ++gameOptionsPassId;
            Mark(
                "GAMEOPTIONS_PASS_BEGIN",
                $"Pass={passId} Origin={origin} SenderCount={senderCount}");
            return passId;
        }

        internal static void MarkGameOptionsSender(
            int passId,
            string senderType,
            bool dirty,
            string target)
        {
            Mark(
                "GAMEOPTIONS_SENDER",
                $"Pass={passId} SenderType={senderType} Dirty={dirty} Target={target}");
        }

        internal static void MarkGameOptionsRoot(
            int targetClientId,
            byte logicOptionsIndex,
            int optionLength,
            int rootLength)
        {
            Mark(
                "GAMEOPTIONS_ROOT_SEND",
                $"Target={targetClientId} Recipient={(targetClientId == -1 ? "Broadcast" : "Targeted")} Tag={(targetClientId == -1 ? "GameData" : "GameDataTo")} LogicOptionsIndex={logicOptionsIndex} OptionLength={optionLength} RootLength={rootLength} Reliable=True");
        }

        internal static string PlayerSnapshot(PlayerControl? player)
        {
            if (player == null) return "Player=NULL";

            var state = PlayerState.GetByPlayerId(player.PlayerId);
            var data = player.Data;
            var nativeRole = data?.Role == null ? "NULL" : data.Role.Role.ToString();
            var dataTaskCount = data?.Tasks == null ? -1 : data.Tasks.Count;
            var mainRole = state == null ? "NO_STATE" : state.MainRole.ToString();
            var taskHas = state?.taskState?.hasTasks.ToString() ?? "NO_STATE";
            var taskAll = state?.taskState?.AllTasksCount ?? -2;
            var taskDone = state?.taskState?.CompletedTasksCount ?? -2;
            var customRole = "UNAVAILABLE";
            try
            {
                customRole = player.GetCustomRole().ToString();
            }
            catch (Exception ex)
            {
                customRole = $"ERROR:{ex.GetType().Name}";
            }

            var cachedSelf = "CACHE_UNAVAILABLE";
            if (Main.LastNotifyNames != null &&
                Main.LastNotifyNames.TryGetValue(
                    (player.PlayerId, player.PlayerId),
                    out var cachedName))
            {
                cachedSelf = Sanitize(cachedName);
            }

            return
                $"PlayerId={player.PlayerId} Owner={player.AmOwner} Mod={player.IsModClient()} " +
                $"MainRole={mainRole} DataRole={nativeRole} CustomRole={customRole} " +
                $"DataTasks={dataTaskCount} TaskState={taskDone}/{taskAll} HasTasks={taskHas} " +
                $"CachedSelf={cachedSelf} CommitReached={RoleCommitReached} ShipBeginReturned={ShipStatusBeginReturned}";
        }

        internal static string GlobalTaskSnapshot()
        {
            var gameData = GameData.Instance;
            if (gameData == null) return "GameData=NULL";
            return
                $"GameDataTotalTasks={gameData.TotalTasks} " +
                $"GameDataCompletedTasks={gameData.CompletedTasks} " +
                $"PlayerCount={gameData.PlayerCount}";
        }

        internal static void MarkAllPlayers(string stage)
        {
            foreach (var player in Main.AllPlayerControls)
            {
                Mark(stage, PlayerSnapshot(player));
            }
        }

        private static string Sanitize(string value)
        {
            if (value == null) return "NULL";
            const int maxLength = 160;
            var normalized = value.Replace('\r', ' ').Replace('\n', ' ');
            return normalized.Length <= maxLength
                ? normalized
                : normalized.Substring(0, maxLength) + "...";
        }
    }
}
