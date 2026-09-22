using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Crewmate;

namespace TownOfHostForE
{
    //参考元 : https://github.com/ykundesu/SuperNewRoles/blob/master/SuperNewRoles/Mode/SuperHostRoles/BlockTool.cs
    class DisableDevice
    {
        public static bool DoDisable => Options.DisableDevices.GetBool() || Options.TimeLimitDevices.GetBool() || Options.IsStandardHAS || IsPoorEnable();
        private static List<byte> DesyncComms = new();
        private static int frame = 0;

        // DEVICE_TIME_LIMIT_PORT_V21
        public static float GameAdminTimer = 0f;
        public static float GameLogAndCamTimer = 0f;
        public static float GameVitalTimer = 0f;

        // SecurityCameraSystemType active-user count.
        public static int UseCount = 0;

        private static readonly Dictionary<byte, Vector2> AdminTimeLimitPositions = new();
        private static readonly Dictionary<byte, Vector2> LogTimeLimitPositions = new();
        private static readonly Dictionary<byte, Vector2> VitalTimeLimitPositions = new();
        public static readonly Dictionary<string, Vector2> DevicePos = new()
        {
            ["SkeldAdmin"] = new(3.48f, -8.62f),
            ["SkeldCamera"] = new(-13.06f, -2.45f),
            ["MiraHQAdmin"] = new(21.02f, 19.09f),
            ["MiraHQDoorLog"] = new(16.22f, 5.82f),
            ["PolusLeftAdmin"] = new(22.80f, -21.52f),
            ["PolusRightAdmin"] = new(24.66f, -21.52f),
            ["PolusCamera"] = new(2.96f, -12.74f),
            ["PolusVital"] = new(26.70f, -15.94f),
            ["AirshipCockpitAdmin"] = new(-22.32f, 0.91f),
            ["AirshipRecordsAdmin"] = new(19.89f, 12.60f),
            ["AirshipCamera"] = new(8.10f, -9.63f),
            ["AirshipVital"] = new(25.24f, -7.94f),
            ["FungleVital"] = new(-2.765f, -9.819f)
        };
        public static float UsableDistance()
        {
            var Map = (MapNames)Main.NormalOptions.MapId;
            return Map switch
            {
                MapNames.Skeld => 1.8f,
                MapNames.MiraHQ => 2.4f,
                MapNames.Polus => 1.8f,
                //MapNames.Dleks => 1.5f,
                MapNames.Airship => 1.8f,
                MapNames.Fungle => 1.8f,
                _ => 0.0f
            };
        }
        public static bool IsInfoPoor(PlayerControl pc)
        {
            return ((pc.Is(CustomRoles.Sheriff) && Sheriff.IsInfoPoor.GetBool())
                    || (pc.Is(CustomRoles.SillySheriff) && SillySheriff.IsInfoPoor.GetBool())
                    || pc.Is(CustomRoles.InfoPoor));
        }
        public static bool IsPoorEnable()
        {
            foreach (var pc in Main.AllAlivePlayerControls)
            {
                if (IsInfoPoor(pc))
                    return true;
            }
            return false;
        }
        // ====================================================================
        // DEVICE_TIME_LIMIT_PORT_V21
        // ====================================================================

        [Attributes.GameModuleInitializer]
        public static void ResetDeviceTimeLimit()
        {
            GameAdminTimer = 0f;
            GameLogAndCamTimer = 0f;
            GameVitalTimer = 0f;
            UseCount = 0;

            AdminTimeLimitPositions.Clear();
            LogTimeLimitPositions.Clear();
            VitalTimeLimitPositions.Clear();
        }

        public static void StartMeetingTimeLimit()
        {
            // Game-wide elapsed timers survive meetings.
            AdminTimeLimitPositions.Clear();
            LogTimeLimitPositions.Clear();
            VitalTimeLimitPositions.Clear();
            UseCount = 0;
        }

