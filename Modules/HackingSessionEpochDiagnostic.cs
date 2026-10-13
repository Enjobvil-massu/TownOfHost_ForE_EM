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
    /// Session/join/disconnect instrumentation and read-only optional-traffic guard.
    /// MessageReader is inspected without any Read* call or position mutation.
    /// </summary>
    internal static class HackingSessionEpochDiagnostic
    {
        private const string LogLabel = "HACKING_SESSION_EPOCH_DIAGNOSTIC_AB_V1";
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly Dictionary<int, long> RemoteJoinMs = new();
        private static int sessionEpoch;
        private static long localJoinMs = -1;
        // SAME_GAMEID_REJOIN_OPTIONAL_TRAFFIC_GUARD_AB_V1
        private static int lastLocalGameId = int.MinValue;
        private static bool sameGameIdRejoin;

        // HACKING_15S_WINDOW_DIAGNOSTIC_V2
        // Logs-only: no packet payload/order/recipient is modified.
        private static readonly int[] WindowCheckpointMs =
        {
            5000,
            10000,
            12000,
            14000,
            14500,
            14800,
            15000,
            15200,
            16000
        };

        private static int windowCheckpointMask;
        private static int windowOutboundCallCount;
        private static long windowOutboundBytes;
        private static long windowLastOutboundTms = -1;
        private static int windowLastOutboundLength = -1;
        private static string windowLastOutboundSendOption = "NA";

        private static int windowInboundCallCount;
        private static long windowInboundBytes;
        private static long windowLastInboundTms = -1;
        private static int windowLastInboundLength = -1;
        private static string windowLastInboundSendOption = "NA";

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

            // HACKING_15S_WINDOW_DIAGNOSTIC_V2
            windowCheckpointMask = 0;
            windowOutboundCallCount = 0;
            windowOutboundBytes = 0;
            windowLastOutboundTms = -1;
            windowLastOutboundLength = -1;
            windowLastOutboundSendOption = "NA";
            windowInboundCallCount = 0;
            windowInboundBytes = 0;
            windowLastInboundTms = -1;
            windowLastInboundLength = -1;
            windowLastInboundSendOption = "NA";

            sameGameIdRejoin =
                lastLocalGameId != int.MinValue &&
                lastLocalGameId == client.GameId;
            lastLocalGameId = client.GameId;
            Mark("LOCAL_SESSION_BEGIN",
                $"SameGameIdRejoin={sameGameIdRejoin} " + ClientSnapshot(client));
        }

        internal static bool IsSameGameIdRejoinProtectionWindow(int windowMs = 16000)
        {
            if (!sameGameIdRejoin || localJoinMs < 0) return false;
            var joinAgeMs = Clock.ElapsedMilliseconds - localJoinMs;
            return joinAgeMs >= 0 && joinAgeMs < windowMs;
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
            // Preserve the most important existing evidence first.
            // Even if an extended diagnostic snapshot ever fails later,
            // the raw Hazel disconnect payload must already be recorded.
            var hazelReason = e == null ? "NULL" : Sanitize(e.Reason);
            var message = e == null ? null : e.Message;

            Mark("ON_DISCONNECT_RAW",
                $"HazelReason={hazelReason} SenderNull={sender == null} " +
                MessageSnapshot(message));

            var now = Clock.ElapsedMilliseconds;
            var age = localJoinMs < 0
                ? -1
                : now - localJoinMs;

            // HACKING_15S_WINDOW_DIAGNOSTIC_V2
            // Exact state at the instant Hazel reports the disconnect.
            Mark(
                "SESSION_WINDOW_DISCONNECT_SNAPSHOT",
                $"ActualAgeMs={age} " +
                $"SameGameIdRejoin={sameGameIdRejoin} " +
                $"PlayerVersionCount={Main.playerVersion.Count} " +
                $"TrackedRemoteJoinCount={RemoteJoinMs.Count} " +
                $"OutCalls={windowOutboundCallCount} " +
                $"OutBytes={windowOutboundBytes} " +
                $"LastOutTms={windowLastOutboundTms} " +
                $"LastOutAgeMs={(windowLastOutboundTms < 0 ? -1 : now - windowLastOutboundTms)} " +
                $"LastOutLen={windowLastOutboundLength} " +
                $"LastOutOpt={windowLastOutboundSendOption} " +
                $"InCalls={windowInboundCallCount} " +
                $"InBytes={windowInboundBytes} " +
                $"LastInTms={windowLastInboundTms} " +
                $"LastInAgeMs={(windowLastInboundTms < 0 ? -1 : now - windowLastInboundTms)} " +
                $"LastInLen={windowLastInboundLength} " +
                $"LastInOpt={windowLastInboundSendOption} " +
                ClientSnapshot(AmongUsClient.Instance));
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

        // HACKING_15S_WINDOW_DIAGNOSTIC_V2
        internal static void MarkWindowOutbound(MessageWriter writer)
        {
            if (localJoinMs < 0 || writer == null)
            {
                return;
            }

            var now = Clock.ElapsedMilliseconds;
            var age = now - localJoinMs;

            // Cause investigation is limited to the observed +15s boundary.
            if (age < 0 || age > 17000)
            {
                return;
            }

            var length = writer.Length;

            windowOutboundCallCount++;
            windowOutboundBytes += length;
            windowLastOutboundTms = now;
            windowLastOutboundLength = length;
            windowLastOutboundSendOption = writer.SendOption.ToString();
        }

        internal static void MarkWindowInbound(
            MessageReader reader,
            SendOption sendOption)
        {
            if (localJoinMs < 0 || reader == null)
            {
                return;
            }

            var now = Clock.ElapsedMilliseconds;
            var age = now - localJoinMs;

            if (age < 0 || age > 17000)
            {
                return;
            }

            var length = reader.Length;

            windowInboundCallCount++;
            windowInboundBytes += length;
            windowLastInboundTms = now;
            windowLastInboundLength = length;
            windowLastInboundSendOption = sendOption.ToString();
        }

        internal static void TickWindowDiagnostic(InnerNetClient innerClient)
        {
            if (localJoinMs < 0)
            {
                return;
            }

            var now = Clock.ElapsedMilliseconds;
            var age = now - localJoinMs;

            if (age < 0 || age > 17000)
            {
                return;
            }

            var client = AmongUsClient.Instance;
            if (client == null)
            {
                return;
            }

            for (var i = 0; i < WindowCheckpointMs.Length; i++)
            {
                var bit = 1 << i;

                if ((windowCheckpointMask & bit) != 0 ||
                    age < WindowCheckpointMs[i])
                {
                    continue;
                }

                windowCheckpointMask |= bit;

                var allClientCount = 0;
                var characterCount = 0;
                var localClientEntryCount = 0;
                var hostClientEntryCount = 0;

                try
                {
                    foreach (ClientData data in client.allClients)
                    {
                        if (data == null)
                        {
                            continue;
                        }

                        allClientCount++;

                        if (data.Character != null)
                        {
                            characterCount++;
                        }

                        if (data.Id == client.ClientId)
                        {
                            localClientEntryCount++;
                        }

                        if (data.Id == client.HostId)
                        {
                            hostClientEntryCount++;
                        }
                    }
                }
                catch
                {
                    // Diagnostic-only enumeration must never affect networking/gameplay.
                }

                Mark(
                    "SESSION_WINDOW_CHECKPOINT",
                    $"TargetMs={WindowCheckpointMs[i]} ActualAgeMs={age} " +
                    $"SameGameIdRejoin={sameGameIdRejoin} " +
                    $"PlayerVersionCount={Main.playerVersion.Count} " +
                    $"TrackedRemoteJoinCount={RemoteJoinMs.Count} " +
                    $"AllClients={allClientCount} Characters={characterCount} " +
                    $"LocalEntryCount={localClientEntryCount} " +
                    $"HostEntryCount={hostClientEntryCount} " +
                    $"OutCalls={windowOutboundCallCount} " +
                    $"OutBytes={windowOutboundBytes} " +
                    $"LastOutTms={windowLastOutboundTms} " +
                    $"LastOutLen={windowLastOutboundLength} " +
                    $"LastOutOpt={windowLastOutboundSendOption} " +
                    $"InCalls={windowInboundCallCount} " +
                    $"InBytes={windowInboundBytes} " +
                    $"LastInTms={windowLastInboundTms} " +
                    $"LastInLen={windowLastInboundLength} " +
                    $"LastInOpt={windowLastInboundSendOption} " +
                    ClientSnapshot(client));
            }
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

    // HACKING_15S_WINDOW_DIAGNOSTIC_V2
    // Observe only. Never read payload bodies or alter send/receive behavior.
    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.SendOrDisconnect))]
    internal static class HackingSessionWindowOutboundPatch
    {
        public static void Prefix(
            InnerNetClient __instance,
            [HarmonyArgument(0)] MessageWriter msg)
        {
            HackingSessionEpochDiagnostic.Safe(() =>
                HackingSessionEpochDiagnostic.MarkWindowOutbound(msg));
        }
    }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.HandleMessage))]
    internal static class HackingSessionWindowInboundPatch
    {
        public static void Prefix(
            InnerNetClient __instance,
            [HarmonyArgument(0)] MessageReader reader,
            [HarmonyArgument(1)] SendOption sendOption)
        {
            HackingSessionEpochDiagnostic.Safe(() =>
                HackingSessionEpochDiagnostic.MarkWindowInbound(
                    reader,
                    sendOption));
        }
    }

    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.FixedUpdate))]
    internal static class HackingSessionWindowFixedUpdatePatch
    {
        public static void Postfix(InnerNetClient __instance)
        {
            HackingSessionEpochDiagnostic.Safe(() =>
                HackingSessionEpochDiagnostic.TickWindowDiagnostic(__instance));
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
