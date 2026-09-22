using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TownOfHostForE.Modules;
using TownOfHostForE.Roles.Core;

namespace TownOfHostForE.Roles.Impostor;

/// <summary>
/// EvilHacker の文字ミニマップ用固定配置と snapshot 生成。
/// 座標を通信せず、ホストの実 game state を AdminProvider から毎回取得する。
/// </summary>
internal static class EvilHackerTextMinimap
{
    internal const float RefreshIntervalSeconds = 0.35f;
    internal const int LogicalCellWidth = 18;
    internal const int Columns = 4;

    private const string SelfMarker = "<size=105%><color=#000000>●</color></size>";
    private const string TeammateMarkerStart = "<size=105%><color=#ff0000>";
    private const string TeammateMarkerEnd = "</color></size>";

    // EVILHACKER_SIMPLEADMIN_UI_AB_V2:
    // Vanilla / Remote MOD / Host MOD で共通利用するLiveText prefix。
    internal const string LiveTextPrefix =
        "<size=62%><line-height=100%>";

    // EVILHACKER_VANILLA_SAFE_LIVETEXT_AB_V1:
    // Vanilla ownerへ送るSetNameはUTF-8実byte数を固定上限以下にする。
    internal const int VanillaSafeMaxUtf8Bytes = 220;
    internal const string VanillaSafeLiveTextPrefix =
        "<size=75%><color=#FFFFFF>";
    private const string VanillaSafeLiveTextSuffix =
        "</color></size>";

    // EVILHACKER_FIXED_POSITION_PREFIX_COMPAT_AB_V1:
    // Legacy LiveTextPrefix remains accepted.
    // The finalized 70% fixed-position renderer uses
    // VanillaSafeLiveTextPrefix, so Remote MOD local rendering
    // accepts that prefix as authoritative SimpleAdmin LiveText too.
    internal static bool IsRealtimeAdminLiveText(string? text) =>
        !string.IsNullOrEmpty(text) &&
        (
            text.StartsWith(
                LiveTextPrefix,
                StringComparison.Ordinal) ||
            text.StartsWith(
                VanillaSafeLiveTextPrefix,
                StringComparison.Ordinal)
        );
    internal sealed record AdminSnapshot(string Key, string LiveText, string VanillaLiveText, string ChatText);

    private readonly record struct RoomSlot(SystemTypes Room, int Row, int Column);
    private readonly record struct DisplayRoomSlot(SystemTypes Room, int Row, int Lane);
    private readonly record struct DisplayCell(string RichText, int VisibleWidth);
    private readonly record struct DisplayRoomLabel(
        string Text,
        int ScalePercent,
        int VisibleWidth);

    private readonly record struct DisplayMapProfile(
        int CanvasWidth,
        int LaneCount,
        int LineHeightPercent,
        string VerticalOffset);

    // EVILHACKER_SIMPLEADMIN_MAP_CANVAS_AB_V3_RETRY2:
    // 表示座標のauthorityをmanual Row/Columnから
    // ShipStatus.AllRoomsの実room-area中心座標へ昇格する。
    //
    // RoomSlotは実座標が取得できない異常ケースだけの
    // fail-open fallbackとして残す。
    private readonly record struct MapCanvasProfile(
        int CanvasWidth,
        int CanvasRows,
        int SidePadding);

    private readonly record struct RoomWorldPoint(
        float X,
        float Y);

    private readonly record struct PositionedCell(
        int Row,
        int DesiredColumn,
        DisplayCell Cell);

    private const int MinimumColumnGap = 6;

    private static MapCanvasProfile GetCanvasProfile(int mapId) =>
        mapId switch
        {
            // The Skeld
            0 => new(110, 11, 4),

            // MIRA HQ
            1 => new(112, 11, 4),

            // Polus
            2 => new(118, 12, 4),

            // Airshipは公式map比率に合わせて最も横長・縦長。
            4 => new(134, 14, 4),

            // The Fungle
            5 => new(126, 13, 4),

            _ => new(118, 12, 4),
        };

    private static Dictionary<SystemTypes, RoomWorldPoint>
        CaptureActualRoomCenters(
            IEnumerable<SystemTypes> roomIds)
    {
        var result =
            new Dictionary<SystemTypes, RoomWorldPoint>();

        var ship =
            ShipStatus.Instance;

        if (
            ship == null ||
            ship.AllRooms == null
        )
        {
            return result;
        }

        var wanted =
            roomIds.ToHashSet();

        foreach (var room in ship.AllRooms)
        {
            if (
                room == null ||
                !wanted.Contains(room.RoomId) ||
                room.RoomId is
                    SystemTypes.Hallway or
                    SystemTypes.Outside
            )
            {
                continue;
            }

            var position =
                room.roomArea
                    ? room.roomArea.bounds.center
                    : room.transform.position;

            result[room.RoomId] =
                new RoomWorldPoint(
                    position.x,
                    position.y);
        }

        return result;
    }

    private static int ProjectAxis(
        float value,
        float minimum,
        float maximum,
        int outputMinimum,
        int outputMaximum)
    {
        if (
            outputMaximum <= outputMinimum ||
            Math.Abs(maximum - minimum) < 0.001f
        )
        {
            return (
                outputMinimum +
                outputMaximum
            ) / 2;
        }

        var normalized =
            Math.Clamp(
                (value - minimum) /
                (maximum - minimum),
                0f,
                1f);

        return
            outputMinimum +
            (int)Math.Round(
                normalized *
                (
                    outputMaximum -
                    outputMinimum
                ));
    }

    // row/column は current v18 の部屋・spawn metadata に沿った固定論理配置。
    // AllRooms に存在しない部屋は AdminProvider に現れないため表示されず、未知の occupied room は末尾へ退避する。
    // EVILHACKER_SIMPLEADMIN_COMPACT_CENTERED_MAP_AB_V1:
    // Each map is curated independently to resemble its real top-down room
    // relationship while staying compact enough for Vanilla name rendering.
    // Columns remain 0..3 to preserve all legacy fail-open consumers.
    // EVILHACKER_SIMPLEADMIN_TRUE_CENTER_5MAP_AB_V1:
    // Every supported map uses the same 5-row / 7-column visual coordinate
    // system. Row=2 / Column=3 is the screen-space center around the player.

    // EVILHACKER_SIMPLEADMIN_REFERENCE_MAP_RELAYOUT_AB_V1:
    // User-supplied reference-map relayout.
    // Shared grid: rows 0..4 / columns 0..6.
    // Visual center is row 2 / column 3.
    private static readonly IReadOnlyDictionary<int, RoomSlot[]> Layouts =
        new Dictionary<int, RoomSlot[]>
        {
            // The Skeld
            [0] =
            [
                new(SystemTypes.UpperEngine, 0, 0),
                new(SystemTypes.Cafeteria, 0, 3),
                new(SystemTypes.Weapons, 0, 5),

                new(SystemTypes.Reactor, 1, 0),
                new(SystemTypes.Security, 1, 1),
                new(SystemTypes.MedBay, 1, 2),
                new(SystemTypes.LifeSupp, 1, 5),
                new(SystemTypes.Nav, 1, 6),

                new(SystemTypes.Electrical, 2, 2),
                new(SystemTypes.Admin, 2, 4),

                new(SystemTypes.LowerEngine, 3, 0),
                new(SystemTypes.Storage, 3, 3),
                new(SystemTypes.Shields, 3, 5),

                new(SystemTypes.Comms, 4, 4),
            ],

            // Polus
            [2] =
            [
                new(SystemTypes.Dropship, 0, 3),
                new(SystemTypes.Laboratory, 0, 6),

                new(SystemTypes.Electrical, 1, 0),
                new(SystemTypes.Security, 1, 1),
                new(SystemTypes.Storage, 1, 3),
                new(SystemTypes.Decontamination, 1, 5),

                new(SystemTypes.LifeSupp, 2, 0),
                new(SystemTypes.Comms, 2, 2),
                new(SystemTypes.Outside, 2, 3),
                new(SystemTypes.Office, 2, 4),

                new(SystemTypes.BoilerRoom, 3, 0),
                new(SystemTypes.Weapons, 3, 1),
                new(SystemTypes.Admin, 3, 4),
                new(SystemTypes.Decontamination2, 3, 5),

                new(SystemTypes.Specimens, 4, 6),
            ],

            // MIRA HQ
            [1] =
            [
                new(SystemTypes.Reactor, 0, 1),
                new(SystemTypes.Laboratory, 0, 2),
                new(SystemTypes.Greenhouse, 0, 6),

                new(SystemTypes.Decontamination, 1, 2),
                new(SystemTypes.Office, 1, 4),
                new(SystemTypes.Admin, 1, 6),

                new(SystemTypes.LockerRoom, 2, 2),
                new(SystemTypes.Cafeteria, 2, 5),

                new(SystemTypes.Launchpad, 3, 0),
                new(SystemTypes.MedBay, 3, 3),
                new(SystemTypes.Storage, 3, 4),
                new(SystemTypes.Comms, 3, 5),

                new(SystemTypes.Balcony, 4, 6),
            ],

            // The Airship
            // EVILHACKER_SIMPLEADMIN_AIRSHIP_MAINHALL_CENTER_AB_V1:
            // Main Hall is the visual center around the player's head.
            [4] =
            [
                // Top center
                new(SystemTypes.MeetingRoom, 0, 3),

                // Upper band
                new(SystemTypes.VaultRoom, 1, 0),
                new(SystemTypes.Brig, 1, 2),
                new(SystemTypes.GapRoom, 1, 3),
                new(SystemTypes.Records, 1, 4),
                new(SystemTypes.Lounge, 1, 6),

                // Center band
                new(SystemTypes.Cockpit, 2, 0),
                new(SystemTypes.Comms, 2, 1),
                new(SystemTypes.Engine, 2, 2),
                new(SystemTypes.MainHall, 2, 3),
                new(SystemTypes.Showers, 2, 5),
                new(SystemTypes.CargoBay, 2, 6),

                // Lower band
                new(SystemTypes.Armory, 3, 0),
                new(SystemTypes.Kitchen, 3, 1),
                new(SystemTypes.HallOfPortraits, 3, 2),
                new(SystemTypes.Security, 3, 3),
                new(SystemTypes.Electrical, 3, 4),
                new(SystemTypes.Ventilation, 3, 5),
                new(SystemTypes.Medical, 3, 6),

                // Lower-left anchor
                new(SystemTypes.ViewingDeck, 4, 0),
            ],

            // The Fungle
            [5] =
            [
                new(SystemTypes.Dropship, 0, 0),
                new(SystemTypes.Cafeteria, 0, 1),
                new(SystemTypes.Storage, 0, 2),
                new(SystemTypes.Lookout, 0, 3),
                new(SystemTypes.MiningPit, 0, 4),
                new(SystemTypes.UpperEngine, 0, 5),
                new(SystemTypes.Comms, 0, 6),

                new(SystemTypes.Beach, 1, 0),
                new(SystemTypes.SleepingQuarters, 1, 2),
                new(SystemTypes.Outside, 1, 3),
                new(SystemTypes.Zipline, 1, 5),

                new(SystemTypes.RecRoom, 2, 1),
                new(SystemTypes.MeetingRoom, 2, 3),
                new(SystemTypes.Greenhouse, 2, 4),
                new(SystemTypes.Highlands, 2, 5),
                new(SystemTypes.Reactor, 2, 6),

                new(SystemTypes.Kitchen, 3, 1),
                new(SystemTypes.Laboratory, 3, 2),
                new(SystemTypes.Jungle, 3, 3),

                new(SystemTypes.FishingDock, 4, 0),
                new(SystemTypes.Decontamination3, 4, 5),
            ],
        };