        public static bool IsAdminTimeLimitExpired()
        {
            if (!Options.TimeLimitDevices.GetBool()) return false;

            float limit = Options.TimeLimitAdmin.GetFloat();
            return limit > 0f && GameAdminTimer >= limit;
        }

        public static bool IsCameraTimeLimitExpired()
        {
            if (!Options.TimeLimitDevices.GetBool()) return false;

            float limit = Options.TimeLimitCamAndLog.GetFloat();
            return limit > 0f && GameLogAndCamTimer >= limit;
        }

        public static bool IsVitalTimeLimitExpired()
        {
            if (!Options.TimeLimitDevices.GetBool()) return false;

            float limit = Options.TimeLimitVital.GetFloat();
            return limit > 0f && GameVitalTimer >= limit;
        }

        private static void ConsumeTimer(
            ref float elapsed,
            float limit,
            float delta)
        {
            // zero = infinity
            if (limit <= 0f ||
                delta <= 0f ||
                elapsed >= limit)
            {
                return;
            }

            elapsed = Mathf.Min(limit, elapsed + delta);
        }

        public static void ConsumeAdminTime(float delta)
        {
            if (!Options.TimeLimitDevices.GetBool()) return;

            ConsumeTimer(
                ref GameAdminTimer,
                Options.TimeLimitAdmin.GetFloat(),
                delta);
        }

        public static void ConsumeLogAndCamTime(float delta)
        {
            if (!Options.TimeLimitDevices.GetBool()) return;

            ConsumeTimer(
                ref GameLogAndCamTimer,
                Options.TimeLimitCamAndLog.GetFloat(),
                delta);
        }

        public static void ConsumeVitalTime(float delta)
        {
            if (!Options.TimeLimitDevices.GetBool()) return;

            ConsumeTimer(
                ref GameVitalTimer,
                Options.TimeLimitVital.GetFloat(),
                delta);
        }

        private static bool ConsumeStationaryTimer(
            Dictionary<byte, Vector2> positions,
            PlayerControl pc,
            Vector2 position,
            ref float elapsed,
            float limit)
        {
            if (limit <= 0f)
            {
                positions.Remove(pc.PlayerId);
                return false;
            }

            if (elapsed >= limit)
            {
                return true;
            }

            if (positions.TryGetValue(pc.PlayerId, out var oldPosition))
            {
                if (oldPosition == position)
                {
                    // DisableDevice.FixedUpdate executes this path once
                    // every four fixed ticks.
                    ConsumeTimer(
                        ref elapsed,
                        limit,
                        Time.fixedDeltaTime * 4f);
                }
                else
                {
                    positions[pc.PlayerId] = position;
                }
            }
            else
            {
                positions.TryAdd(pc.PlayerId, position);
            }

            return elapsed >= limit;
        }

        private static void ClearTimeLimitTracking(byte playerId)
        {
            AdminTimeLimitPositions.Remove(playerId);
            LogTimeLimitPositions.Remove(playerId);
            VitalTimeLimitPositions.Remove(playerId);
        }

