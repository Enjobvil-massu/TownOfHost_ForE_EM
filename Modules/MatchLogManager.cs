using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace TownOfHostForE.Modules;

public static class MatchLogManager
{
    private static readonly object Sync = new();
    private static readonly MatchLogListener Listener = new();

    private static StreamWriter? writer;
    private static Timer? flushTimer;
    private static Timer? endTimer;
    private static bool listenerRegistered;
    private static int matchSequence;

    private const int FlushIntervalMs = 500;
    private const int EndDelayMs = 5000;
    private const int MaxRetainedMatchLogs = 50;

    public static void BeginMatch()
    {
        try
        {
            if (
                AmongUsClient.Instance == null ||
                !AmongUsClient.Instance.AmHost)
            {
                return;
            }

            lock (Sync)
            {
                CloseWriterLocked();

                var directory =
                    Path.Combine(
                        ResolveBepInExRoot(),
                        "MatchLogs");

                Directory.CreateDirectory(
                    directory);

                TrimOldMatchLogsBeforeCreate(
                    directory);

                matchSequence++;

                var startedAt =
                    DateTime.Now;

                var fileName =
                    "TOH4E_" +
                    startedAt.ToString(
                        "yyyyMMdd_HHmmss_fff") +
                    "_match" +
                    matchSequence.ToString("000") +
                    ".log";

                var path =
                    Path.Combine(
                        directory,
                        fileName);

                var stream =
                    new FileStream(
                        path,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        64 * 1024,
                        FileOptions.SequentialScan);

                writer =
                    new StreamWriter(
                        stream,
                        new UTF8Encoding(false))
                    {
                        NewLine = "\r\n",
                    };

                writer.WriteLine(
                    "# TOH4E MatchLog");
                writer.WriteLine(
                    "# StartedLocal=" +
                    startedAt.ToString(
                        "yyyy-MM-dd HH:mm:ss.fff"));
                writer.WriteLine(
                    "# AssemblyVersion=" +
                    (
                        typeof(MatchLogManager)
                            .Assembly
                            .GetName()
                            .Version?
                            .ToString()
                        ?? "unknown"
                    ));
                writer.WriteLine(
                    "# Host=True");
                writer.Flush();

                if (!listenerRegistered)
                {
                    BepInEx.Logging.Logger
                        .Listeners
                        .Add(Listener);

                    listenerRegistered = true;
                }

                flushTimer =
                    new Timer(
                        _ => FlushCurrent(),
                        null,
                        FlushIntervalMs,
                        FlushIntervalMs);
            }
        }
        catch
        {
            SafeCloseAfterFailure();
        }
    }

    public static void ScheduleEndMatch()
    {
        try
        {
            lock (Sync)
            {
                if (writer == null)
                {
                    return;
                }

                endTimer?.Dispose();

                endTimer =
                    new Timer(
                        _ => EndMatch(),
                        null,
                        EndDelayMs,
                        Timeout.Infinite);
            }
        }
        catch
        {
            // Logging must never affect gameplay.
        }
    }

    public static void EndMatch()
    {
        try
        {
            lock (Sync)
            {
                CloseWriterLocked();
            }
        }
        catch
        {
            // Logging must never affect gameplay.
        }
    }

    private static void FlushCurrent()
    {
        try
        {
            lock (Sync)
            {
                writer?.Flush();
            }
        }
        catch
        {
            // Logging must never affect gameplay.
        }
    }

    private static void SafeCloseAfterFailure()
    {
        try
        {
            lock (Sync)
            {
                CloseWriterLocked();
            }
        }
        catch
        {
            // Logging must never affect gameplay.
        }
    }

    private static void CloseWriterLocked()
    {
        try
        {
            flushTimer?.Dispose();
        }
        catch
        {
        }

        flushTimer = null;

        try
        {
            endTimer?.Dispose();
        }
        catch
        {
        }

        endTimer = null;

        try
        {
            writer?.Flush();
        }
        catch
        {
        }

        try
        {
            writer?.Dispose();
        }
        catch
        {
        }

        writer = null;
    }