    // EVILHACKER_SIMPLEADMIN_REDUCED_5MAP_UI_AB_V1:
    // Display-only topology. Canonical Layouts above remains the authority for
    // chat ordering and every non-visual fallback. A lane is a fixed-width
    // horizontal slot; occupancy never changes any room's row or lane.
    private static readonly IReadOnlyDictionary<int, DisplayRoomSlot[]> DisplayLayouts =
        new Dictionary<int, DisplayRoomSlot[]>
        {
            // The Skeld: Admin is the compact visual center.
            [0] =
            [
                new(SystemTypes.UpperEngine, 0, 0),
                new(SystemTypes.Cafeteria, 0, 2),
                new(SystemTypes.Weapons, 0, 4),

                new(SystemTypes.Reactor, 1, 0),
                new(SystemTypes.Security, 1, 1),
                new(SystemTypes.MedBay, 1, 2),
                new(SystemTypes.LifeSupp, 1, 3),
                new(SystemTypes.Nav, 1, 4),

                new(SystemTypes.Electrical, 2, 1),
                new(SystemTypes.Admin, 2, 2),

                new(SystemTypes.LowerEngine, 3, 0),
                new(SystemTypes.Storage, 3, 2),
                new(SystemTypes.Shields, 3, 4),

                new(SystemTypes.Comms, 4, 3),
            ],

            // MIRA HQ: Decontamination anchors the two map branches.
            [1] =
            [
                new(SystemTypes.Greenhouse, 0, 3),

                new(SystemTypes.Reactor, 1, 0),
                new(SystemTypes.Laboratory, 1, 1),
                new(SystemTypes.Office, 1, 3),
                new(SystemTypes.Admin, 1, 4),

                new(SystemTypes.Decontamination, 2, 2),

                new(SystemTypes.Launchpad, 3, 0),
                new(SystemTypes.LockerRoom, 3, 1),
                new(SystemTypes.Comms, 3, 2),
                new(SystemTypes.Storage, 3, 3),
                new(SystemTypes.Cafeteria, 3, 4),

                new(SystemTypes.MedBay, 4, 2),
                new(SystemTypes.Balcony, 4, 4),
            ],

            // Polus: Outside is the physical central connection area.
            [2] =
            [
                new(SystemTypes.Dropship, 0, 2),

                new(SystemTypes.Security, 1, 0),
                new(SystemTypes.Electrical, 1, 1),
                new(SystemTypes.Storage, 1, 2),
                new(SystemTypes.Decontamination, 1, 3),
                new(SystemTypes.Laboratory, 1, 4),

                new(SystemTypes.LifeSupp, 2, 0),
                new(SystemTypes.Comms, 2, 1),
                new(SystemTypes.Outside, 2, 2),
                new(SystemTypes.Office, 2, 3),

                new(SystemTypes.BoilerRoom, 3, 0),
                new(SystemTypes.Weapons, 3, 1),
                new(SystemTypes.Admin, 3, 3),
                new(SystemTypes.Decontamination2, 3, 4),

                new(SystemTypes.Specimens, 4, 4),
            ],

            // The Airship: Main Hall owns the central main axis. Comms stays
            // left of Gap on the compact bridge band from the approved sketch.
            [4] =
            [
                new(SystemTypes.MeetingRoom, 0, 2),

                new(SystemTypes.VaultRoom, 1, 0),
                new(SystemTypes.Brig, 1, 1),
                new(SystemTypes.Records, 1, 3),
                new(SystemTypes.Lounge, 1, 4),

                new(SystemTypes.Comms, 2, 0),
                new(SystemTypes.GapRoom, 2, 2),

                new(SystemTypes.Cockpit, 3, 0),
                new(SystemTypes.Engine, 3, 1),
                new(SystemTypes.MainHall, 3, 2),
                new(SystemTypes.Showers, 3, 3),
                new(SystemTypes.CargoBay, 3, 4),

                new(SystemTypes.Armory, 4, 0),
                new(SystemTypes.Kitchen, 4, 1),
                new(SystemTypes.Security, 4, 2),
                new(SystemTypes.Electrical, 4, 3),
                new(SystemTypes.Medical, 4, 4),

                new(SystemTypes.ViewingDeck, 5, 0),
                new(SystemTypes.HallOfPortraits, 5, 2),
                new(SystemTypes.Ventilation, 5, 3),
            ],

            // The Fungle: Meeting Room is the compact center between the
            // western settlement and the eastern industrial/jungle branch.
            [5] =
            [
                new(SystemTypes.Dropship, 0, 1),
                new(SystemTypes.MiningPit, 0, 3),
                new(SystemTypes.Comms, 0, 4),

                new(SystemTypes.Cafeteria, 1, 0),
                new(SystemTypes.Storage, 1, 1),
                new(SystemTypes.Lookout, 1, 2),
                new(SystemTypes.Highlands, 1, 3),
                new(SystemTypes.UpperEngine, 1, 4),

                new(SystemTypes.Beach, 2, 0),
                new(SystemTypes.Outside, 2, 1),
                new(SystemTypes.MeetingRoom, 2, 2),
                new(SystemTypes.SleepingQuarters, 2, 3),
                new(SystemTypes.Zipline, 2, 4),

                new(SystemTypes.RecRoom, 3, 0),
                new(SystemTypes.Laboratory, 3, 1),
                new(SystemTypes.Jungle, 3, 2),
                new(SystemTypes.Reactor, 3, 4),

                new(SystemTypes.FishingDock, 4, 0),
                new(SystemTypes.Kitchen, 4, 1),
                new(SystemTypes.Greenhouse, 4, 3),
                new(SystemTypes.Decontamination3, 4, 4),
            ],
        };

    private static DisplayMapProfile GetDisplayProfile(int mapId) =>
        mapId switch
        {
            0 => new(48, 5, 135, "-1.2em"),
            1 => new(50, 5, 135, "-1.2em"),
            2 => new(52, 5, 135, "-1.2em"),
            4 => new(56, 5, 128, "-3.4em"),
            5 => new(54, 5, 135, "-1.2em"),
            _ => new(52, 5, 135, "-1.2em"),
        };

    internal static AdminSnapshot Capture(
        PlayerControl player,
        bool showImpostorTeammates,
        bool showDeadBodiesInMeetingChat)
    {
        var mapId = Main.NormalOptions.MapId;
        var commsBlocked = Utils.IsActive(SystemTypes.Comms);
        if (commsBlocked)
        {
            var blocked = Translator.GetString("EvilHackerCommsBlocked");
            return new($"{mapId}|COMMS", WrapLiveText(blocked), WrapVanillaSafeLiveText("ADMIN\n" + blocked), blocked);
        }

        var admins = AdminProvider.CalculateAdmin();
        var selfRoom = player.GetPlainShipRoom()?.RoomId;
        var teammates = showImpostorTeammates
            ? Main.AllPlayerControls
                .Where(teammate => teammate != null && teammate != player && teammate.Data != null &&
                    !teammate.Data.Disconnected && teammate.IsAlive() && teammate.Is(CustomRoleTypes.Impostor))
                .OrderBy(teammate => teammate.PlayerId)
                .ToArray()
            : Array.Empty<PlayerControl>();

        var teammateRooms = new Dictionary<SystemTypes, int>();
        foreach (var teammate in teammates)
        {
            var room = teammate.GetPlainShipRoom()?.RoomId;
            if (room is not { } roomId || roomId == SystemTypes.Hallway)
            {
                continue;
            }
            teammateRooms.TryGetValue(roomId, out var count);
            teammateRooms[roomId] = count + 1;
        }

        var key = BuildSnapshotKey(mapId, admins, selfRoom, teammates, showDeadBodiesInMeetingChat);
        var liveText = BuildLiveText(mapId, admins, selfRoom, teammateRooms);
        var vanillaLiveText = BuildVanillaSafeLiveText(mapId, admins, selfRoom, teammateRooms);
        var chatText = BuildChatText(mapId, admins, teammates, showDeadBodiesInMeetingChat);
        return new(key, liveText, vanillaLiveText, chatText);
    }

