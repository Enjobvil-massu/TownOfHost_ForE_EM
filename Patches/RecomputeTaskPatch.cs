using HarmonyLib;

namespace TownOfHostForE
{
    [HarmonyPatch(typeof(GameData), nameof(GameData.RecomputeTaskCounts))]
    class CustomTaskCountsPatch
    {
        public static bool Prefix(GameData __instance)
        {
            // A4_PLAYERSTATE_READINESS_GUARD_PERMANENT:
            // GameData.RecomputeTaskCounts はロビー中にも公式側から呼ばれる。
            // PlayerState はゲーム開始処理で作成されるため、全員分が未作成の間は
            // A4集計で上書きせず公式 RecomputeTaskCounts へフォールバックする。
            if (__instance == null || __instance.AllPlayers == null) return true;
            foreach (var p in __instance.AllPlayers)
            {
                if (p == null) continue;
                if (PlayerState.GetByPlayerId(p.PlayerId) == null) return true;
            }

            __instance.TotalTasks = 0;
            __instance.CompletedTasks = 0;
            foreach (var p in __instance.AllPlayers)
            {
                if (p == null) continue;
                // RECOMPUTE_TASKSTATE_WIN_COUNTS_PERMANENT:
                // TOH4E独自のUtils.HasTasks(ForRecompute=true)を維持したまま、
                // 勝利用GameData集計をPlayerState.TaskStateへ統一する。
                var taskState = PlayerState.GetByPlayerId(p.PlayerId).GetTaskState();
                var hasTasks = Utils.HasTasks(p) && taskState.AllTasksCount > 0;
                if (hasTasks)
                {
                    __instance.TotalTasks += taskState.AllTasksCount;
                    __instance.CompletedTasks += taskState.CompletedTasksCount;
                }
            }

            return false;
        }
    }
    [HarmonyPatch(typeof(GameData), nameof(GameData.CompleteTask))]
    class CompleteTaskPatch
    {
        public static bool Prefix(PlayerControl pc, uint taskId)
        {
            NetworkedPlayerInfo.TaskInfo taskById = pc.Data.FindTaskById(taskId);
            if (taskById != null)
            {
                if (!taskById.Complete)
                {
                    taskById.Complete = true;
                    if (Utils.HasTasks(pc.Data))
                        ++GameData.Instance.CompletedTasks;
                    Logger.Info($"{pc?.name} {Utils.HasTasks(pc?.Data)} TotalTaskCounts = {GameData.Instance.CompletedTasks}/{GameData.Instance.TotalTasks}", "CompleteTaskPatch.Prefix");
                }
            }

            return false;
        }
    }
}