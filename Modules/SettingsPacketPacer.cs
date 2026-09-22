using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Hazel;
using UnityEngine;

namespace TownOfHostForE.Modules;

/// <summary>
/// Paces only SyncCustomSettings reliable roots. Payload and recipient semantics
/// remain identical to the former direct-send implementation.
/// </summary>
internal static class SettingsPacketPacer
{
    internal const int MaxOptionsPerSyncPacket = 150;
    internal const float MinReliableSettingsRootInterval = 0.025f;
    internal const float HardWatchdogSeconds = 12f;

    private const string LogLabel = "PACKET_PACER_AB_V1";

    private sealed class Generation
    {
        internal int Id { get; }
        internal string Origin { get; }
        internal int[] Snapshot { get; }
        internal string SnapshotHash { get; }
        internal List<int> Targets { get; }
        internal int ChunkCount { get; }
        internal int RootCount => Targets.Count * ChunkCount;
        internal int RootsSent { get; set; }
        internal bool Aborted { get; set; }

        internal Generation(
            int id,
            string origin,
            int[] snapshot,
            string snapshotHash,
            List<int> targets)
        {
            Id = id;
            Origin = origin;
            Snapshot = snapshot;
            SnapshotHash = snapshotHash;
            Targets = targets;
            ChunkCount =
                (snapshot.Length + MaxOptionsPerSyncPacket - 1) /
                MaxOptionsPerSyncPacket;
        }
    }

    private static readonly Queue<Generation> PendingGenerations = new();
    private static Generation? currentGeneration;
    private static bool drainRunning;
    private static int nextGenerationId;
    private static int cancellationEpoch;
    private static bool hasPreviousSend;
    private static float previousSendRealtime;

    internal static bool IsBusy =>
        drainRunning ||
        currentGeneration != null ||
        PendingGenerations.Count > 0;

    internal static int Enqueue(int targetId, string origin)
    {
        var client = AmongUsClient.Instance;
        var localPlayer = PlayerControl.LocalPlayer;
        if (client == null ||
            !client.AmHost ||
            localPlayer == null ||
            PlayerControl.AllPlayerControls.Count <= 1)
        {
            return 0;
        }

        var snapshot = OptionItem.AllOptions
            .Select(option => option.GetValue())
            .ToArray();
        var snapshotHash = ComputeSnapshotHash(snapshot);
        var requestedTargets = ResolveTargets(client, targetId);
        var queuedTargets = new List<int>(requestedTargets.Count);

        foreach (var target in requestedTargets)
        {
            if (TryFindDuplicate(snapshotHash, target, out var oldOrigin))
            {
                Logger.Info(
                    $"PACKET_PACER_COALESCE OldOrigin={oldOrigin} " +
                    $"NewOrigin={origin} Target={target} " +
                    $"SnapshotHash={snapshotHash}",
                    LogLabel);
                continue;
            }

            queuedTargets.Add(target);
        }

        var generationId = ++nextGenerationId;
        if (queuedTargets.Count == 0)
        {
            if (requestedTargets.Count == 0)
            {
                Logger.Info(
                    $"PACKET_PACER_GEN_BEGIN GenerationId={generationId} " +
                    $"Origin={origin} SnapshotHash={snapshotHash} " +
                    $"Settings={snapshot.Length} TargetCount=0 RootCount=0",
                    LogLabel);
                Logger.Info(
                    $"PACKET_PACER_GEN_COMPLETE GenerationId={generationId} " +
                    "RootsSent=0 ElapsedMs=0 PeakRoots100ms=0 PeakRoots1s=0",
                    LogLabel);
            }

            return 0;
        }

        var generation = new Generation(
            generationId,
            origin,
            snapshot,
            snapshotHash,
            queuedTargets);

        Logger.Info(
            $"PACKET_PACER_GEN_BEGIN GenerationId={generation.Id} " +
            $"Origin={generation.Origin} SnapshotHash={generation.SnapshotHash} " +
            $"Settings={generation.Snapshot.Length} " +
            $"TargetCount={generation.Targets.Count} RootCount={generation.RootCount}",
            LogLabel);

        var sequence = 0;
        for (var chunk = 0; chunk < generation.ChunkCount; chunk++)
        {
            foreach (var target in generation.Targets)
            {
                Logger.Info(
                    $"PACKET_PACER_QUEUE GenerationId={generation.Id} " +
                    $"Target={target} Chunk={chunk} Sequence={sequence}",
                    LogLabel);
                sequence++;
            }
        }

        PendingGenerations.Enqueue(generation);
        EnsureDrainStarted(client);
        return queuedTargets.Count;
    }