    private static string BuildSnapshotKey(
        int mapId,
        SortedDictionary<SystemTypes, AdminProvider.AdminEntry> admins,
        SystemTypes? selfRoom,
        IEnumerable<PlayerControl> teammates,
        bool showDeadBodiesInMeetingChat)
    {
        var key = new StringBuilder(256);
        key.Append(mapId).Append('|').Append((int?)selfRoom ?? -1).Append('|');
        foreach (var entry in admins)
        {
            key.Append((int)entry.Key).Append(':').Append(entry.Value.TotalPlayers);
            // EVILHACKER_SIMPLE_ADMIN_DEADBODY_INHERIT_AB_V2
            if (showDeadBodiesInMeetingChat)
            {
                key.Append('/').Append(entry.Value.NumDeadBodies);
            }
            key.Append(',');
        }
        key.Append('|');
        foreach (var teammate in teammates)
        {
            key.Append(teammate.PlayerId).Append(':')
                .Append((int?)(teammate.GetPlainShipRoom()?.RoomId) ?? -1).Append(',');
        }
        return key.ToString();
    }
        // EVILHACKER_SIMPLEADMIN_V274_ALLROOM_DEMO_AB_V1:
    // DISPLAY DEMO ONLY.
    // Every room in the current v6180.292 DisplayLayouts is rendered as:
    //   player count = 1
    //   self marker   = BLACK dot x1
    //   teammate      = RED dot x1
    // Real AdminProvider / real player locations are not written or changed.
        // EVILHACKER_SIMPLEADMIN_V274_ALLROOM_DEMO_AB_V1:
    // DISPLAY DEMO ONLY: every room remains 1 + BLACK + RED.
    //
    // EVILHACKER_SIMPLEADMIN_READABILITY_ROWS_AB_V1:
    // Keep v6180.292 DisplayLayouts as room-position authority.
    // Add display-only row separators for dense maps.
        // EVILHACKER_SIMPLEADMIN_V274_ALLROOM_DEMO_AB_V1:
    // DISPLAY DEMO ONLY: every room remains 1 + BLACK + RED.
    //
    // EVILHACKER_SIMPLEADMIN_READABILITY_ROWS_AB_V1:
    // Keep v6180.292 DisplayLayouts as room-position authority.
    // Add display-only row separators for dense maps.
        // EVILHACKER_SIMPLEADMIN_V274_ALLROOM_DEMO_AB_V1:
    // DISPLAY DEMO ONLY: every room remains 1 + BLACK + RED.
    //
    // EVILHACKER_SIMPLEADMIN_READABILITY_ROWS_AB_V1:
    // Existing room/lane authority remains unchanged.
    //
    // EVILHACKER_SIMPLEADMIN_CONTINUOUS_ROW_BAND_AB_V1:
    // Horizontal geometry remains unchanged in cell counts.
    // Only padding changes from ordinary SPACE to NBSP.
        // EVILHACKER_SIMPLEADMIN_REALDATA_FIXED_LAYOUT_AB_V1:
    // DEMO解除:
    // - AdminProviderの実人数 > 0 の部屋だけroom/count/markerを表示
    // - DisplayLayoutsは固定authorityとして維持
    // - 人が移動しても部屋anchorをrecenterしない
    // - unknown occupied roomは末尾fallbackへ退避
    //
    // Fungle:
    // - 空室を含むlayout背景は常時表示
    // - foregroundはmark外なので文字色へ背景alphaを乗算しない
    // EVILHACKER_SIMPLEADMIN_REALDATA_FIXED_POSITION_LOCAL_V1:
    // EVILHACKER_SIMPLEADMIN_REALDATA_FIXED_POSITION_ALLCLIENT_V1:
    //
    // Final five-map renderer:
    // - AdminProvider real occupancy only
    // - empty rooms are not rendered
    // - occupied rooms keep the finalized fixed map anchors
    // - disappearing rooms never cause recenter/repacking
    // - same LiveText is used by Host / Remote MOD / Remote Vanilla
    // - room naming follows the finalized local-demo map labels
    private static string BuildLiveText(
        int mapId,
        SortedDictionary<SystemTypes, AdminProvider.AdminEntry> admins,
        SystemTypes? selfRoom,
        IReadOnlyDictionary<SystemTypes, int> teammateRooms)
    {
        return
            WrapAllRoomPositionPayloadDemoBody(
                BuildFixedPositionRealDataBody(
                    mapId,
                    admins,
                    selfRoom,
                    teammateRooms));
    }
    private static string BuildDisplayLiveText(
        int mapId,
        IReadOnlyDictionary<SystemTypes, int> occupied,
        SystemTypes? selfRoom,
        IReadOnlyDictionary<SystemTypes, int> teammateRooms,
        DisplayRoomSlot[] topology)
    {
        const int sidePadding = 1;
        const int roomScalePercent = 60;
        const string occupiedBackground = "#252A30FF";

        var profile =
            GetDisplayProfile(mapId);

        var maximumRow =
            Math.Max(
                0,
                topology.Max(
                    slot => slot.Row));

        var rendered =
            new StringBuilder(
                (profile.CanvasWidth + 1) *
                (maximumRow + 1) *
                2);

        var usableWidth =
            profile.CanvasWidth -
            sidePadding * 2;

        for (
            var row = 0;
            row <= maximumRow;
            row++
        )
        {
            var cursor = 0;

            foreach (
                var slot in topology
                    .Where(
                        slot =>
                            slot.Row == row)
                    .OrderBy(
                        slot =>
                            slot.Lane)
            )
            {
                if (
                    !occupied.TryGetValue(
                        slot.Room,
                        out var count)
                )
                {
                    continue;
                }

                var laneLeft =
                    sidePadding +
                    (int)Math.Round(
                        slot.Lane *
                        usableWidth /
                        (double)profile.LaneCount);

                var laneRight =
                    sidePadding +
                    (int)Math.Round(
                        (slot.Lane + 1) *
                        usableWidth /
                        (double)profile.LaneCount);

                var laneWidth =
                    Math.Max(
                        1,
                        laneRight -
                        laneLeft);

                var roomLabel =
                    CreateDisplayRoomLabel(
                        slot.Room,
                        laneWidth,
                        roomScalePercent);

                var liveCell =
                    CreateCell(
                        slot.Room,
                        count,
                        selfRoom,
                        teammateRooms,
                        roomLabel.Text,
                        roomLabel.ScalePercent);

                var scaledVisibleWidth =
                    Math.Max(
                        1,
                        (int)Math.Ceiling(
                            liveCell.VisibleWidth *
                            roomScalePercent /
                            100.0));

                var laneCenter =
                    (laneLeft + laneRight) / 2;

                // The anchor width deliberately excludes live markers and
                // reserves two count digits. Label position therefore stays
                // fixed when count/marker content changes.
                var anchorVisibleWidth =
                    roomLabel.VisibleWidth +
                    1 +
                    2;

                var scaledAnchorWidth =
                    Math.Max(
                        1,
                        (int)Math.Ceiling(
                            anchorVisibleWidth *
                            roomScalePercent /
                            100.0));

                var cellStart =
                    Math.Max(
                        0,
                        laneCenter -
                        scaledAnchorWidth / 2);

                AppendAdminPanelFill(
                    rendered,
                    Math.Max(
                        0,
                        cellStart -
                        cursor));

                rendered
                    .Append("<size=")
                    .Append(roomScalePercent)
                    .Append("%><mark=")
                    .Append(occupiedBackground)
                    .Append(" padding=\"0.15,0.15,0.02,0.02\">")
                    .Append(liveCell.RichText)
                    .Append("</mark></size>");

                cursor =
                    Math.Max(
                        cursor,
                        cellStart +
                        scaledVisibleWidth);
            }

            AppendAdminPanelFill(
                rendered,
                Math.Max(
                    0,
                    profile.CanvasWidth -
                    cursor));

            if (row < maximumRow)
            {
                rendered.Append('\n');
            }
        }

        return WrapTopologyLiveText(
            mapId,
            "<line-height=" +
            profile.LineHeightPercent +
            "%>" +
            rendered.ToString() +
            "</line-height>");
    }

    private static DisplayRoomLabel CreateDisplayRoomLabel(
        SystemTypes room,
        int laneWidth,
        int roomScalePercent)
    {
        var roomName =
            DestroyableSingleton<TranslationController>
                .Instance
                .GetString(room);

        var rawLaneCapacity =
            Math.Max(
                8,
                (int)Math.Floor(
                    laneWidth *
                    100.0 /
                    roomScalePercent));

        // Space + two count digits + self dot + one teammate dot. This is a
        // fixed geometry budget and never depends on current occupancy.
        const int reservedSuffixWidth = 7;
        const int minimumNameScalePercent = 55;

        var availableNameWidth =
            Math.Max(
                1,
                rawLaneCapacity -
                reservedSuffixWidth);

        var roomNameWidth =
            Math.Max(
                1,
                GetDisplayWidth(roomName));

        var roomNameScalePercent =
            Math.Min(
                100,
                availableNameWidth *
                100 /
                roomNameWidth);

        if (roomNameScalePercent >= minimumNameScalePercent)
        {
            return new(
                roomName,
                roomNameScalePercent,
                Math.Max(
                    1,
                    (int)Math.Ceiling(
                        roomNameWidth *
                        roomNameScalePercent /
                        100.0)));
        }

        var shortName =
            GetSimpleAdminShortName(
                room,
                roomName);

        var shortNameWidth =
            Math.Max(
                1,
                GetDisplayWidth(shortName));

        var shortNameScalePercent =
            Math.Clamp(
                availableNameWidth *
                100 /
                shortNameWidth,
                minimumNameScalePercent,
                100);

        return new(
            shortName,
            shortNameScalePercent,
            Math.Max(
                1,
                (int)Math.Ceiling(
                    shortNameWidth *
                    shortNameScalePercent /
                    100.0)));
    }

    private static string GetSimpleAdminShortName(
        SystemTypes room,
        string roomName)
    {
        var isJapanese =
            roomName.Any(
                character =>
                    character is >= '\u3040' and <= '\u30ff');

        if (!isJapanese)
        {
            return roomName;
        }

        return room switch
        {
            SystemTypes.MeetingRoom => "ミーティング",
            SystemTypes.Security => "セキュリティ",
            SystemTypes.Engine => "エンジン",
            SystemTypes.Records => "アーカイブ",
            SystemTypes.HallOfPortraits => "肖像画ホール",
            SystemTypes.Ventilation => "換気室",
            _ => roomName,
        };
    }