        private static bool ApplyVanillaTimeLimit(
            PlayerControl pc,
            Vector2 playerPosition,
            bool alreadyBlocked)
        {
            if (!Options.TimeLimitDevices.GetBool() ||
                !pc.IsAlive() ||
                pc.inVent ||
                !GameStates.IsInTask ||
                Utils.IsActive(SystemTypes.Comms))
            {
                ClearTimeLimitTracking(pc.PlayerId);
                return false;
            }

            float usableDistance = UsableDistance();

            bool adminNear = false;
            bool logNear = false;
            bool cameraNear = false;
            bool vitalNear = false;

            switch ((MapNames)Main.NormalOptions.MapId)
            {
                case MapNames.Skeld:
                    adminNear =
                        Vector2.Distance(
                            playerPosition,
                            DevicePos["SkeldAdmin"]) <= usableDistance;

                    cameraNear =
                        Vector2.Distance(
                            playerPosition,
                            DevicePos["SkeldCamera"]) <= usableDistance;
                    break;

                case MapNames.MiraHQ:
                    adminNear =
                        Vector2.Distance(
                            playerPosition,
                            DevicePos["MiraHQAdmin"]) <= usableDistance;

                    logNear =
                        Vector2.Distance(
                            playerPosition,
                            DevicePos["MiraHQDoorLog"]) <= usableDistance;
                    break;

                case MapNames.Polus:
                    adminNear =
                        Vector2.Distance(
                            playerPosition,
                            DevicePos["PolusLeftAdmin"]) <= usableDistance ||
                        Vector2.Distance(
                            playerPosition,
                            DevicePos["PolusRightAdmin"]) <= usableDistance;

                    cameraNear =
                        Vector2.Distance(
                            playerPosition,
                            DevicePos["PolusCamera"]) <= usableDistance;

                    vitalNear =
                        Vector2.Distance(
                            playerPosition,
                            DevicePos["PolusVital"]) <= usableDistance;
                    break;

                case MapNames.Airship:
                    adminNear =
                        Vector2.Distance(
                            playerPosition,
                            DevicePos["AirshipCockpitAdmin"]) <= usableDistance ||
                        Vector2.Distance(
                            playerPosition,
                            DevicePos["AirshipRecordsAdmin"]) <= usableDistance;

                    cameraNear =
                        Vector2.Distance(
                            playerPosition,
                            DevicePos["AirshipCamera"]) <= usableDistance;

                    vitalNear =
                        Vector2.Distance(
                            playerPosition,
                            DevicePos["AirshipVital"]) <= usableDistance;
                    break;

                case MapNames.Fungle:
                    vitalNear =
                        Vector2.Distance(
                            playerPosition,
                            DevicePos["FungleVital"]) <= usableDistance;
                    break;
            }

            bool timerBlocked = false;

            if (adminNear && !alreadyBlocked)
            {
                timerBlocked |= ConsumeStationaryTimer(
                    AdminTimeLimitPositions,
                    pc,
                    playerPosition,
                    ref GameAdminTimer,
                    Options.TimeLimitAdmin.GetFloat());
            }
            else
            {
                AdminTimeLimitPositions.Remove(pc.PlayerId);
            }

            // MIRA HQ Doorlog shares Camera/Doorlog time.
            if (logNear && !alreadyBlocked)
            {
                timerBlocked |= ConsumeStationaryTimer(
                    LogTimeLimitPositions,
                    pc,
                    playerPosition,
                    ref GameLogAndCamTimer,
                    Options.TimeLimitCamAndLog.GetFloat());
            }
            else
            {
                LogTimeLimitPositions.Remove(pc.PlayerId);
            }

            // Camera elapsed time itself is measured by the native
            // SecurityCameraSystemType active-user count.
            if (cameraNear && IsCameraTimeLimitExpired())
            {
                timerBlocked = true;
            }

            if (vitalNear && !alreadyBlocked)
            {
                timerBlocked |= ConsumeStationaryTimer(
                    VitalTimeLimitPositions,
                    pc,
                    playerPosition,
                    ref GameVitalTimer,
                    Options.TimeLimitVital.GetFloat());
            }
            else
            {
                VitalTimeLimitPositions.Remove(pc.PlayerId);
            }

            return timerBlocked;
        }

        private static void TickCameraTimeLimit()
        {
            if (!AmongUsClient.Instance.AmHost ||
                !Options.TimeLimitDevices.GetBool() ||
                !GameStates.IsInTask ||
                UseCount <= 0)
            {
                return;
            }

            // Shared camera time is consumed once per active user.
            ConsumeLogAndCamTime(
                Time.fixedDeltaTime * UseCount);
        }