    internal static IEnumerator WaitForDrain(float timeoutSeconds)
    {
        Logger.Info(
            $"SETTINGS_SYNC_START Busy={IsBusy} " +
            $"PendingGenerations={PendingGenerations.Count}",
            LogLabel);

        var elapsedSeconds = 0f;
        var timedOut = false;
        while (IsBusy)
        {
            if (!timedOut && elapsedSeconds >= timeoutSeconds)
            {
                timedOut = true;
                AbortAll("StartPhaseBarrierWatchdog");
            }

            elapsedSeconds += Time.unscaledDeltaTime;
            yield return null;
        }

        Logger.Info(
            $"SETTINGS_SYNC_COMPLETE TimedOut={timedOut} " +
            $"ElapsedMs={(long)(elapsedSeconds * 1000f)}",
            LogLabel);
    }

    private static List<int> ResolveTargets(
        AmongUsClient client,
        int targetId)
    {
        if (targetId >= 0)
        {
            if (targetId == client.ClientId)
            {
                return new List<int>();
            }

            var targetClient = client.allClients.ToArray()
                .FirstOrDefault(data => data != null && data.Id == targetId);

            if (targetClient?.Character == null ||
                !RPC.IsCompatibleVersion(targetClient.Character.PlayerId))
            {
                return new List<int>();
            }

            return new List<int> { targetId };
        }

        return client.allClients.ToArray()
            .Where(data =>
                data?.Character != null &&
                data.Id != client.ClientId &&
                RPC.IsCompatibleVersion(data.Character.PlayerId))
            .Select(data => data.Id)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    private static bool TryFindDuplicate(
        string snapshotHash,
        int target,
        out string oldOrigin)
    {
        if (currentGeneration != null &&
            !currentGeneration.Aborted &&
            currentGeneration.SnapshotHash == snapshotHash &&
            currentGeneration.Targets.Contains(target))
        {
            oldOrigin = currentGeneration.Origin;
            return true;
        }

        foreach (var generation in PendingGenerations)
        {
            if (!generation.Aborted &&
                generation.SnapshotHash == snapshotHash &&
                generation.Targets.Contains(target))
            {
                oldOrigin = generation.Origin;
                return true;
            }
        }

        oldOrigin = string.Empty;
        return false;
    }

    private static void EnsureDrainStarted(AmongUsClient client)
    {
        if (drainRunning || PendingGenerations.Count == 0) return;

        drainRunning = true;
        client.StartCoroutine(DrainQueue().WrapToIl2Cpp());
    }

    private static IEnumerator DrainQueue()
    {
        try
        {
            while (PendingGenerations.Count > 0)
            {
                var generation = PendingGenerations.Dequeue();
                currentGeneration = generation;
                var generationEpoch = cancellationEpoch;
                var clock = Stopwatch.StartNew();
                var sendTimes = new List<long>(generation.RootCount);
                var peakRoots100ms = 0;
                var peakRoots1s = 0;

                for (var chunk = 0;
                     chunk < generation.ChunkCount && !generation.Aborted;
                     chunk++)
                {
                    var startIndex = chunk * MaxOptionsPerSyncPacket;
                    var optionCount = Math.Min(
                        MaxOptionsPerSyncPacket,
                        generation.Snapshot.Length - startIndex);

                    for (var targetIndex = 0;
                         targetIndex < generation.Targets.Count && !generation.Aborted;
                         targetIndex++)
                    {
                        if (generationEpoch != cancellationEpoch)
                        {
                            generation.Aborted = true;
                            break;
                        }

                        if (clock.Elapsed.TotalSeconds >= HardWatchdogSeconds)
                        {
                            AbortAll("GenerationWatchdog");
                            generation.Aborted = true;
                            break;
                        }

                        if (hasPreviousSend)
                        {
                            var sincePrevious =
                                Time.realtimeSinceStartup - previousSendRealtime;
                            var remaining =
                                MinReliableSettingsRootInterval - sincePrevious;
                            if (remaining > 0f)
                            {
                                yield return new WaitForSecondsRealtime(remaining);
                            }
                        }

                        if (generationEpoch != cancellationEpoch)
                        {
                            generation.Aborted = true;
                            break;
                        }

                        if (clock.Elapsed.TotalSeconds >= HardWatchdogSeconds)
                        {
                            AbortAll("GenerationWatchdog");
                            generation.Aborted = true;
                            break;
                        }

                        var sendRealtime = Time.realtimeSinceStartup;
                        var deltaMs = hasPreviousSend
                            ? (sendRealtime - previousSendRealtime) * 1000f
                            : -1f;
                        var sequence =
                            chunk * generation.Targets.Count + targetIndex;
                        var targetClientId = generation.Targets[targetIndex];
                        var rootLength = 0;

                        try
                        {
                            rootLength = SendRoot(
                                generation,
                                targetClientId,
                                startIndex,
                                optionCount);
                        }
                        catch (Exception error)
                        {
                            Logger.Exception(error, LogLabel);
                            AbortAll("SendException");
                            generation.Aborted = true;
                            break;
                        }

                        previousSendRealtime = sendRealtime;
                        hasPreviousSend = true;
                        generation.RootsSent++;

                        var elapsedMs = clock.ElapsedMilliseconds;
                        sendTimes.Add(elapsedMs);
                        peakRoots100ms = Math.Max(
                            peakRoots100ms,
                            CountRecent(sendTimes, elapsedMs, 100));
                        peakRoots1s = Math.Max(
                            peakRoots1s,
                            CountRecent(sendTimes, elapsedMs, 1000));

                        Logger.Info(
                            $"PACKET_PACER_SEND GenerationId={generation.Id} " +
                            $"Sequence={sequence} TargetClientId={targetClientId} " +
                            $"Chunk={chunk} RootLength={rootLength} " +
                            $"DeltaMsFromPreviousSend={deltaMs:F1} " +
                            $"ElapsedMs={elapsedMs}",
                            LogLabel);
                    }
                }

                if (!generation.Aborted)
                {
                    Logger.Info(
                        $"PACKET_PACER_GEN_COMPLETE GenerationId={generation.Id} " +
                        $"RootsSent={generation.RootsSent} " +
                        $"ElapsedMs={clock.ElapsedMilliseconds} " +
                        $"PeakRoots100ms={peakRoots100ms} " +
                        $"PeakRoots1s={peakRoots1s}",
                        LogLabel);
                }

                currentGeneration = null;
            }
        }
        finally
        {
            currentGeneration = null;
            drainRunning = false;

            if (PendingGenerations.Count > 0 && AmongUsClient.Instance != null)
            {
                EnsureDrainStarted(AmongUsClient.Instance);
            }
        }
    }

    private static int SendRoot(
        Generation generation,
        int targetClientId,
        int startIndex,
        int optionCount)
    {
        var client = AmongUsClient.Instance ??
            throw new InvalidOperationException("AmongUsClient is unavailable.");
        var localPlayer = PlayerControl.LocalPlayer ??
            throw new InvalidOperationException("LocalPlayer is unavailable.");

        MessageWriter writer = client.StartRpcImmediately(
            localPlayer.NetId,
            (byte)CustomRPC.SyncCustomSettings,
            SendOption.Reliable,
            targetClientId);

        writer.WritePacked(startIndex);
        writer.WritePacked(optionCount);

        for (var index = 0; index < optionCount; index++)
        {
            writer.WritePacked(generation.Snapshot[startIndex + index]);
        }

        var rootLength = writer.Length;
        client.FinishRpcImmediately(writer);
        return rootLength;
    }

    private static int CountRecent(
        List<long> sendTimes,
        long nowMs,
        long windowMs)
    {
        var count = 0;
        for (var index = sendTimes.Count - 1; index >= 0; index--)
        {
            if (nowMs - sendTimes[index] >= windowMs) break;
            count++;
        }
        return count;
    }

    private static void AbortAll(string reason)
    {
        var activeId = currentGeneration?.Id ?? -1;
        var rootsSent = currentGeneration?.RootsSent ?? 0;
        var activeRoots = currentGeneration?.RootCount ?? 0;
        var queuedRoots = PendingGenerations.Sum(generation => generation.RootCount);
        var targets = currentGeneration == null
            ? "NONE"
            : string.Join(",", currentGeneration.Targets);

        Logger.Error(
            $"PACKET_PACER_TIMEOUT GenerationId={activeId} Reason={reason} " +
            $"RootsSent={rootsSent} UnsentRoots={activeRoots - rootsSent + queuedRoots} " +
            $"Targets={targets} PendingGenerations={PendingGenerations.Count} " +
            "FallbackBurst=False",
            LogLabel);

        cancellationEpoch++;
        if (currentGeneration != null)
        {
            currentGeneration.Aborted = true;
        }
        PendingGenerations.Clear();
    }

    private static string ComputeSnapshotHash(int[] snapshot)
    {
        var bytes = new byte[snapshot.Length * sizeof(int)];
        for (var index = 0; index < snapshot.Length; index++)
        {
            var value = snapshot[index];
            var offset = index * sizeof(int);
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(bytes));
    }
}