    private static string BuildTopologyLiveText(
        int mapId,
        IReadOnlyDictionary<SystemTypes, int> occupied,
        SystemTypes? selfRoom,
        IReadOnlyDictionary<SystemTypes, int> teammateRooms,
        RoomSlot[] topology)
    {
        if (
            mapId is 0 or 1 or 2 or 4 or 5 &&
            topology.Length > 0
        )
        {
            return BuildFixedTopologyLiveText(
                mapId,
                occupied,
                selfRoom,
                teammateRooms,
                topology);
        }

        var canvasWidth =
            mapId switch
            {
                0 => 58,
                1 => 60,
                2 => 62,
                4 => 66,
                5 => 64,
                _ => 112,
            };

        const int sidePadding = 1;
        const int minimumGap = 1;
        const int topologyColumns = 7;
        const char layoutSpace = '\u00A0';

        var topologyByRoom =
            topology.ToDictionary(
                slot => slot.Room);

        var maximumRow =
            Math.Max(
                0,
                topology.Max(
                    slot => slot.Row));

        var positioned =
            new List<(
                int Row,
                int DesiredCenter,
                DisplayCell Cell)>();

        foreach (
            var entry in occupied
                .OrderBy(
                    entry =>
                        (int)entry.Key)
        )
        {
            if (
                !topologyByRoom.TryGetValue(
                    entry.Key,
                    out var slot)
            )
            {
                continue;
            }

            var usableWidth =
                canvasWidth -
                sidePadding * 2;

            var desiredCenter =
                sidePadding +
                (
                    topologyColumns <= 1
                        ? usableWidth / 2
                        : (int)Math.Round(
                            slot.Column *
                            usableWidth /
                            (double)(topologyColumns - 1))
                );

            positioned.Add(
                (
                    slot.Row,
                    desiredCenter,
                    CreateCell(
                        entry.Key,
                        entry.Value,
                        selfRoom,
                        teammateRooms)
                ));
        }

        var resolved =
            new List<(
                int Row,
                int Start,
                DisplayCell Cell)>(
                    positioned.Count);

        var canvasLeft =
            sidePadding;

        var canvasRight =
            canvasWidth -
            sidePadding;

        foreach (
            var rowGroup in positioned
                .GroupBy(
                    item => item.Row)
                .OrderBy(
                    group => group.Key)
        )
        {
            var items =
                rowGroup
                    .OrderBy(
                        item =>
                            item.DesiredCenter)
                    .ToArray();

            var starts =
                new int[items.Length];

            for (
                var index = 0;
                index < items.Length;
                index++
            )
            {
                var width =
                    items[index]
                        .Cell
                        .VisibleWidth;

                var maximumStart =
                    Math.Max(
                        canvasLeft,
                        canvasRight -
                        width);

                starts[index] =
                    Math.Clamp(
                        items[index]
                            .DesiredCenter -
                        width / 2,
                        canvasLeft,
                        maximumStart);

                if (index > 0)
                {
                    var minimumStart =
                        starts[index - 1] +
                        items[index - 1]
                            .Cell
                            .VisibleWidth +
                        minimumGap;

                    starts[index] =
                        Math.Max(
                            starts[index],
                            minimumStart);
                }
            }

            if (items.Length > 0)
            {
                var rowEnd =
                    starts[^1] +
                    items[^1]
                        .Cell
                        .VisibleWidth;

                if (rowEnd > canvasRight)
                {
                    var shift =
                        rowEnd -
                        canvasRight;

                    for (
                        var index = 0;
                        index < starts.Length;
                        index++
                    )
                    {
                        starts[index] -=
                            shift;
                    }
                }

                if (starts[0] < canvasLeft)
                {
                    var shift =
                        canvasLeft -
                        starts[0];

                    for (
                        var index = 0;
                        index < starts.Length;
                        index++
                    )
                    {
                        starts[index] +=
                            shift;
                    }
                }
            }

            for (
                var index = 0;
                index < items.Length;
                index++
            )
            {
                resolved.Add(
                    (
                        items[index].Row,
                        starts[index],
                        items[index].Cell
                    ));
            }
        }

        var rendered =
            new StringBuilder(
                (canvasWidth + 1) *
                (maximumRow + 1));

        for (
            var row = 0;
            row <= maximumRow;
            row++
        )
        {
            var cursor = 0;

            foreach (
                var item in resolved
                    .Where(
                        item =>
                            item.Row == row)
                    .OrderBy(
                        item =>
                            item.Start)
            )
            {
                var spaces =
                    Math.Max(
                        0,
                        item.Start -
                        cursor);

                rendered.Append(
                    layoutSpace,
                    spaces);

                rendered.Append(
                    item.Cell.RichText);

                cursor =
                    item.Start +
                    item.Cell.VisibleWidth;
            }

            var trailing =
                Math.Max(
                    0,
                    canvasWidth -
                    cursor);

            rendered.Append(
                layoutSpace,
                trailing);

            if (row < maximumRow)
            {
                rendered.Append('\n');
            }
        }

        return WrapTopologyLiveText(
            mapId,
            rendered.ToString());
    }


    // Outer wrapper deliberately has no mark/background. Supported maps add
    // one fully opaque mark around each occupied cell only.
    private static string WrapTopologyLiveText(
        int mapId,
        string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var mapOffset =
            GetDisplayProfile(mapId)
                .VerticalOffset;

        return
            $"{LiveTextPrefix}<align=center><voffset={mapOffset}><color=#FFFFFF>{text}</color><voffset=0em></align></line-height></size>";
    }

    // EVILHACKER_SIMPLEADMIN_ALLMAP_V283_FOOTPRINT_FIXED_ANCHOR_AB_V1:
    // The full panel footprint returns to the accepted v6180.283 size.
    // Room anchors depend only on RoomSlot row/column, never on occupancy,
    // room-name width, player count or markers.
    //
    // EVILHACKER_SIMPLEADMIN_INLINE_MARKERS_RIGHT_AB_V1:
    // Existing CreateCell keeps BLACK self / RED teammate dots immediately
    // to the right of the player count.
    //
    // EVILHACKER_SIMPLEADMIN_UNIFORM_FONT_VERTICAL_GAP_AB_V1:
    // Every room uses one common local scale. This keeps Engine Room,
    // Cargo Bay and every shorter room label visually identical in size.
    // Legacy fixed-topology fallback. Its background is also scoped to each
    // occupied cell so no row-wide mark can reappear on a fail-open path.
    private static string BuildFixedTopologyLiveText(
        int mapId,
        IReadOnlyDictionary<SystemTypes, int> occupied,
        SystemTypes? selfRoom,
        IReadOnlyDictionary<SystemTypes, int> teammateRooms,
        RoomSlot[] topology)
    {
        const int sidePadding = 1;
        const int topologyColumns = 7;
        const int uniformCellScalePercent = 60;

        var canvasWidth =
            mapId switch
            {
                0 => 52,
                1 => 54,
                2 => 56,
                4 => 56,
                5 => 58,
                _ => 56,
            };

        var maximumRow =
            Math.Max(
                0,
                topology.Max(
                    slot => slot.Row));

        var rendered =
            new StringBuilder(
                (canvasWidth + 1) *
                (maximumRow + 1) *
                2);

        var usableWidth =
            canvasWidth -
            sidePadding * 2;

        for (
            var row = 0;
            row <= maximumRow;
            row++
        )
        {
            var rowSlots =
                topology
                    .Where(
                        slot =>
                            slot.Row == row)
                    .OrderBy(
                        slot =>
                            slot.Column)
                    .ToArray();

            var centers =
                new int[rowSlots.Length];

            for (
                var index = 0;
                index < rowSlots.Length;
                index++
            )
            {
                centers[index] =
                    sidePadding +
                    (
                        topologyColumns <= 1
                            ? usableWidth / 2
                            : (int)Math.Round(
                                rowSlots[index].Column *
                                usableWidth /
                                (double)(topologyColumns - 1))
                    );
            }

            var cursor = 0;

            for (
                var index = 0;
                index < rowSlots.Length;
                index++
            )
            {
                var slot =
                    rowSlots[index];

                var center =
                    centers[index];

                var laneLeft =
                    index == 0
                        ? sidePadding
                        : (centers[index - 1] + center) / 2;

                var laneRight =
                    index == rowSlots.Length - 1
                        ? canvasWidth - sidePadding
                        : (center + centers[index + 1]) / 2;

                laneLeft =
                    Math.Clamp(
                        laneLeft,
                        sidePadding,
                        canvasWidth - sidePadding);

                laneRight =
                    Math.Clamp(
                        laneRight,
                        laneLeft + 1,
                        canvasWidth - sidePadding);

                var laneWidth =
                    Math.Max(
                        1,
                        laneRight -
                        laneLeft);

                AppendAdminPanelFill(
                    rendered,
                    Math.Max(
                        0,
                        laneLeft -
                        cursor));

                if (
                    occupied.TryGetValue(
                        slot.Room,
                        out var count)
                )
                {
                    var liveCell =
                        CreateCell(
                            slot.Room,
                            count,
                            selfRoom,
                            teammateRooms);

                    var scalePercent =
                        uniformCellScalePercent;

                    var scaledVisibleWidth =
                        Math.Max(
                            1,
                            (int)Math.Ceiling(
                                liveCell.VisibleWidth *
                                uniformCellScalePercent /
                                100.0));

                    var cellStart =
                        laneLeft +
                        Math.Max(
                            0,
                            (
                                laneWidth -
                                scaledVisibleWidth
                            ) / 2);

                    AppendAdminPanelFill(
                        rendered,
                        Math.Max(
                            0,
                            cellStart -
                            Math.Max(
                                cursor,
                                laneLeft)));

                    rendered
                        .Append(
                            "<size=")
                        .Append(
                            scalePercent)
                        .Append(
                            "%><mark=#252A30FF padding=\"0.15,0.15,0.02,0.02\">")
                        .Append(
                            liveCell.RichText)
                        .Append(
                            "</mark></size>");

                    cursor =
                        Math.Max(
                            cursor,
                            cellStart +
                            scaledVisibleWidth);
                }
                else
                {
                    AppendAdminPanelFill(
                        rendered,
                        Math.Max(
                            0,
                            laneRight -
                            Math.Max(
                                cursor,
                                laneLeft)));

                    cursor =
                        Math.Max(
                            cursor,
                            laneRight);
                }

                if (cursor < laneRight)
                {
                    AppendAdminPanelFill(
                        rendered,
                        laneRight -
                        cursor);

                    cursor =
                        laneRight;
                }
            }

            AppendAdminPanelFill(
                rendered,
                Math.Max(
                    0,
                    canvasWidth -
                    cursor));

            if (row < maximumRow)
            {
                rendered.Append('\n');
            }
        }

        // EVILHACKER_SIMPLEADMIN_TOPOLOGY_ROW_GAP_AB_V1:
        // Scope line-height=150% to the supported fixed topology payload.
        // WrapTopologyLiveText still owns map offset / outer 62% policy.
        return WrapTopologyLiveText(
            mapId,
            "<line-height=150%>" +
            rendered.ToString() +
            "</line-height>");
    }

    private static void AppendAdminPanelFill(
        StringBuilder builder,
        int count)
    {
        if (count <= 0)
        {
            return;
        }

        builder
            .Append("<color=#00000000>")
            .Append('•', count)
            .Append("</color>");
    }

    // ====================================================
    // v6180.274 exact visual authority - DEMO ONLY
    // ====================================================
private const string V274DemoSelfMarker = "<size=85%><color=#000000FF>●</color></size>";
private const string V274DemoTeammateMarkerStart = "<size=85%><color=#FF2020FF>";
private const string V274DemoTeammateMarkerEnd = "</color></size>";