        private static string FormatRemainingTime(
            float limit,
            float elapsed)
        {
            if (limit <= 0f)
            {
                return "\u221E";
            }

            float remaining =
                Mathf.Max(0f, limit - elapsed);

            if (remaining <= 0f)
            {
                return "0.0秒";
            }

            // 0.1-second CEILING.
            // The epsilon prevents normal float noise from making,
            // for example, 18.40000 display as 18.5.
            int tenths =
                Mathf.Max(
                    1,
                    Mathf.CeilToInt(
                        Mathf.Max(
                            0f,
                            remaining - 0.0001f
                        ) * 10f
                    )
                );

            return $"{tenths / 10}.{tenths % 10}秒";
        }

        private static string TimePart(
            string labelKey,
            float limit,
            float elapsed)
        {
            return
                Translator.GetString(labelKey) +
                " " +
                FormatRemainingTime(limit, elapsed);
        }

        public static string GetMeetingTimeLimitText()
        {
            if (!Options.TimeLimitDevices.GetBool() ||
                !Options.ShowDeviceTimeLimitInMeeting.GetBool())
            {
                return "";
            }

            string admin =
                TimePart(
                    "DeviceTimeLimitAdminLabel",
                    Options.TimeLimitAdmin.GetFloat(),
                    GameAdminTimer);

            string camera =
                TimePart(
                    "DeviceTimeLimitCameraLabel",
                    Options.TimeLimitCamAndLog.GetFloat(),
                    GameLogAndCamTimer);

            string doorlog =
                TimePart(
                    "DeviceTimeLimitDoorLogLabel",
                    Options.TimeLimitCamAndLog.GetFloat(),
                    GameLogAndCamTimer);

            string vital =
                TimePart(
                    "DeviceTimeLimitVitalLabel",
                    Options.TimeLimitVital.GetFloat(),
                    GameVitalTimer);

            return (MapNames)Main.NormalOptions.MapId switch
            {
                MapNames.Skeld =>
                    $"{admin} ｜ {camera}\n<color=#00000000>.</color>",

                MapNames.MiraHQ =>
                    $"{admin} ｜ {doorlog}\n<color=#00000000>.</color>",

                MapNames.Polus =>
                    $"{admin} ｜ {camera}\n{vital}\n<color=#00000000>.</color>",

                MapNames.Airship =>
                    $"{admin} ｜ {camera}\n{vital}\n<color=#00000000>.</color>",

                MapNames.Fungle =>
                    $"{vital}\n<color=#00000000>.</color>",

                _ => "",
            };
        }
        public static void FixedUpdate()
        {
            // Camera use must be integrated every fixed tick rather than
            // only once every four ticks.
            TickCameraTimeLimit();

            frame = frame == 3 ? 0 : ++frame;
            if (frame != 0) return;

            if (!DoDisable) return;
            foreach (var pc in Main.AllPlayerControls)
            {
                bool PcIsPoor = IsInfoPoor(pc);

                try
                {
                    if (pc.IsModClient()) continue;

                    bool doComms = false;
                    Vector2 PlayerPos = pc.GetTruePosition();
                    bool ignore = (Options.DisableDevicesIgnoreImpostors.GetBool() && pc.Is(CustomRoleTypes.Impostor)) ||
                            (Options.DisableDevicesIgnoreMadmates.GetBool() && pc.Is(CustomRoleTypes.Madmate)) ||
                            (Options.DisableDevicesIgnoreNeutrals.GetBool() && pc.Is(CustomRoleTypes.Neutral)) ||
                            (Options.DisableDevicesIgnoreAnimals.GetBool() && pc.Is(CustomRoleTypes.Animals)) ||
                            (Options.DisableDevicesIgnoreCrewmates.GetBool() && pc.Is(CustomRoleTypes.Crewmate)) ||
                            (Options.DisableDevicesIgnoreAfterAnyoneDied.GetBool() && GameStates.AlreadyDied);

                    if (pc.IsAlive() && !Utils.IsActive(SystemTypes.Comms))
                    {
                        switch (Main.NormalOptions.MapId)
                        {
                            case 0:
                                if (Options.DisableSkeldAdmin.GetBool() || PcIsPoor)
                                    doComms |= Vector2.Distance(PlayerPos, DevicePos["SkeldAdmin"]) <= UsableDistance();
                                if (Options.DisableSkeldCamera.GetBool() || PcIsPoor)
                                    doComms |= Vector2.Distance(PlayerPos, DevicePos["SkeldCamera"]) <= UsableDistance();
                                break;
                            case 1:
                                if (Options.DisableMiraHQAdmin.GetBool() || PcIsPoor)
                                    doComms |= Vector2.Distance(PlayerPos, DevicePos["MiraHQAdmin"]) <= UsableDistance();
                                if (Options.DisableMiraHQDoorLog.GetBool() || PcIsPoor)
                                    doComms |= Vector2.Distance(PlayerPos, DevicePos["MiraHQDoorLog"]) <= UsableDistance();
                                break;
                            case 2:
                                if (Options.DisablePolusAdmin.GetBool() || PcIsPoor)
                                {
                                    doComms |= Vector2.Distance(PlayerPos, DevicePos["PolusLeftAdmin"]) <= UsableDistance();
                                    doComms |= Vector2.Distance(PlayerPos, DevicePos["PolusRightAdmin"]) <= UsableDistance();
                                }
                                if (Options.DisablePolusCamera.GetBool() || PcIsPoor)
                                    doComms |= Vector2.Distance(PlayerPos, DevicePos["PolusCamera"]) <= UsableDistance();
                                if (Options.DisablePolusVital.GetBool() || PcIsPoor)
                                    doComms |= Vector2.Distance(PlayerPos, DevicePos["PolusVital"]) <= UsableDistance();
                                break;
                            case 4:
                                if (Options.DisableAirshipCockpitAdmin.GetBool() || PcIsPoor)
                                    doComms |= Vector2.Distance(PlayerPos, DevicePos["AirshipCockpitAdmin"]) <= UsableDistance();
                                if (Options.DisableAirshipRecordsAdmin.GetBool() || PcIsPoor)
                                    doComms |= Vector2.Distance(PlayerPos, DevicePos["AirshipRecordsAdmin"]) <= UsableDistance();
                                if (Options.DisableAirshipCamera.GetBool() || PcIsPoor)
                                    doComms |= Vector2.Distance(PlayerPos, DevicePos["AirshipCamera"]) <= UsableDistance();
                                if (Options.DisableAirshipVital.GetBool() || PcIsPoor)
                                    doComms |= Vector2.Distance(PlayerPos, DevicePos["AirshipVital"]) <= UsableDistance();
                                break;
                            case 5:
                                if (Options.DisableFungleVital.GetBool())
                                {
                                    doComms |= Vector2.Distance(PlayerPos, DevicePos["FungleVital"]) <= UsableDistance();
                                }
                                break;
                        }
                    }
                    doComms &= !ignore;

                    // DEVICE_TIME_LIMIT_PORT_V21
                    // This path is Vanilla-only because the existing
                    // pc.IsModClient() skip above remains unchanged.
                    bool timerComms =
                        pc != PlayerControl.LocalPlayer &&
                        ApplyVanillaTimeLimit(
                            pc,
                            PlayerPos,
                            doComms);

                    doComms |= timerComms;

                    if (doComms && !pc.inVent && GameStates.IsInTask)
                    {
                        if (!DesyncComms.Contains(pc.PlayerId))
                            DesyncComms.Add(pc.PlayerId);

                        pc.RpcDesyncUpdateSystem(SystemTypes.Comms, 128);
                    }
                    else if (!Utils.IsActive(SystemTypes.Comms) && DesyncComms.Contains(pc.PlayerId))
                    {
                        DesyncComms.Remove(pc.PlayerId);
                        pc.RpcDesyncUpdateSystem(SystemTypes.Comms, 16);

                        if (Main.NormalOptions.MapId is 1 or 5)
                            pc.RpcDesyncUpdateSystem(SystemTypes.Comms, 17);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex, "DisableDevice");
                }
            }
        }
    }
    [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
    public class RemoveDisableDevicesPatch
    {
        public static void Postfix()
        {
            if (!(Options.DisableDevices.GetBool() || DisableDevice.IsPoorEnable())) return;
            UpdateDisableDevices();
        }

        public static void UpdateDisableDevices()
        {
            var player = PlayerControl.LocalPlayer;
            bool PcIsPoor = DisableDevice.IsInfoPoor(player);

            bool ignore = player.Is(CustomRoles.GM) ||
                !player.IsAlive() ||
                (Options.DisableDevicesIgnoreImpostors.GetBool() && player.Is(CustomRoleTypes.Impostor)) ||
                (Options.DisableDevicesIgnoreMadmates.GetBool() && player.Is(CustomRoleTypes.Madmate)) ||
                (Options.DisableDevicesIgnoreNeutrals.GetBool() && player.Is(CustomRoleTypes.Neutral)) ||
                (Options.DisableDevicesIgnoreAnimals.GetBool() && player.Is(CustomRoleTypes.Animals)) ||
                (Options.DisableDevicesIgnoreCrewmates.GetBool() && player.Is(CustomRoleTypes.Crewmate)) ||
                (Options.DisableDevicesIgnoreAfterAnyoneDied.GetBool() && GameStates.AlreadyDied);
            var admins = GameObject.FindObjectsOfType<MapConsole>(true);
            var consoles = GameObject.FindObjectsOfType<SystemConsole>(true);
            if (admins == null || consoles == null) return;
            switch (Main.NormalOptions.MapId)
            {
                case 0:
                    if (Options.DisableSkeldAdmin.GetBool() || PcIsPoor)
                        admins[0].gameObject.GetComponent<CircleCollider2D>().enabled = ignore;
                    if (Options.DisableSkeldCamera.GetBool() || PcIsPoor)
                        consoles.DoIf(x => x.name == "SurvConsole", x => x.gameObject.GetComponent<PolygonCollider2D>().enabled = ignore);
                    break;
                case 1:
                    if (Options.DisableMiraHQAdmin.GetBool() || PcIsPoor)
                        admins[0].gameObject.GetComponent<CircleCollider2D>().enabled = ignore;
                    if (Options.DisableMiraHQDoorLog.GetBool() || PcIsPoor)
                        consoles.DoIf(x => x.name == "SurvLogConsole", x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);
                    break;
                case 2:
                    if (Options.DisablePolusAdmin.GetBool() || PcIsPoor)
                        admins.Do(x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);
                    if (Options.DisablePolusCamera.GetBool() || PcIsPoor)
                        consoles.DoIf(x => x.name == "Surv_Panel", x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);
                    if (Options.DisablePolusVital.GetBool() || PcIsPoor)
                        consoles.DoIf(x => x.name == "panel_vitals", x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);
                    break;
                case 4:
                    admins.Do(x =>
                    {
                        if (((Options.DisableAirshipCockpitAdmin.GetBool() || PcIsPoor) && x.name == "panel_cockpit_map") ||
                            ((Options.DisableAirshipRecordsAdmin.GetBool() || PcIsPoor) && x.name == "records_admin_map"))
                            x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore;
                    });
                    if (Options.DisableAirshipCamera.GetBool() || PcIsPoor)
                        consoles.DoIf(x => x.name == "task_cams", x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);
                    if (Options.DisableAirshipVital.GetBool() || PcIsPoor)
                        consoles.DoIf(x => x.name == "panel_vitals", x => x.gameObject.GetComponent<CircleCollider2D>().enabled = ignore);
                    break;
                case 5:
                    if (Options.DisableFungleVital.GetBool())
                    {
                        consoles.DoIf(x => x.name == "VitalsConsole", x => x.GetComponent<Collider2D>().enabled = ignore);
                    }
                    break;
            }
        }
    }
    // ========================================================================
    // DEVICE_TIME_LIMIT_PORT_V21 - host local minigame enforcement
    // ========================================================================

    [HarmonyPatch(typeof(MapCountOverlay), nameof(MapCountOverlay.Update))]
    class DeviceTimeLimitAdminPatch
    {
        public static void Prefix(MapCountOverlay __instance)
        {
            if (!AmongUsClient.Instance.AmHost ||
                !Options.TimeLimitDevices.GetBool() ||
                !GameStates.IsInTask ||
                !PlayerControl.LocalPlayer.IsAlive() ||
                !MapBehaviour.Instance ||
                !__instance)
            {
                return;
            }

            if (DisableDevice.IsAdminTimeLimitExpired())
            {
                MapBehaviour.Instance.Close();
                return;
            }

            DisableDevice.ConsumeAdminTime(Time.deltaTime);

            if (DisableDevice.IsAdminTimeLimitExpired())
            {
                MapBehaviour.Instance.Close();
            }
        }
    }

    [HarmonyPatch(typeof(VitalsMinigame), nameof(VitalsMinigame.Update))]
    class DeviceTimeLimitVitalsPatch
    {
        public static void Postfix(VitalsMinigame __instance)
        {
            if (!AmongUsClient.Instance.AmHost ||
                !Options.TimeLimitDevices.GetBool() ||
                !GameStates.IsInTask ||
                !PlayerControl.LocalPlayer.IsAlive() ||
                !__instance)
            {
                return;
            }

            if (DisableDevice.IsVitalTimeLimitExpired())
            {
                __instance.Close();
                return;
            }

            DisableDevice.ConsumeVitalTime(Time.deltaTime);

            if (DisableDevice.IsVitalTimeLimitExpired())
            {
                __instance.Close();
            }
        }
    }

    [HarmonyPatch(typeof(SecurityLogGame), nameof(SecurityLogGame.Update))]
    class DeviceTimeLimitDoorLogPatch
    {
        public static void Postfix(SecurityLogGame __instance)
        {
            if (!AmongUsClient.Instance.AmHost ||
                !Options.TimeLimitDevices.GetBool() ||
                !GameStates.IsInTask ||
                !PlayerControl.LocalPlayer.IsAlive() ||
                !__instance)
            {
                return;
            }

            if (DisableDevice.IsCameraTimeLimitExpired())
            {
                __instance.Close();
                return;
            }

            DisableDevice.ConsumeLogAndCamTime(Time.deltaTime);

            if (DisableDevice.IsCameraTimeLimitExpired())
            {
                __instance.Close();
            }
        }
    }

    [HarmonyPatch(typeof(PlanetSurveillanceMinigame), nameof(PlanetSurveillanceMinigame.Update))]
    class DeviceTimeLimitPlanetCameraPatch
    {
        public static void Postfix(PlanetSurveillanceMinigame __instance)
        {
            if (!AmongUsClient.Instance.AmHost ||
                !Options.TimeLimitDevices.GetBool() ||
                !PlayerControl.LocalPlayer.IsAlive() ||
                !__instance)
            {
                return;
            }

            if (DisableDevice.IsCameraTimeLimitExpired())
            {
                __instance.Close();
            }
        }
    }

    [HarmonyPatch(typeof(SurveillanceMinigame), nameof(SurveillanceMinigame.Update))]
    class DeviceTimeLimitCameraPatch
    {
        public static void Postfix(SurveillanceMinigame __instance)
        {
            if (!AmongUsClient.Instance.AmHost ||
                !Options.TimeLimitDevices.GetBool() ||
                !PlayerControl.LocalPlayer.IsAlive() ||
                !__instance)
            {
                return;
            }

            if (DisableDevice.IsCameraTimeLimitExpired())
            {
                __instance.Close();
            }
        }
    }
}