using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using HarmonyLib;
using Hazel;
using InnerNet;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace TownOfHostForE.Modules
{
    /// <summary>
    /// HACKING_SESSION_EPOCH_DIAGNOSTIC_AB_V1:
    /// Logs-only session/join/disconnect instrumentation.
    /// MessageReader is inspected without any Read* call or position mutation.
    /// </summary>
    internal static class HackingSessionEpochDiagnostic
    {
        private const string LogLabel = "HACKING_SESSION_EPOCH_DIAGNOSTIC_AB_V1";
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly Dictionary<int, long> RemoteJoinMs = new();
        private static int sessionEpoch;
        private static long localJoinMs = -1;

        // HACKING_SESSION_EPOCH_DIAGNOSTIC_FAIL_OPEN_AB_V2
        internal static void Safe(Action action)
        {
            try
            {
                action?.Invoke();
            }
            catch
            {
                // Logs-only instrumentation must never affect gameplay or networking.
            }
        }

        internal static void BeginLocalSession(AmongUsClient client)
        {
            sessionEpoch++;
            localJoinMs = Clock.ElapsedMilliseconds;
            RemoteJoinMs.Clear();
            Mark("LOCAL_SESSION_BEGIN", ClientSnapshot(client));
        }

        internal static void Mark(string stage, string detail = "")
        {
            var now = Clock.ElapsedMilliseconds;
            var joinAge = localJoinMs < 0 ? -1 : now - localJoinMs;
            Logger.Info(
                $"Epoch={sessionEpoch} Tms={now} JoinAgeMs={joinAge} Stage={stage}" +
                (string.IsNullOrEmpty(detail) ? string.Empty : $" {detail}"),
                LogLabel);
        }

        internal static void MarkPlayerJoined(AmongUsClient client, ClientData data)
        {
            var now = Clock.ElapsedMilliseconds;

            if (data != null)
            {
                RemoteJoinMs[data.Id] = now;
            }

            Mark("PLAYER_JOINED",
                $"JoinedClientId={(data == null ? -1 : data.Id)} " +
                $"PlayerName={Sanitize(data == null ? null : data.PlayerName)} " +
                ClientSnapshot(client));
        }

        internal static void MarkPlayerLeft(
            AmongUsClient client,
            ClientData data,
            DisconnectReasons reason)
        {
            var now = Clock.ElapsedMilliseconds;
            var remoteAge = -1L;

            if (data != null && RemoteJoinMs.TryGetValue(data.Id, out var joinedAt))
            {
                remoteAge = now - joinedAt;
                RemoteJoinMs.Remove(data.Id);
            }

            Mark("PLAYER_LEFT",
                $"LeftClientId={(data == null ? -1 : data.Id)} " +
                $"RemoteJoinAgeMs={remoteAge} Reason={reason} " +
                $"PlayerName={Sanitize(data == null ? null : data.PlayerName)} " +
                ClientSnapshot(client));
        }

        internal static void MarkDisconnectInternal(
            InnerNetClient client,
            DisconnectReasons reason,
            string stringReason)
        {
            Mark("DISCONNECT_INTERNAL",
                $"Reason={reason} ReasonValue={(int)reason} " +
                $"StringReason={Sanitize(stringReason)} " +
                ClientSnapshot(AmongUsClient.Instance));
        }

        internal static void MarkRawDisconnect(
            InnerNetClient client,
            Il2CppSystem.Object sender,
            DisconnectedEventArgs e)
        {
            var hazelReason = e == null ? "NULL" : Sanitize(e.Reason);
            var message = e == null ? null : e.Message;

            Mark("ON_DISCONNECT_RAW",
                $"HazelReason={hazelReason} SenderNull={sender == null} " +
                MessageSnapshot(message));
        }

        private static string MessageSnapshot(MessageReader? reader)
        {
            if (reader == null)
            {
                return "Message=NULL";
            }

            try
            {
                var buffer = reader.Buffer;
                var bufferLength = buffer == null ? -1 : buffer.Length;
                var position = reader.Position;
                var internalPosition = reader._position;
                var offset = reader.Offset;
                var readHead = reader.readHead;
                var length = reader.Length;
                var remaining = reader.BytesRemaining;
                var tag = reader.Tag;
                var offsetPlusPosition = offset + position;

                return
                    $"MessageLength={length} BufferLength={bufferLength} " +
                    $"Offset={offset} Position={position} InternalPosition={internalPosition} " +
                    $"ReadHead={readHead} BytesRemaining={remaining} Tag={tag} " +
                    $"B0={ByteAt(buffer, 0)} " +
                    $"BOffset={ByteAt(buffer, offset)} " +
                    $"BPosition={ByteAt(buffer, position)} " +
                    $"BOffsetPlusPosition={ByteAt(buffer, offsetPlusPosition)} " +
                    $"BReadHead={ByteAt(buffer, readHead)} " +
                    $"Hex0={HexSlice(buffer, 0, 32)} " +
                    $"HexOffset={HexSlice(buffer, offset, 32)} " +
                    $"HexPosition={HexSlice(buffer, position, 32)} " +
                    $"HexReadHead={HexSlice(buffer, readHead, 32)}";
            }
            catch (Exception ex)
            {
                return $"MessageSnapshotError={ex.GetType().Name}:{Sanitize(ex.Message)}";
            }
        }

        private static string ByteAt(Il2CppStructArray<byte>? buffer, int index)
        {
            if (buffer == null || index < 0 || index >= buffer.Length)
            {
                return "NA";
            }

            return buffer[index].ToString("X2");
        }

        private static string HexSlice(
            Il2CppStructArray<byte>? buffer,
            int start,
            int maxCount)
        {
            if (buffer == null)
            {
                return "NULL";
            }

            if (start < 0 || start >= buffer.Length)
            {
                return "OUT";
            }

            var count = Math.Min(maxCount, buffer.Length - start);
            var builder = new StringBuilder(count * 3);

            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    builder.Append('-');
                }

                builder.Append(buffer[start + i].ToString("X2"));
            }

            return builder.ToString();
        }

        private static string ClientSnapshot(AmongUsClient client)
        {
            if (client == null)
            {
                return "Client=NULL";
            }

            return
                $"GameId={client.GameId} ClientId={client.ClientId} HostId={client.HostId} " +
                $"AmHost={client.AmHost} GameState={client.GameState} Ping={client.Ping}";
        }

        private static string Sanitize(string? value)
        {
            if (value == null)
            {
                return "NULL";
            }

            return value.Replace('\r', ' ').Replace('\n', ' ');
        }
    }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.OnDisconnect))]
    internal static class HackingSessionRawOnDisconnectPatch
    {
        public static void Prefix(
            InnerNetClient __instance,
            [HarmonyArgument(0)] Il2CppSystem.Object sender,
            [HarmonyArgument(1)] DisconnectedEventArgs e)
        {
            HackingSessionEpochDiagnostic.Safe(() =>
                HackingSessionEpochDiagnostic.MarkRawDisconnect(__instance, sender, e));
        }
    }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.EnqueueDisconnect))]
    internal static class HackingSessionEnqueueDisconnectPatch
    {
        public static void Prefix(
            InnerNetClient __instance,
            [HarmonyArgument(0)] DisconnectReasons reason,
            [HarmonyArgument(1)] string stringReason)
        {
            HackingSessionEpochDiagnostic.Safe(() =>
                HackingSessionEpochDiagnostic.Mark("ENQUEUE_DISCONNECT",
                    $"Reason={reason} ReasonValue={(int)reason} " +
                    $"StringReason={SanitizeForPatch(stringReason)}"));
        }

        private static string SanitizeForPatch(string value)
        {
            return value == null ? "NULL" : value.Replace('\r', ' ').Replace('\n', ' ');
        }
    }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.HandleDisconnect))]
    internal static class HackingSessionHandleDisconnectPatch
    {
        public static void Prefix(
            InnerNetClient __instance,
            [HarmonyArgument(0)] DisconnectReasons reason,
            [HarmonyArgument(1)] string stringReason)
        {
            HackingSessionEpochDiagnostic.Safe(() =>
                HackingSessionEpochDiagnostic.Mark("HANDLE_DISCONNECT",
                    $"Reason={reason} ReasonValue={(int)reason} " +
                    $"StringReason={SanitizeForPatch(stringReason)}"));
        }

        private static string SanitizeForPatch(string value)
        {
            return value == null ? "NULL" : value.Replace('\r', ' ').Replace('\n', ' ');
        }
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnBecomeHost))]
    internal static class HackingSessionOnBecomeHostPatch
    {
        public static void Postfix(AmongUsClient __instance)
        {
            HackingSessionEpochDiagnostic.Safe(() =>
                HackingSessionEpochDiagnostic.Mark("BECOME_HOST",
                    $"GameId={__instance.GameId} ClientId={__instance.ClientId} " +
                    $"HostId={__instance.HostId} AmHost={__instance.AmHost}"));
        }
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnDisconnected))]
    internal static class HackingSessionOnDisconnectedPatch
    {
        public static void Postfix(AmongUsClient __instance)
        {
            HackingSessionEpochDiagnostic.Safe(() =>
                HackingSessionEpochDiagnostic.Mark("ON_DISCONNECTED",
                    $"LastReason={__instance.LastDisconnectReason} " +
                    $"LastCustom={__instance.LastCustomDisconnect}"));
        }
    }
}