    // EVILHACKER_SIMPLEADMIN_READABILITY_AB_V1:
    // room = pure white/bold; count = yellow/bold/135%; dots = 85%.
        // EVILHACKER_SIMPLEADMIN_READABILITY_AB_V1:
    // room = pure white/bold; count = yellow/bold/110%; dots = 85%.
        // EVILHACKER_SIMPLEADMIN_READABILITY_AB_V1:
    //
    // EVILHACKER_SIMPLEADMIN_COUNT100_VISIBILITY_AB_V1:
    // room  = pure white / bold / base size
    // count = yellow / bold / base size (NO nested size tag)
    // dots  = black/red / 85%
    // EVILHACKER_SIMPLEADMIN_PLAIN_ROOM_LABEL_AB_V1:
    // SimpleAdmin owns room-label styling. Remove any embedded TMP RichText
    // from localization output before applying pure-white/bold styling.
    // EVILHACKER_SIMPLEADMIN_FUNGLE_ZIPLINE_STRMISS_FALLBACK_AB_V1:
    // Fungle DisplayLayouts authority:
    //   SystemTypes.Zipline = row 2 / lane 4.
    // Runtime screenshot shows STRMISS at this exact slot.
    // Keep the fallback display-only and do not touch room/system identity.
    private static string GetSimpleAdminRoomLabel(SystemTypes room)
    {
        var translated =
            NormalizeSimpleAdminRoomLabel(
                DestroyableSingleton<TranslationController>
                    .Instance
                    .GetString(room));

        if (
            room == SystemTypes.Zipline &&
            (
                string.IsNullOrWhiteSpace(translated) ||
                translated.Contains("STRMISS")
            )
        )
        {
            return "ジップライン";
        }

        return translated;
    }

    private static string NormalizeSimpleAdminRoomLabel(string value)
    {
        if (
            string.IsNullOrEmpty(value) ||
            value.IndexOf('<') < 0
        )
        {
            return value;
        }

        var normalized =
            new StringBuilder(value.Length);

        var insideTag = false;

        foreach (var character in value)
        {
            if (character == '<')
            {
                insideTag = true;
                continue;
            }

            if (
                character == '>' &&
                insideTag
            )
            {
                insideTag = false;
                continue;
            }

            if (!insideTag)
            {
                normalized.Append(character);
            }
        }

        return normalized.ToString();
    }

        // EVILHACKER_SIMPLEADMIN_READABILITY_AB_V1:
    //
    // EVILHACKER_SIMPLEADMIN_CRISP_FOREGROUND_AB_V1:
    // room  = sanitized plain text / pure white / bold / base size
    // count = pure yellow / bold / 110%
    // dots  = black/red / 85%
        // EVILHACKER_SIMPLEADMIN_READABILITY_AB_V1:
    //
    // EVILHACKER_SIMPLEADMIN_FULL_ALPHA_FOREGROUND_AB_V1:
    // room  = display-normalized / explicit opaque white / bold
    // count = explicit opaque pure yellow / bold / 110%
    // dots  = explicit opaque black/red / 85%
    private static DisplayCell CreateV274DemoCell(
        SystemTypes room,
        int totalPlayers,
        SystemTypes? selfRoom,
        IReadOnlyDictionary<SystemTypes, int> teammateRooms)
    {
        var roomName =
            GetSimpleAdminRoomLabel(room);

        var countText =
            totalPlayers.ToString();

        var text =
            new StringBuilder();

        text.Append("<b><color=#FFFFFFFF>")
            .Append(roomName)
            .Append("</color></b>")
            .Append('\u00A0')
            .Append("<b><size=110%><color=#FFFF00FF>")
            .Append(countText)
            .Append("</color></size></b>");

        var visibleWidth =
            GetDisplayWidth(roomName) +
            1 +
            GetDisplayWidth(countText);

        if (selfRoom == room)
        {
            text.Append('\u00A0')
                .Append(V274DemoSelfMarker);

            visibleWidth += 2;
        }

        if (
            teammateRooms.TryGetValue(
                room,
                out var teammateCount)
            && teammateCount > 0
        )
        {
            for (
                var index = 0;
                index < teammateCount;
                index++
            )
            {
                text.Append('\u00A0')
                    .Append(V274DemoTeammateMarkerStart)
                    .Append('●')
                    .Append(V274DemoTeammateMarkerEnd);

                visibleWidth += 2;
            }
        }

        return new(
            text.ToString(),
            visibleWidth);
    }

