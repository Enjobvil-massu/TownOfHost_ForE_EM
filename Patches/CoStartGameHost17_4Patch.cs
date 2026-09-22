using System.Collections;

using AmongUs.GameOptions;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using InnerNet;
using UnityEngine;

using TownOfHostForE.Modules;

namespace TownOfHostForE
{
    [HarmonyPatch(
        typeof(AmongUsClient),
        nameof(AmongUsClient.CoStartGameHost))]
    internal static class CoStartGameHost17_4Patch
    {
        private const string LogLabel =
            "CoStartGameHost17_4";

        private const float SenderIntervalSeconds =
            0.3f;

        internal static bool Enabled = true;

        [HarmonyPrefix]
        private static bool Prefix(
            AmongUsClient __instance,
            ref Il2CppSystem.Collections.IEnumerator __result)
        {
            if (!Enabled) return true;
            if (!__instance.AmHost) return true;

            // バニラのHide N Seek開始処理には介入しない。
            if (
                GameOptionsManager
                    .Instance
                    .CurrentGameOptions
                    .GameMode
                is GameModes.HideNSeek
                    or GameModes.SeekFools
            )
            {
                return true;
            }

            __result =
                StartGameHost(__instance)
                    .WrapToIl2Cpp();

            return false;
        }

        private static IEnumerator AdvanceLoadingBar(
            LoadingBarManager? loadingBarManager,
            float startPercent,
            float endPercent,
            float durationSeconds)
        {
            if (durationSeconds <= 0f)
            {
                if (loadingBarManager != null)
                {
                    loadingBarManager.SetLoadingPercent(
                        endPercent,
                        StringNames.LoadingBarGameStart
                    );
                }

                yield break;
            }

            var elapsedSeconds = 0f;

            while (elapsedSeconds < durationSeconds)
            {
                elapsedSeconds += Time.unscaledDeltaTime;

                if (loadingBarManager != null)
                {
                    var progress = Mathf.Clamp01(
                        elapsedSeconds / durationSeconds
                    );

                    loadingBarManager.SetLoadingPercent(
                        Mathf.Lerp(
                            startPercent,
                            endPercent,
                            progress
                        ),
                        StringNames.LoadingBarGameStart
                    );
                }

                yield return null;
            }

            if (loadingBarManager != null)
            {
                loadingBarManager.SetLoadingPercent(
                    endPercent,
                    StringNames.LoadingBarGameStart
                );
            }
        }

