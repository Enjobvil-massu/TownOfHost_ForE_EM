using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AmongUs.GameOptions;
using Hazel;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSystem;
using InnerNet;
// Il2CppStructArray<byte>とbyte[]との間での暗黙的な変換の際に発生する重い計算を抑制するため，意図的にIl2CppSystemとIl2CppInterop.Runtime.InteropTypes.Arraysを使用します - Hyz-sui

namespace TownOfHostForE.Modules
{
    public abstract class GameOptionsSender
    {
        #region Static
        public readonly static List<GameOptionsSender> AllSenders = new(15) { new NormalGameOptionsSender() };

        public static void SendAllGameOptions(
            [CallerMemberName] string origin = "Unknown")
        {
            AllSenders.RemoveAll(s => !s.AmValid());
            var passId = CriticalStartFlowPacketDiagnostic.BeginGameOptionsPass(
                origin,
                AllSenders.Count);
            foreach (var sender in AllSenders)
            {
                var target = sender is PlayerGameOptionsSender playerSender
                    ? $"PlayerId={playerSender.player?.PlayerId.ToString() ?? "NULL"},ClientId={playerSender.player?.GetClientId().ToString() ?? "NULL"},Owner={playerSender.player?.AmOwner.ToString() ?? "NULL"}"
                    : "BroadcastOrLocal";
                CriticalStartFlowPacketDiagnostic.MarkGameOptionsSender(
                    passId,
                    sender.GetType().Name,
                    sender.IsDirty,
                    target);
                if (sender.IsDirty)
                {
                    TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.Pass(sender, passId));
                    try { sender.SendGameOptions(); }
                    finally { TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.Pass(sender, null)); }
                }
                sender.IsDirty = false;
            }
            CriticalStartFlowPacketDiagnostic.Mark(
                "GAMEOPTIONS_PASS_RETURN",
                $"Pass={passId} Origin={origin}");
        }
        #endregion

        public abstract IGameOptions BasedGameOptions { get; }
        public abstract bool IsDirty { get; protected set; }

        public virtual void SendGameOptions()
        {
            var opt = BuildGameOptions();
            var currentGameMode = AprilFoolsMode.IsAprilFoolsModeToggledOn //April fools mode toggled on by host
                ? opt.AprilFoolsOnMode : opt.GameMode; //Change game mode, same as well as in "RpcSyncSettings()"

            // option => byte[]
            MessageWriter writer = MessageWriter.Get(SendOption.None);
            writer.Write(opt.Version);
            writer.StartMessage(0);
            writer.Write((byte)currentGameMode);
            if (opt.TryCast<NormalGameOptionsV11>(out var normalOpt))
                NormalGameOptionsV11.Serialize(writer, normalOpt);
            else if (opt.TryCast<HideNSeekGameOptionsV11>(out var hnsOpt))
                HideNSeekGameOptionsV11.Serialize(writer, hnsOpt);
            else
            {
                writer.Recycle();
                Logger.Error("オプションのキャストに失敗しました", this.ToString());
                return;
            }
            writer.EndMessage();

            // 配列化&送信
            var byteArray = new Il2CppStructArray<byte>(writer.Length - 1);
            // MessageWriter.ToByteArray
            Buffer.BlockCopy(writer.Buffer.Cast<Array>(), 1, byteArray.Cast<Array>(), 0, writer.Length - 1);

            TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.Mode(this, currentGameMode));
            SendOptionsArray(byteArray);
            writer.Recycle();
        }
        public virtual void SendOptionsArray(Il2CppStructArray<byte> optionArray)
        {
            for (byte i = 0; i < GameManager.Instance.LogicComponents.Count; i++)
            {
                if (GameManager.Instance.LogicComponents[i].TryCast<LogicOptions>(out _))
                {
                    SendOptionsArray(optionArray, i, -1);
                }
            }
        }
        protected virtual void SendOptionsArray(Il2CppStructArray<byte> optionArray, byte LogicOptionsIndex, int targetClientId)
        {
            var writer = MessageWriter.Get(SendOption.Reliable);

            writer.StartMessage(targetClientId == -1 ? Tags.GameData : Tags.GameDataTo);
            {
                writer.Write(AmongUsClient.Instance.GameId);
                if (targetClientId != -1) writer.WritePacked(targetClientId);
                writer.StartMessage(1);
                {
                    writer.WritePacked(GameManager.Instance.NetId);
                    writer.StartMessage(LogicOptionsIndex);
                    {
                        writer.WriteBytesAndSize(optionArray);
                    }
                    writer.EndMessage();
                }
                writer.EndMessage();
            }
            writer.EndMessage();

            CriticalStartFlowPacketDiagnostic.MarkGameOptionsRoot(
                targetClientId,
                LogicOptionsIndex,
                optionArray.Length,
                writer.Length);
            var sendOrDisconnectReturned = false;
            try
            {
                AmongUsClient.Instance.SendOrDisconnect(writer);
                sendOrDisconnectReturned = true;
            }
            finally
            {
                TownOfHostForE.Modules.Priority2to5Diagnostics.Safe(() => TownOfHostForE.Modules.Priority2to5Diagnostics.Options(this, optionArray, LogicOptionsIndex, targetClientId, sendOrDisconnectReturned));
            }
            writer.Recycle();
        }
        public abstract IGameOptions BuildGameOptions();

        public virtual bool AmValid() => true;
    }
}