        // EVILHACKER_SIMPLEADMIN_VERTICAL_SPACING_OPAQUE_BG_AB_V1:
    // 120% line-height prevents row-band collision.
    // Opaque neutral gray does not become darker when mark rectangles overlap.
    // TMP mark remains behind glyphs, so room/count/markers stay foreground.
        // EVILHACKER_SIMPLEADMIN_CONTINUOUS_BAND_VERTICAL145_AB_V1:
    // v6180.274-proven translucent mark keeps glyphs visible.
    // 145% line-height keeps neighboring row bands physically separated.
        // EVILHACKER_SIMPLEADMIN_CONTINUOUS_BAND_VERTICAL145_AB_V1:
    //
    // EVILHACKER_SIMPLEADMIN_CRISP_MARK_ALPHA_AB_V1:
    // Preserve the continuous v274-style mark, but reduce alpha so white
    // room labels and pure-yellow counts remain visually crisp.
        // EVILHACKER_SIMPLEADMIN_CONTINUOUS_BAND_VERTICAL145_AB_V1:
    //
    // EVILHACKER_SIMPLEADMIN_MARK40_COLOR_STABILITY_AB_V1:
    // Keep the same neutral RGB, reduce alpha from 0x68 to 0x40 so
    // foreground white/yellow remains visually stable across maps.
    private static string WrapV274DemoLiveText(string text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : $"<size=60%><line-height=145%><align=center><mark=#505B6640>{text}</mark></align></line-height></size>";

    // EVILHACKER_SIMPLEADMIN_FUNGLE_NO_MARK_COLOR_STABILITY_AB_V1:
    // Fungleだけ背景markを外してforeground色の安定性をA/B確認する。
    // 配置・文字・人数・marker・NBSP・line-heightは変更しない。
    private static string WrapV274DemoLiveTextNoMark(string text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : $"<size=60%><line-height=145%><align=center>{text}</align></line-height></size>";
    // EVILHACKER_SIMPLEADMIN_FUNGLE_SPLIT_BACKGROUND_AB_V1:
    // Fungleの背景は空きlayoutにも常時残すが、
    // foreground文字そのものはmarkの外に置く。
    // NBSPだけをhighlightするため、Room/Count/MarkerのRGBAへ乗算しない。
    private static void AppendFungleLayoutBackground(
        StringBuilder text,
        int width)
    {
        if (width <= 0)
        {
            return;
        }

        text.Append("<mark=#505B6640>")
            .Append('\u00A0', width)
            .Append("</mark>");
    }




    private static DisplayCell CreateCell(
        SystemTypes room,
        int totalPlayers,
        SystemTypes? selfRoom,
        IReadOnlyDictionary<SystemTypes, int> teammateRooms,
        string? displayRoomName = null,
        int roomNameScalePercent = 100)
    {
        var roomName =
            displayRoomName ??
            DestroyableSingleton<TranslationController>
                .Instance
                .GetString(room);

        var countText =
            totalPlayers.ToString();

        var text =
            new StringBuilder(
                roomName.Length + 160);

        // EVILHACKER_SIMPLEADMIN_MAP_CANVAS_AB_V3:
        // 部屋名・人数・self/teammate markerを1つの不可分cellにする。
        //
        // <nobr> + NBSPの二重対策により、
        // 人数だけ次行、自分●だけ次行、という崩れを禁止する。
        text.Append("<nobr>")
            .Append("<b><color=#FFFFFF>");

        if (roomNameScalePercent < 100)
        {
            text.Append("<size=")
                .Append(roomNameScalePercent)
                .Append("%>")
                .Append(roomName)
                .Append("</size>");
        }
        else
        {
            text.Append(roomName);
        }

        text.Append("</color></b>")
            .Append('\u00A0')
            .Append("<size=140%><color=#FFE066><b>")
            .Append(countText)
            .Append("</b></color></size>");

        var visibleWidth =
            (int)Math.Ceiling(
                GetDisplayWidth(roomName) *
                roomNameScalePercent /
                100.0) +
            1 +
            countText.Length;

        if (
            selfRoom is { } currentRoom &&
            currentRoom == room
        )
        {
            text.Append('\u00A0')
                .Append(SelfMarker);

            visibleWidth += 2;
        }

        if (
            teammateRooms.TryGetValue(
                room,
                out var teammateCount) &&
            teammateCount > 0
        )
        {
            for (
                var index = 0;
                index < teammateCount;
                index++
            )
            {
                text.Append('\u00A0')
                    .Append(TeammateMarkerStart)
                    .Append('●')
                    .Append(TeammateMarkerEnd);

                visibleWidth += 2;
            }
        }

        text.Append("</nobr>");

        return new(
            text.ToString(),
            visibleWidth);
    }

    // EVILHACKER_VANILLA_POSITIONAL_PERCENT5LANE_AB_V2:
    // Vanilla ownerは旧DisplayLayouts(Row/Lane)をTMP <pos>で軽量再現する。
    // 220-byte ceiling維持。空室なし。v327 lifecycle / Host / MOD / Nyaoha変更なし。
    // EVILHACKER_ALLROOM_POSITION_PAYLOAD_DEMO_AB_V1:
    // LOCAL DISPLAY + PAYLOAD MEASUREMENT ONLY.
    // VanillaSafe builder / Vanilla sender / lifecycle are untouched.
    private static readonly HashSet<int>
        AllRoomPositionPayloadDemoLoggedMaps = new();

    // EVILHACKER_LOCAL_DEMO_SAFE_ROOM_ABBREVIATION_AB_V1:
    // LOCAL DISPLAY + PAYLOAD MEASUREMENT ONLY
    // EXACT JAPANESE ROOM NAME MATCH ONLY
    // NETWORK EXPANSION NONE
    private static string GetLocalDemoRoomDisplayName(
        SystemTypes room,
        string translatedName)
    {
        return (room, translatedName) switch
        {
            (SystemTypes.Nav, "ナビゲーション") => "ナビ",
            (SystemTypes.Weapons, "ウェポンルーム") => "ウェポン",
            (SystemTypes.Security, "セキュリティ") => "セキュ",
            (SystemTypes.Security, "セキュリティルーム") => "セキュ",
            (SystemTypes.Cafeteria, "カフェテリア") => "カフェ",
            (SystemTypes.Electrical, "電気室") => "電気",
            (SystemTypes.Electrical, "エレクトリカル") => "電気",
            (SystemTypes.Comms, "通信室") => "通信",
            (SystemTypes.Comms, "コミュニケーション") => "通信",
            (SystemTypes.UpperEngine, "上部エンジン") => "上エンジン",
            (SystemTypes.LowerEngine, "下部エンジン") => "下エンジン",
            (SystemTypes.MeetingRoom, "ミーティングルーム") => "ミーティング",
            (SystemTypes.Brig, "宿舎前通路") => "宿舎前",
            (SystemTypes.Zipline, "STRMISS") => "ジップライン",
            _ => translatedName,
        };
    }

    // EVILHACKER_AIRSHIP_OFFICIAL_ADMIN_LAYOUT_AB_V4:
    // Airship LOCAL DEMO layout is FIXED.
    //
    // EVILHACKER_SKELD_OFFICIAL_ADMIN_LAYOUT_AB_V1:
    // Skeld LOCAL DEMO ONLY.
    // Official Admin geography is used as the visual authority.
    //
    // Network / VanillaSafe / Remote MOD / Production real-data
    // behavior is unchanged.
    private static bool ShouldDisplayLocalDemoRoom(
        int mapId,
        SystemTypes room)
    {
        // Airship Ventilation is not represented as an independent
        // official Admin room in this local demo.
        return
            mapId != 4 ||
            room != SystemTypes.Ventilation;
    }

    // EVILHACKER_MIRA_POLUS_FUNGLE_OFFICIAL_LAYOUT_AB_V1:
    // LOCAL DEMO ONLY.
    // MIRA / Polus use native Admin geography as visual authority.
    // Fungle has no Admin table; sabotage-map geography is visual authority.
    // Airship V4 and Skeld FINAL remain frozen.
    private static string GetLocalDemoRoomDisplayNameForMap(
        int mapId,
        SystemTypes room,
        string translatedName)
    {
        if (mapId == 0)
        {
            return room switch
            {
                SystemTypes.LifeSupp => "酸素",
                SystemTypes.Shields => "シールド",

                _ => GetLocalDemoRoomDisplayName(
                    room,
                    translatedName),
            };
        }

        if (mapId == 1)
        {
            return room switch
            {
                SystemTypes.LockerRoom => "ロッカー",

                _ => GetLocalDemoRoomDisplayName(
                    room,
                    translatedName),
            };
        }

        if (mapId == 2)
        {
            return room switch
            {
                SystemTypes.LifeSupp => "酸素",
                SystemTypes.Decontamination => "除染",
                SystemTypes.Decontamination2 => "上部除染",

                _ => GetLocalDemoRoomDisplayName(
                    room,
                    translatedName),
            };
        }

        if (mapId == 5)
        {
            return room switch
            {
                SystemTypes.Cafeteria => "カフェ",
                SystemTypes.MiningPit => "鉱山",
                SystemTypes.SleepingQuarters => "宿舎",
                SystemTypes.MeetingRoom => "ミーティング",
                SystemTypes.Greenhouse => "温室",
                SystemTypes.Decontamination3 => "下部除染",

                _ => GetLocalDemoRoomDisplayName(
                    room,
                    translatedName),
            };
        }
        if (mapId == 4)
        {
            return room switch
            {
                SystemTypes.Engine => "エンジン",
                SystemTypes.MainHall => "メイン",
                SystemTypes.Showers => "シャワー",
                SystemTypes.CargoBay => "貨物",
                SystemTypes.Electrical => "エレキ",
                SystemTypes.HallOfPortraits => "肖像画",

                _ => GetLocalDemoRoomDisplayName(
                    room,
                    translatedName),
            };
        }

        return GetLocalDemoRoomDisplayName(
            room,
            translatedName);
    }

    private static int GetLocalDemoRoomRow(
        int mapId,
        DisplayRoomSlot slot)
    {
        if (mapId == 0)
        {
            return slot.Room switch
            {
                // ROW 0
                SystemTypes.Cafeteria => 0,

                // ROW 1
                SystemTypes.UpperEngine => 1,
                SystemTypes.MedBay => 1,
                SystemTypes.Weapons => 1,

                // ROW 2
                SystemTypes.Reactor => 2,
                SystemTypes.Security => 2,
                SystemTypes.LifeSupp => 2,
                SystemTypes.Nav => 2,

                // ROW 3
                SystemTypes.Electrical => 3,
                SystemTypes.Admin => 3,

                // ROW 4
                SystemTypes.LowerEngine => 4,
                SystemTypes.Storage => 4,
                SystemTypes.Shields => 4,

                // ROW 5
                SystemTypes.Comms => 5,

                _ => slot.Row,
            };
        }

        // EVILHACKER_FUNGLE_MAP_GEOGRAPHY_LAYOUT_AB_V2:
        // Fungle sabotage-map geography + collision-free local demo layout.
        // Laboratory moves to the right of Kitchen.
        // Jungle / Greenhouse swap positions.
        if (mapId == 5)
        {
            return slot.Room switch
            {
                // ROW 0
                SystemTypes.Dropship => 0,
                SystemTypes.MiningPit => 0,
                SystemTypes.Comms => 0,

                // ROW 1
                SystemTypes.Cafeteria => 1,
                SystemTypes.Storage => 1,
                SystemTypes.Lookout => 1,
                SystemTypes.Highlands => 1,
                SystemTypes.UpperEngine => 1,

                // ROW 2
                SystemTypes.Beach => 2,
                SystemTypes.Outside => 2,
                SystemTypes.MeetingRoom => 2,
                SystemTypes.SleepingQuarters => 2,
                SystemTypes.Zipline => 2,

                // ROW 3
                SystemTypes.RecRoom => 3,
                SystemTypes.Laboratory => 3,
                SystemTypes.Greenhouse => 3,
                SystemTypes.Reactor => 3,

                // ROW 4
                SystemTypes.FishingDock => 4,
                SystemTypes.Kitchen => 4,
                SystemTypes.Jungle => 4,
                SystemTypes.Decontamination3 => 4,

                _ => slot.Row,
            };
        }
        if (mapId == 4)
        {
            return slot.Room switch
            {
                SystemTypes.MeetingRoom => 1,

                SystemTypes.VaultRoom => 2,
                SystemTypes.Brig => 2,
                SystemTypes.GapRoom => 2,
                SystemTypes.Records => 2,

                SystemTypes.Comms => 3,
                SystemTypes.Lounge => 3,

                SystemTypes.Cockpit => 4,
                SystemTypes.Engine => 4,
                SystemTypes.MainHall => 3,
                SystemTypes.Showers => 4,
                SystemTypes.CargoBay => 4,

                SystemTypes.Armory => 5,
                SystemTypes.Kitchen => 5,
                SystemTypes.Electrical => 5,
                SystemTypes.Medical => 5,

                SystemTypes.ViewingDeck => 6,
                SystemTypes.HallOfPortraits => 6,
                SystemTypes.Security => 6,

                _ => slot.Row,
            };
        }

        return slot.Row;
    }

    // EVILHACKER_FUNGLE_MAP_GEOGRAPHY_LAYOUT_AB_V3:
    // Final collision-spacing pass requested from v6180.346 runtime screenshot.
    // X-position changes only; Fungle V2 row topology remains unchanged.
    // EVILHACKER_FUNGLE_MAP_GEOGRAPHY_LAYOUT_AB_V4:
    // Final geography pass:
    // SplashZone -> Laboratory -> Greenhouse spatial relationship.
    // Reactor anchor remains unchanged.
    // Jungle shifted 4em left.
    // EVILHACKER_FUNGLE_MAP_GEOGRAPHY_LAYOUT_AB_V5:
    // Final collision-only adjustment.
    // Splash Zone shifted 4em left: -22em -> -26em.
    // All other five-map geometry is frozen.
    private static int GetLocalDemoRoomXEm(
        int mapId,
        DisplayRoomSlot slot)
    {
        if (mapId == 0)
        {
            return slot.Room switch
            {
                // ROW 0
                SystemTypes.Cafeteria => 0,

                // ROW 1
                SystemTypes.UpperEngine => -12,
                SystemTypes.MedBay => -3,
                SystemTypes.Weapons => 12,

                // ROW 2
                SystemTypes.Reactor => -14,
                SystemTypes.Security => -6,
                SystemTypes.LifeSupp => 6,
                SystemTypes.Nav => 14,

                // ROW 3
                SystemTypes.Electrical => -8,
                SystemTypes.Admin => 5,

                // ROW 4
                SystemTypes.LowerEngine => -12,
                SystemTypes.Storage => 0,
                SystemTypes.Shields => 12,

                // ROW 5
                SystemTypes.Comms => 9,

                _ => slot.Lane * 6 - 12,
            };
        }

        if (mapId == 1)
        {
            return slot.Room switch
            {
                SystemTypes.Greenhouse => 8,

                SystemTypes.Reactor => -15,
                SystemTypes.Laboratory => -7,
                SystemTypes.Office => 7,
                SystemTypes.Admin => 15,

                SystemTypes.Decontamination => -2,

                SystemTypes.Launchpad => -18,
                SystemTypes.LockerRoom => -9,
                SystemTypes.Comms => 0,
                SystemTypes.Storage => 9,
                SystemTypes.Cafeteria => 18,

                SystemTypes.MedBay => 1,
                SystemTypes.Balcony => 16,

                _ => slot.Lane * 6 - 12,
            };
        }

        if (mapId == 2)
        {
            return slot.Room switch
            {
                SystemTypes.Dropship => 0,

                SystemTypes.Security => -18,
                SystemTypes.Electrical => -10,
                SystemTypes.Storage => 0,
                SystemTypes.Decontamination => 9,
                SystemTypes.Laboratory => 18,

                SystemTypes.LifeSupp => -17,
                SystemTypes.Comms => -9,
                SystemTypes.Outside => 0,
                SystemTypes.Office => 9,

                SystemTypes.BoilerRoom => -16,
                SystemTypes.Weapons => -8,
                SystemTypes.Admin => 8,
                SystemTypes.Decontamination2 => 16,

                SystemTypes.Specimens => 16,

                _ => slot.Lane * 6 - 12,
            };
        }

        if (mapId == 5)
        {
            return slot.Room switch
            {
                // ROW 0
                SystemTypes.Dropship => -9,
                SystemTypes.MiningPit => 6,
                SystemTypes.Comms => 18,

                // ROW 1
                SystemTypes.Cafeteria => -18,
                SystemTypes.Storage => -9,
                SystemTypes.Lookout => 0,
                SystemTypes.Highlands => 9,
                SystemTypes.UpperEngine => 18,

                // ROW 2
                // Keep Splash Zone at the collision-free left anchor.
                // Shift the remaining rooms left to separate Meeting/Sleeping
                // while preserving the sabotage-map left-to-right geography.
                SystemTypes.Beach => -26,
                SystemTypes.Outside => -11,
                SystemTypes.MeetingRoom => -3,
                SystemTypes.SleepingQuarters => 8,
                SystemTypes.Zipline => 18,

                // ROW 3
                // Map-geography V4:
                // Splash Zone is above-left.
                // Laboratory sits to its right, then Greenhouse.
                // Reactor remains at the already-approved +16em anchor.
                SystemTypes.RecRoom => -16,
                SystemTypes.Laboratory => -6,
                SystemTypes.Greenhouse => 4,
                SystemTypes.Reactor => 16,

                // ROW 4
                // Jungle shifts four em left from V3.
                SystemTypes.FishingDock => -22,
                SystemTypes.Kitchen => -12,
                SystemTypes.Jungle => 2,
                SystemTypes.Decontamination3 => 16,

                _ => slot.Lane * 6 - 12,
            };
        }
        if (mapId == 4)
        {
            return slot.Room switch
            {
                // ROW 0
                SystemTypes.MeetingRoom => 0,

                // ROW 1
                SystemTypes.VaultRoom => -14,
                SystemTypes.Brig => -7,
                SystemTypes.GapRoom => 0,
                SystemTypes.Records => 7,

                // ROW 2
                SystemTypes.Comms => -12,
                SystemTypes.Lounge => 10,

                // ROW 3
                SystemTypes.Cockpit => -17,
                SystemTypes.Engine => -6,
                SystemTypes.MainHall => 0,
                SystemTypes.Showers => 5,
                SystemTypes.CargoBay => 14,

                // ROW 4
                SystemTypes.Armory => -15,
                SystemTypes.Kitchen => -8,
                SystemTypes.Electrical => 3,
                SystemTypes.Medical => 11,

                // ROW 5
                SystemTypes.ViewingDeck => -16,
                SystemTypes.HallOfPortraits => -6,
                SystemTypes.Security => 1,

                _ => slot.Lane * 6 - 12,
            };
        }

        return slot.Lane * 6 - 12;
    }
    // EVILHACKER_COMPACT_FIXED_SLOT_6EM_LOCAL_DEMO_AB_V1:
    // LOCAL DISPLAY + PAYLOAD MEASUREMENT ONLY
    // NETWORK EXPANSION NONE
    // EVILHACKER_COMPACT_FIXED_SLOT_6EM_CENTERED_LOCAL_DEMO_AB_V1:
    // LOCAL DISPLAY CENTER CALIBRATION ONLY
    // LANES=-12/-6/0/6/12em
    // NETWORK EXPANSION NONE
    // EVILHACKER_LOCAL_DEMO_POS_CLOSE_ELIMINATION_AB_V1:
    // LOCAL DISPLAY + PAYLOAD MEASUREMENT ONLY
    // TMP POS CLOSING TAG ELIMINATION
    // NETWORK EXPANSION NONE
    private static string BuildAllRoomPositionPayloadDemoBody(
        int mapId,
        int maxRooms,
        bool markersInEveryRoom)
    {
        if (
            !DisplayLayouts.TryGetValue(
                mapId,
                out var topology) ||
            topology.Length == 0)
        {
            return string.Empty;
        }

        var body = new StringBuilder(1024);
        body.Append("<align=left>");

        var emittedRooms =
            new HashSet<SystemTypes>();

        var previousRow = -1;
        var emittedCount = 0;

        foreach (
            var slot in topology
                .OrderBy(
                    slot =>
                        GetLocalDemoRoomRow(
                            mapId,
                            slot))
                .ThenBy(
                    slot =>
                        GetLocalDemoRoomXEm(
                            mapId,
                            slot)))
        {
            if (emittedCount >= maxRooms)
            {
                break;
            }

            if (
                !ShouldDisplayLocalDemoRoom(
                    mapId,
                    slot.Room) ||
                !emittedRooms.Add(slot.Room))
            {
                continue;
            }

            var localRow =
                GetLocalDemoRoomRow(
                    mapId,
                    slot);

            var rowBreakCount =
                previousRow < 0
                    ? localRow + 1
                    : Math.Max(
                        0,
                        localRow - previousRow);

            if (rowBreakCount > 0)
            {
                body.Append('\n', rowBreakCount);
            }

            body.Append("<pos=")
                .Append(
                    GetLocalDemoRoomXEm(
                        mapId,
                        slot))
                .Append("em>")
                .Append(
                    GetLocalDemoRoomDisplayNameForMap(
                        mapId,
                        slot.Room,
                        DestroyableSingleton<TranslationController>
                            .Instance
                            .GetString(slot.Room)))
                .Append(":1");

            // EVILHACKER_LOCAL_DEMO_SELF_MARKER_FILLED_CIRCLE_AB_V1:
            // LOCAL DISPLAY + PAYLOAD MEASUREMENT ONLY
            // SELF=● TEAMMATE=★
            // UTF8 PAYLOAD DELTA=0
            // NETWORK EXPANSION NONE
            if (markersInEveryRoom)
            {
                // Intentional artificial stress:
                // every displayed room carries both marker glyphs.
                body.Append("●★");
            }
            else
            {
                // Representative 15-player layout:
                // self and teammate exist in separate rooms.
                if (emittedCount == 0)
                {
                    body.Append("●");
                }

                if (emittedCount == 1)
                {
                    body.Append("★");
                }
            }


            previousRow = localRow;
            emittedCount++;
        }

        if (previousRow >= 0)
        {
            body.Append(
                '\n',
                topology.Max(
                    slot =>
                        GetLocalDemoRoomRow(
                            mapId,
                            slot)) -
                previousRow);
        }

        body.Append("</align>");
        return body.ToString();
    }

    private static string WrapAllRoomPositionPayloadDemoBody(
        string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        // EVILHACKER_SIMPLEADMIN_STABLE_BOLD_NO_MARK_AB_V2:
        // Keep only global bold styling.
        // TMP <mark> was removed because Vanilla PlayerName rendering
        // showed unstable highlight/color/digit visibility with <pos>.
        // Added network cost is exactly 7 ASCII/UTF-8 bytes per changed
        // snapshot, independent of occupied-room count.
        return
            VanillaSafeLiveTextPrefix +
            "<b>" +
            body +
            "</b>" +
            VanillaSafeLiveTextSuffix;
    }

    // EVILHACKER_SIMPLEADMIN_FIXED_POSITION_REALDATA_BODY_V1:
    // Uses the exact finalized Row/X/name helpers that powered
    // the all-room visual stress demo.
    private static string BuildFixedPositionRealDataBody(
        int mapId,
        SortedDictionary<SystemTypes, AdminProvider.AdminEntry> admins,
        SystemTypes? selfRoom,
        IReadOnlyDictionary<SystemTypes, int> teammateRooms)
    {
        if (
            !DisplayLayouts.TryGetValue(
                mapId,
                out var topology) ||
            topology.Length == 0)
        {
            return string.Empty;
        }

        var occupied =
            admins
                .Where(
                    entry =>
                        entry.Value.TotalPlayers > 0)
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.TotalPlayers);

        if (occupied.Count == 0)
        {
            return string.Empty;
        }

        var body =
            new StringBuilder(1024);

        body.Append("<align=left>");

        var emittedRooms =
            new HashSet<SystemTypes>();

        var previousRow = -1;

        foreach (
            var slot in topology
                .OrderBy(
                    slot =>
                        GetLocalDemoRoomRow(
                            mapId,
                            slot))
                .ThenBy(
                    slot =>
                        GetLocalDemoRoomXEm(
                            mapId,
                            slot)))
        {
            if (
                !ShouldDisplayLocalDemoRoom(
                    mapId,
                    slot.Room) ||
                !occupied.TryGetValue(
                    slot.Room,
                    out var totalPlayers) ||
                !emittedRooms.Add(
                    slot.Room))
            {
                continue;
            }

            var localRow =
                GetLocalDemoRoomRow(
                    mapId,
                    slot);

            var rowBreakCount =
                previousRow < 0
                    ? localRow + 1
                    : Math.Max(
                        0,
                        localRow - previousRow);

            if (rowBreakCount > 0)
            {
                body.Append(
                    '\n',
                    rowBreakCount);
            }

            body.Append("<pos=")
                .Append(
                    GetLocalDemoRoomXEm(
                        mapId,
                        slot))
                .Append("em>")
                .Append(
                    GetLocalDemoRoomDisplayNameForMap(
                        mapId,
                        slot.Room,
                        DestroyableSingleton<TranslationController>
                            .Instance
                            .GetString(slot.Room)))
                .Append(':')
                .Append(totalPlayers);

            if (selfRoom == slot.Room)
            {
                body.Append("●");
            }

            if (
                teammateRooms.TryGetValue(
                    slot.Room,
                    out var teammateCount) &&
                teammateCount > 0)
            {
                body.Append("★");

                if (teammateCount > 1)
                {
                    body.Append(
                        teammateCount);
                }
            }

            previousRow =
                localRow;
        }

        if (previousRow < 0)
        {
            return string.Empty;
        }

        var highestDisplayRow =
            topology
                .Where(
                    slot =>
                        ShouldDisplayLocalDemoRoom(
                            mapId,
                            slot.Room))
                .Select(
                    slot =>
                        GetLocalDemoRoomRow(
                            mapId,
                            slot))
                .DefaultIfEmpty(
                    previousRow)
                .Max();

        if (highestDisplayRow > previousRow)
        {
            body.Append(
                '\n',
                highestDisplayRow - previousRow);
        }

        body.Append("</align>");

        return body.ToString();
    }
    private static int CalculateWorstCase15PayloadUtf8Bytes(
        int mapId)
    {
        if (
            !DisplayLayouts.TryGetValue(
                mapId,
                out var topology) ||
            topology.Length == 0)
        {
            return 0;
        }

        var slots =
            topology
                .GroupBy(slot => slot.Room)
                .Select(group => group.First())
                .ToArray();

        var selectedRoomCount =
            Math.Min(
                15,
                slots.Length);

        if (selectedRoomCount == 0)
        {
            return 0;
        }

        var roomTokenBytes =
            slots
                .ToDictionary(
                    slot => slot.Room,
                    slot =>
                        Encoding.UTF8.GetByteCount(
                            "<pos=" +
                            (slot.Lane * 6 - 12) +
                            "em>" +
                            GetLocalDemoRoomDisplayName(
                                slot.Room,
                                DestroyableSingleton<TranslationController>
                                    .Instance
                                    .GetString(slot.Room)) +
                            ":1"));

        var fixedBytes =
            Encoding.UTF8.GetByteCount(
                VanillaSafeLiveTextPrefix +
                "<align=left>") +
            Encoding.UTF8.GetByteCount(
                "</align>" +
                VanillaSafeLiveTextSuffix);

        // 15-player model:
        // self one room + teammate one different room.
        fixedBytes +=
            Encoding.UTF8.GetByteCount("◎");

        if (selectedRoomCount >= 2)
        {
            fixedBytes +=
                Encoding.UTF8.GetByteCount("★");
        }

        var best = -1;
        var highestLayoutRow =
            slots.Max(slot => slot.Row);

        for (
            var candidateMaxRow = 0;
            candidateMaxRow <= highestLayoutRow;
            candidateMaxRow++)
        {
            var eligible =
                slots
                    .Where(
                        slot =>
                            slot.Row <= candidateMaxRow)
                    .ToArray();

            if (eligible.Length < selectedRoomCount)
            {
                continue;
            }

            var maxRowSlots =
                eligible
                    .Where(
                        slot =>
                            slot.Row == candidateMaxRow)
                    .ToArray();

            foreach (var forcedMaxRowSlot in maxRowSlots)
            {
                var remaining =
                    eligible
                        .Where(
                            slot =>
                                slot.Room !=
                                forcedMaxRowSlot.Room)
                        .OrderByDescending(
                            slot =>
                                roomTokenBytes[slot.Room])
                        .Take(selectedRoomCount - 1)
                        .ToArray();

                if (
                    remaining.Length !=
                    selectedRoomCount - 1)
                {
                    continue;
                }

                var candidateBytes =
                    fixedBytes +
                    roomTokenBytes[forcedMaxRowSlot.Room] +
                    remaining.Sum(
                        slot =>
                            roomTokenBytes[slot.Room]) +
                    highestLayoutRow +
                    1;

                if (candidateBytes > best)
                {
                    best = candidateBytes;
                }
            }
        }

        return Math.Max(
            0,
            best);
    }
    internal static string BuildAllRoomPositionPayloadDemoLocalLiveText(
        int mapId)
    {
        var allRoomStress =
            WrapAllRoomPositionPayloadDemoBody(
                BuildAllRoomPositionPayloadDemoBody(
                    mapId,
                    int.MaxValue,
                    markersInEveryRoom: true));

        var representative15 =
            WrapAllRoomPositionPayloadDemoBody(
                BuildAllRoomPositionPayloadDemoBody(
                    mapId,
                    15,
                    markersInEveryRoom: false));

        if (
            AllRoomPositionPayloadDemoLoggedMaps.Add(mapId) &&
            DisplayLayouts.TryGetValue(
                mapId,
                out var topology))
        {
            var allRoomCount =
                topology
                    .Select(slot => slot.Room)
                    .Distinct()
                    .Count();

            var representative15RoomCount =
                Math.Min(
                    15,
                    allRoomCount);

            var allRoomBytes =
                Encoding.UTF8.GetByteCount(
                    allRoomStress);

            var representative15Bytes =
                Encoding.UTF8.GetByteCount(
                    representative15);

            var worstCase15Bytes =
                CalculateWorstCase15PayloadUtf8Bytes(
                    mapId);

            TownOfHostForE.Logger.Info(
                $"EVILHACKER_ALLROOM_POSITION_PAYLOAD_DEMO_AB_V1 " +
                $"MapId={mapId} " +
                $"AllRoomCount={allRoomCount} " +
                $"AllRoomStressChars={allRoomStress.Length} " +
                $"AllRoomStressUtf8Bytes={allRoomBytes} " +
                $"AllRoomStressOver220={Math.Max(0, allRoomBytes - VanillaSafeMaxUtf8Bytes)} " +
                $"Representative15RoomCount={representative15RoomCount} " +
                $"Representative15Chars={representative15.Length} " +
                $"Representative15Utf8Bytes={representative15Bytes} " +
                $"WorstCase15Utf8Bytes={worstCase15Bytes} " +
                $"WorstCase15Over220={Math.Max(0, worstCase15Bytes - VanillaSafeMaxUtf8Bytes)} " +
                $"CurrentCeiling={VanillaSafeMaxUtf8Bytes} " +
                $"NetworkSetName=False DemoNetworkSetName=False LocalDisplay=True",
                nameof(EvilHackerTextMinimap));
        }

        return allRoomStress;
    }
    private static string BuildVanillaSafeLiveText(
        int mapId,
        SortedDictionary<SystemTypes, AdminProvider.AdminEntry> admins,
        SystemTypes? selfRoom,
        IReadOnlyDictionary<SystemTypes, int> teammateRooms)
    {
        var body = new StringBuilder(192);
        body.Append("<align=left>");

        var occupied =
            admins
                .Where(entry => entry.Value.TotalPlayers > 0)
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.TotalPlayers);

        var emittedRooms =
            new HashSet<SystemTypes>();

        var previousRow = -1;
        var budgetExhausted = false;

        if (
            DisplayLayouts.TryGetValue(
                mapId,
                out var topology) &&
            topology.Length > 0)
        {
            foreach (
                var slot in topology
                    .OrderBy(slot => slot.Row)
                    .ThenBy(slot => slot.Lane)
            )
            {
                if (
                    !occupied.TryGetValue(
                        slot.Room,
                        out var totalPlayers) ||
                    emittedRooms.Contains(slot.Room))
                {
                    continue;
                }

                var cell =
                    new StringBuilder(48);

                var rowBreakCount =
                    previousRow < 0
                        ? slot.Row + 1
                        : Math.Max(
                            0,
                            slot.Row - previousRow);

                if (rowBreakCount > 0)
                {
                    cell.Append('\n', rowBreakCount);
                }

                // 旧1lane ≒ 17～19半角幅 ≒ 約9em。
                // 数十個のNBSPではなく短い絶対水平位置タグへ再符号化する。
                cell.Append("<pos=")
                    .Append(slot.Lane * 20)
                    .Append("%>")
                    .Append(
                        DestroyableSingleton<TranslationController>
                            .Instance
                            .GetString(slot.Room))
                    .Append(':')
                    .Append(totalPlayers);

                if (selfRoom == slot.Room)
                {
                    cell.Append("◎");
                }

                if (
                    teammateRooms.TryGetValue(
                        slot.Room,
                        out var teammateCount) &&
                    teammateCount > 0)
                {
                    cell.Append("★");

                    if (teammateCount > 1)
                    {
                        cell.Append(teammateCount);
                    }
                }

                cell.Append("</pos>");

                var candidateBody =
                    body.ToString() +
                    cell +
                    "</align>";

                var candidateWrapped =
                    VanillaSafeLiveTextPrefix +
                    candidateBody +
                    VanillaSafeLiveTextSuffix;

                if (
                    Encoding.UTF8.GetByteCount(candidateWrapped) >
                    VanillaSafeMaxUtf8Bytes)
                {
                    budgetExhausted = true;
                    break;
                }

                body.Append(cell);
                emittedRooms.Add(slot.Room);
                previousRow = slot.Row;
            }
        }

        if (!budgetExhausted)
        {
            foreach (var room in OrderRooms(mapId, occupied.Keys))
            {
                if (emittedRooms.Contains(room))
                {
                    continue;
                }

                var line =
                    new StringBuilder(48);

                line.Append('\n')
                    .Append("<pos=0em>")
                    .Append(
                        DestroyableSingleton<TranslationController>
                            .Instance
                            .GetString(room))
                    .Append(':')
                    .Append(occupied[room]);

                if (selfRoom == room)
                {
                    line.Append("◎");
                }

                if (
                    teammateRooms.TryGetValue(
                        room,
                        out var teammateCount) &&
                    teammateCount > 0)
                {
                    line.Append("★");
                }

                line.Append("</pos>");

                var candidateBody =
                    body.ToString() +
                    line +
                    "</align>";

                var candidateWrapped =
                    VanillaSafeLiveTextPrefix +
                    candidateBody +
                    VanillaSafeLiveTextSuffix;

                if (
                    Encoding.UTF8.GetByteCount(candidateWrapped) >
                    VanillaSafeMaxUtf8Bytes)
                {
                    break;
                }

                body.Append(line);
                emittedRooms.Add(room);
            }
        }

        if (emittedRooms.Count == 0)
        {
            body.Append("ADMIN");
        }

        body.Append("</align>");

        return WrapVanillaSafeLiveText(
            body.ToString());
    }

