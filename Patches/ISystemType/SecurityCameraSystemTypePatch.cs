using HarmonyLib;
using Hazel;

namespace TownOfHostForE.Patches.ISystemType;

[HarmonyPatch(typeof(SecurityCameraSystemType), nameof(SecurityCameraSystemType.UpdateSystem))]
public static class SecurityCameraSystemTypeUpdateSystemPatch
{
    public static bool Prefix([HarmonyArgument(1)] MessageReader msgReader)
    {
        byte amount;
        {
            var newReader = MessageReader.Get(msgReader);
            amount = newReader.ReadByte();
            newReader.Recycle();
        }

        // カメラ無効時，バニラプレイヤーはカメラを開けるので点滅させない
        if (amount == SecurityCameraSystemType.IncrementOp)
        {
            var camerasDisabled = (MapNames)Main.NormalOptions.MapId switch
            {
                MapNames.Skeld => Options.DisableSkeldCamera.GetBool(),
                MapNames.Polus => Options.DisablePolusCamera.GetBool(),
                MapNames.Airship => Options.DisableAirshipCamera.GetBool(),
                _ => false,
            };

            // DEVICE_TIME_LIMIT_PORT_V21
            if (!camerasDisabled &&
                DisableDevice.IsCameraTimeLimitExpired())
            {
                camerasDisabled = true;
            }

            if (!camerasDisabled &&
                Options.TimeLimitDevices.GetBool())
            {
                DisableDevice.UseCount++;
            }

            return !camerasDisabled;
        }

        // DEVICE_TIME_LIMIT_PORT_V21
        if (amount == SecurityCameraSystemType.DecrementOp &&
            Options.TimeLimitDevices.GetBool() &&
            DisableDevice.UseCount > 0)
        {
            DisableDevice.UseCount--;
        }

        return true;
    }
}