    private static void TrimOldMatchLogsBeforeCreate(
        string directory)
    {
        try
        {
            var candidates =
                new List<(string Path, DateTime StartedAt, int Ordinal, string FileName)>();

            foreach (
                var path in Directory.EnumerateFiles(
                    directory,
                    "TOH4E_*.log",
                    SearchOption.TopDirectoryOnly))
            {
                var fileName =
                    Path.GetFileName(
                        path);

                if (
                    !TryParseOwnedMatchLogFileName(
                        fileName,
                        out var startedAt,
                        out var ordinal))
                {
                    continue;
                }

                candidates.Add(
                    (
                        path,
                        startedAt,
                        ordinal,
                        fileName
                    ));
            }

            candidates.Sort(
                (left, right) =>
                {
                    var timestampCompare =
                        left.StartedAt.CompareTo(
                            right.StartedAt);

                    if (timestampCompare != 0)
                    {
                        return timestampCompare;
                    }

                    var ordinalCompare =
                        left.Ordinal.CompareTo(
                            right.Ordinal);

                    if (ordinalCompare != 0)
                    {
                        return ordinalCompare;
                    }

                    return string.CompareOrdinal(
                        left.FileName,
                        right.FileName);
                });

            var deleteCount =
                Math.Max(
                    0,
                    candidates.Count -
                    (MaxRetainedMatchLogs - 1));

            for (
                var index = 0;
                index < deleteCount;
                index++)
            {
                try
                {
                    File.Delete(
                        candidates[index].Path);
                }
                catch
                {
                    // Retention failure must never affect gameplay.
                    // A failed deletion may temporarily leave more than 50 logs.
                }
            }
        }
        catch
        {
            // Enumeration/parse failures must never affect gameplay.
        }
    }

    private static bool TryParseOwnedMatchLogFileName(
        string fileName,
        out DateTime startedAt,
        out int ordinal)
    {
        startedAt = default;
        ordinal = 0;

        const string prefix = "TOH4E_";
        const string matchMarker = "_match";
        const string extension = ".log";
        const int timestampLength = 19;
        const int ordinalLength = 3;

        if (
            string.IsNullOrEmpty(
                fileName) ||
            !fileName.StartsWith(
                prefix,
                StringComparison.Ordinal) ||
            !fileName.EndsWith(
                extension,
                StringComparison.Ordinal))
        {
            return false;
        }

        var expectedLength =
            prefix.Length +
            timestampLength +
            matchMarker.Length +
            ordinalLength +
            extension.Length;

        if (fileName.Length != expectedLength)
        {
            return false;
        }

        var timestampText =
            fileName.Substring(
                prefix.Length,
                timestampLength);

        var markerStart =
            prefix.Length +
            timestampLength;

        if (
            string.CompareOrdinal(
                fileName,
                markerStart,
                matchMarker,
                0,
                matchMarker.Length) != 0)
        {
            return false;
        }

        var ordinalStart =
            markerStart +
            matchMarker.Length;

        var ordinalText =
            fileName.Substring(
                ordinalStart,
                ordinalLength);

        return
            DateTime.TryParseExact(
                timestampText,
                "yyyyMMdd_HHmmss_fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out startedAt) &&
            int.TryParse(
                ordinalText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ordinal);
    }

    private static string ResolveBepInExRoot()
    {
        var assemblyDirectory =
            Path.GetDirectoryName(
                typeof(MatchLogManager)
                    .Assembly
                    .Location);

        if (string.IsNullOrEmpty(
            assemblyDirectory))
        {
            return Environment.CurrentDirectory;
        }

        return
            Directory.GetParent(
                assemblyDirectory)
                ?.FullName
            ?? assemblyDirectory;
    }

    private sealed class MatchLogListener
        : ILogListener
    {
        public LogLevel LogLevelFilter =>
            LogLevel.Fatal |
            LogLevel.Error |
            LogLevel.Warning |
            LogLevel.Message |
            LogLevel.Info;

        public void Dispose()
        {
            // Static listener lifetime follows the plugin process.
            // Writer/timers are owned by MatchLogManager.
        }

        public void LogEvent(
            object sender,
            LogEventArgs eventArgs)
        {
            try
            {
                lock (Sync)
                {
                    if (writer == null)
                    {
                        return;
                    }

                    writer.Write(
                        DateTime.Now.ToString(
                            "yyyy-MM-dd HH:mm:ss.fff"));
                    writer.Write(" ");
                    writer.WriteLine(
                        eventArgs.ToStringLine());
                }
            }
            catch
            {
                // Never call Logger here:
                // doing so would recurse into this listener.
            }
        }
    }
}