    private static string WrapVanillaSafeLiveText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var wrapped =
            VanillaSafeLiveTextPrefix +
            text +
            VanillaSafeLiveTextSuffix;

        if (
            Encoding.UTF8.GetByteCount(wrapped) <=
            VanillaSafeMaxUtf8Bytes)
        {
            return wrapped;
        }

        return
            VanillaSafeLiveTextPrefix +
            "ADMIN" +
            VanillaSafeLiveTextSuffix;
    }

    private static string BuildChatText(
        int mapId,
        SortedDictionary<SystemTypes, AdminProvider.AdminEntry> admins,
        IEnumerable<PlayerControl> teammates,
        bool showDeadBodiesInMeetingChat)
    {
        var chat = new StringBuilder(512);
        foreach (var room in OrderRooms(mapId, admins.Keys))
        {
            var entry = admins[room];
            if (entry.TotalPlayers <= 0)
            {
                continue;
            }
            chat.Append(DestroyableSingleton<TranslationController>.Instance.GetString(room))
                .Append(':').Append(entry.TotalPlayers);
            if (showDeadBodiesInMeetingChat && entry.NumDeadBodies > 0)
            {
                chat.Append(" (死体×")
                    .Append(entry.NumDeadBodies)
                    .Append(')');
            }
            chat.Append('\n');
        }

        foreach (var teammate in teammates)
        {
            var room = teammate.GetPlainShipRoom()?.RoomId;
            if (room is not { } roomId || roomId == SystemTypes.Hallway)
            {
                continue;
            }
            chat.Append("<color=#ff0000>●</color> ")
                .Append(teammate.GetRealName()).Append(": ")
                .Append(DestroyableSingleton<TranslationController>.Instance.GetString(roomId)).Append('\n');
        }
        return chat.ToString().TrimEnd();
    }

    private static IEnumerable<SystemTypes> OrderRooms(int mapId, IEnumerable<SystemTypes> rooms)
    {
        var remaining = new HashSet<SystemTypes>(rooms);
        if (Layouts.TryGetValue(mapId, out var layout))
        {
            foreach (var slot in layout.OrderBy(slot => slot.Row).ThenBy(slot => slot.Column))
            {
                if (remaining.Remove(slot.Room))
                {
                    yield return slot.Room;
                }
            }
        }
        foreach (var room in remaining.OrderBy(room => (int)room))
        {
            yield return room;
        }
    }

    private static string WrapLiveText(string text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : $"{LiveTextPrefix}<voffset=-1.4em><align=center><color=#FFFFFF>{text}</color></align><voffset=0em></line-height></size>";

    private static int GetDisplayWidth(string text)
    {
        var width = 0;
        foreach (var character in text)
        {
            width += character <= 0x7f ? 1 : 2;
        }
        return width;
    }
}