        private static IEnumerator StartGameHost(
            AmongUsClient client)
        {
            CriticalStartFlowPacketDiagnostic.Mark(
                "COSTARTGAMEHOST_ENTER",
                $"ClientId={client.ClientId} Players={PlayerControl.AllPlayerControls.Count}");

            var loadingBarManager =
                LoadingBarManager.Instance;

            if (loadingBarManager != null)
            {
                loadingBarManager.ToggleLoadingBar(true);

                loadingBarManager.SetLoadingPercent(
                    0f,
                    StringNames.LoadingBarGameStart
                );
            }

            if (LobbyBehaviour.Instance)
            {
                LobbyBehaviour.Instance.Despawn();
            }

            if (!ShipStatus.Instance)
            {
                var mapId = Mathf.Clamp(
                    GameOptionsManager
                        .Instance
                        .CurrentGameOptions
                        .MapId,
                    0,
                    Constants.MapNames.Length - 1
                );

                client.ShipLoadingAsyncHandle =
                    client
                        .ShipPrefabs[mapId]
                        .InstantiateAsync();

                while (
                    !client
                        .ShipLoadingAsyncHandle
                        .IsDone
                )
                {
                    if (loadingBarManager != null)
                    {
                        var progress =
                            client
                                .ShipLoadingAsyncHandle
                                .PercentComplete;

                        loadingBarManager.SetLoadingPercent(
                            Mathf.Lerp(
                                0f,
                                10f,
                                progress
                            ),
                            StringNames.LoadingBarGameStart
                        );
                    }

                    yield return null;
                }

                var shipGameObject =
                    client
                        .ShipLoadingAsyncHandle
                        .Result;

                ShipStatus.Instance =
                    shipGameObject
                        .GetComponent<ShipStatus>();

                client.Spawn(
                    ShipStatus.Instance,
                    -2,
                    SpawnFlags.None
                );
            }

            if (loadingBarManager != null)
            {
                loadingBarManager.SetLoadingPercent(
                    10f,
                    StringNames.LoadingBarGameStart
                );
            }

            var readyTimer = 0f;

            while (true)
            {
                var allClientsReady = true;

                var maximumReadyWaitSeconds =
                    GameOptionsManager
                        .Instance
                        .CurrentGameOptions
                        .MapId is 4 or 5
                            ? 15
                            : 10;

                lock (client.allClients)
                {
                    for (
                        var index = 0;
                        index < client.allClients.Count;
                        index++
                    )
                    {
                        ClientData clientData =
                            client.allClients[index];

                        if (
                            clientData.Id ==
                            client.ClientId
                        ) {
                            continue;
                        }

                        if (clientData.IsReady)
                        {
                            continue;
                        }

                        if (
                            readyTimer <
                            maximumReadyWaitSeconds
                        )
                        {
                            allClientsReady = false;
                            continue;
                        }

                        client.SendLateRejection(
                            clientData.Id,
                            DisconnectReasons.ClientTimeout
                        );

                        Logger.Info(
                            clientData.Id +
                            " timed out.",
                            LogLabel
                        );

                        clientData.IsReady = true;

                        client.OnPlayerLeft(
                            clientData,
                            DisconnectReasons.ClientTimeout
                        );
                    }
                }

                yield return null;

                if (allClientsReady)
                {
                    break;
                }

                readyTimer += Time.deltaTime;
            }


            yield return AdvanceLoadingBar(
                loadingBarManager,
                10f,
                90f,
                2f
            );
            CriticalStartFlowPacketDiagnostic.Mark("LOADING_90_RETURNED");

            yield return SettingsPacketPacer.WaitForDrain(12f);



            SelectRolesPatch.DeferRoleFinalization =
                true;

            try
            {
                CriticalStartFlowPacketDiagnostic.Mark("SELECT_ROLES_CALL");
                RoleManager.Instance.SelectRoles();
                CriticalStartFlowPacketDiagnostic.Mark("SELECT_ROLES_RETURNED");
            }
            finally
            {
                SelectRolesPatch.DeferRoleFinalization =
                    false;
            }


            CriticalStartFlowPacketDiagnostic.Mark("ROLE_SELECTION_DEFERRED_CALL");
            yield return
                SelectRolesPatch
                    .CompleteRoleSelectionDeferred(
                        SenderIntervalSeconds
                    );
            CriticalStartFlowPacketDiagnostic.Mark("ROLE_SELECTION_DEFERRED_RETURNED_TO_HOST");


            // TASKSTATE_ROOTCAUSE_LOADING_ORDER_PERMANENT:
            // ShipStatus.Beginは100%描画yieldより先に実行する。
            // UI描画待機でIntro側TaskState.Initがタスク配布より先行する回帰を防ぐ。
            CriticalStartFlowPacketDiagnostic.Mark("SHIPSTATUS_BEGIN_CALL");
            ShipStatus.Instance.Begin();
            CriticalStartFlowPacketDiagnostic.MarkShipStatusBeginReturned();
            StartFlowReadiness.MarkShipStatusBeginReturned();
            yield return StartFlowReadiness.WaitForIntroSyncReady(12f);

            if (loadingBarManager != null)
            {
                // START_LOADING_100_PERCENT_PERMANENT: 100%を描画してからゲームへ移行
                yield return AdvanceLoadingBar(
                    loadingBarManager,
                    90f,
                    100f,
                    0.25f
                );
                CriticalStartFlowPacketDiagnostic.Mark("LOADING_100_RETURNED");
            }

            CriticalStartFlowPacketDiagnostic.Mark("SEND_CLIENT_READY_CALL");
            client.SendClientReady();
            CriticalStartFlowPacketDiagnostic.Mark("SEND_CLIENT_READY_RETURNED");

            if (loadingBarManager != null)
            {
                loadingBarManager.ToggleLoadingBar(false);
            }

            CriticalStartFlowPacketDiagnostic.Mark("COSTARTGAMEHOST_RETURN");

        }
    }
}
