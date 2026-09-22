using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using TownOfHostForE.Roles;
using TownOfHostForE.Roles.Core;
using TownOfHostForE.Roles.Neutral;

namespace TownOfHostForE
{
    // MASSU_ORIGINAL_ROLE_ASSIGN_AB_V1
    // 直前1試合のMainRoleだけを比較するA/B。
    // 陣営は比較しない。Addonは対象外。
    // Standard配役では同一MainRoleを禁止し、数学的に避けられない
    // CustomRoles.Crewmate -> CustomRoles.Crewmate だけを最小数で許容する。
    internal static class MassuOriginalRoleAssignmentABV1
    {
        private const string LogMarker = "MASSU_ORIGINAL_ROLE_ASSIGN_AB_V1";

        // ENJOB_REINCARNATION_3_HISTORY_WEIGHTED_V1
        // With a maximum of 15 players:
        // 256 > 15 * (16 + 1), and 16 > 15 * 1.
        // Therefore the global min-cost objective is lexicographic:
        // previous game > two games ago > three games ago.
        private const int HistoryDepth = 3;
        private const int HistoryPenaltyPrevious1 = 256;
        private const int HistoryPenaltyPrevious2 = 16;
        private const int HistoryPenaltyPrevious3 = 1;

        // The maximum total history penalty is 15 * (256 + 16 + 1) = 4095.
        // Keep every configured custom-role token more valuable than that,
        // so history avoidance can never discard a configured custom role.
        private const int CustomRoleReward = 10_000;
        private const int InfiniteCost = 1_000_000;

        private static readonly Dictionary<string, List<CustomRoles>> PreviousMainRoles = new();
        private static readonly Dictionary<byte, CustomRoles> DesyncPlan = new();
        private static readonly Dictionary<byte, CustomRoles> StandardMainPlan = new();
        private static readonly Dictionary<byte, RoleTypes> StandardNativePlan = new();

        private static bool StandardPlanReady;

        internal static bool Enabled =>
            Options.IsMassuOriginalRoleAssigning &&
            Options.CurrentGameMode == CustomGameMode.Standard;

        internal static void BeginGame()
        {
            DesyncPlan.Clear();
            StandardMainPlan.Clear();
            StandardNativePlan.Clear();
            StandardPlanReady = false;

            if (Options.CurrentGameMode != CustomGameMode.Standard)
            {
                PreviousMainRoles.Clear();
                return;
            }

            Logger.Info(
                $"Stage=BEGIN | Enabled={Enabled} | PreviousPlayers={PreviousMainRoles.Count} | HistoryDepth={HistoryDepth}",
                LogMarker);
        }

        private static string? GetIdentityKey(PlayerControl player)
        {
            if (player == null) return null;

            var friendCode = player.FriendCode;
            if (!string.IsNullOrWhiteSpace(friendCode))
                return "F|" + friendCode;

            var clientId = player.GetClientId();
            if (clientId >= 0)
                return "C|" + clientId;

            return "P|" + player.PlayerId;
        }

        private static int GetHistoryPenalty(PlayerControl player, CustomRoles role)
        {
            var key = GetIdentityKey(player);
            if (key == null ||
                !PreviousMainRoles.TryGetValue(key, out var history) ||
                history == null ||
                history.Count == 0)
                return 0;

            var penalty = 0;

            if (history.Count > 0 && history[0] == role)
                penalty += HistoryPenaltyPrevious1;

            if (history.Count > 1 && history[1] == role)
                penalty += HistoryPenaltyPrevious2;

            if (history.Count > 2 && history[2] == role)
                penalty += HistoryPenaltyPrevious3;

            return penalty;
        }
        private static void Shuffle<T>(IList<T> values)
        {
            var random = IRandom.Instance;
            if (random == null) return;

            for (var i = values.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (values[i], values[j]) = (values[j], values[i]);
            }
        }

        private static int CountBits(int value)
        {
            var count = 0;
            while (value != 0)
            {
                count += value & 1;
                value >>= 1;
            }
            return count;
        }

        internal static void PrepareDesyncPlan(List<PlayerControl> allPlayers)
        {
            DesyncPlan.Clear();
            if (!Enabled || allPlayers == null || allPlayers.Count == 0) return;

            var players = allPlayers
                .Where(player => player != null && player.Data != null)
                .ToList();

            if (players.Count == 0 || players.Count > 15)
            {
                Logger.Info(
                    $"Stage=DESYNC_FALLBACK | Reason=PLAYER_COUNT | Players={players.Count}",
                    LogMarker);
                return;
            }

            var roleTokens = new List<CustomRoles>();
            var remainingPlayers = players.Count;
            var impostorCount =
                Main.NormalOptions.GetInt(Int32OptionNames.NumImpostors);

            foreach (var (role, info) in CustomRoleManager.AllRolesInfo)
            {
                if (!info.IsDesyncImpostor) continue;

                if (role == CustomRoles.Opportunist &&
                    !Opportunist.OptionCanKill.GetBool())
                    continue;

                var requested = Math.Max(0, role.GetRealCount());

                // Existing AssignDesyncRole(StrayWolf, IsImpostorRole:true)
                // intentionally returns without assigning when it would consume
                // every native impostor slot.
                if (role == CustomRoles.StrayWolf &&
                    impostorCount == requested)
                    continue;

                var count = Math.Min(requested, remainingPlayers);
                for (var i = 0; i < count; i++)
                    roleTokens.Add(role);

                remainingPlayers -= count;
                if (remainingPlayers <= 0) break;
            }

            if (roleTokens.Count == 0)
            {
                Logger.Info("Stage=DESYNC_PLAN | Tokens=0", LogMarker);
                return;
            }

            if (!TryBuildPartialPlan(players, roleTokens, out var plan))
            {
                Logger.Info(
                    $"Stage=DESYNC_FALLBACK | Reason=NO_WEIGHTED_HISTORY_MATCH | Tokens={roleTokens.Count}",
                    LogMarker);
                return;
            }

            foreach (var pair in plan)
                DesyncPlan[pair.Key] = pair.Value;

            Logger.Info(
                $"Stage=DESYNC_PLAN | Tokens={roleTokens.Count} | Planned={DesyncPlan.Count}",
                LogMarker);
        }

        private static bool TryBuildPartialPlan(
            List<PlayerControl> sourcePlayers,
            List<CustomRoles> sourceRoles,
            out Dictionary<byte, CustomRoles> plan)
        {
            plan = new Dictionary<byte, CustomRoles>();

            var players = new List<PlayerControl>(sourcePlayers);
            var roles = new List<CustomRoles>(sourceRoles);

            while (roles.Count < players.Count)
                roles.Add(CustomRoles.NotAssigned);

            if (roles.Count != players.Count || players.Count > 15)
                return false;

            Shuffle(players);
            Shuffle(roles);

            var size = 1 << players.Count;
            var bestCost = Enumerable.Repeat(InfiniteCost, size).ToArray();
            var previousMask = new int[size];
            var chosenPlayer = new int[size];
            Array.Fill(previousMask, -1);
            Array.Fill(chosenPlayer, -1);
            bestCost[0] = 0;

            for (var mask = 0; mask < size; mask++)
            {
                if (bestCost[mask] >= InfiniteCost) continue;

                var roleIndex = CountBits(mask);
                if (roleIndex >= roles.Count) continue;

                var role = roles[roleIndex];

                for (var playerIndex = 0; playerIndex < players.Count; playerIndex++)
                {
                    var bit = 1 << playerIndex;
                    if ((mask & bit) != 0) continue;

                    var historyCost =
                        role == CustomRoles.NotAssigned
                            ? 0
                            : GetHistoryPenalty(players[playerIndex], role);

                    var nextMask = mask | bit;
                    var nextCost = bestCost[mask] + historyCost;

                    if (nextCost >= bestCost[nextMask])
                        continue;

                    bestCost[nextMask] = nextCost;
                    previousMask[nextMask] = mask;
                    chosenPlayer[nextMask] = playerIndex;
                }
            }

            var fullMask = size - 1;
            if (bestCost[fullMask] >= InfiniteCost) return false;

            var currentMask = fullMask;
            for (var roleIndex = roles.Count - 1; roleIndex >= 0; roleIndex--)
            {
                var playerIndex = chosenPlayer[currentMask];
                if (playerIndex < 0) return false;

                var role = roles[roleIndex];
                if (role != CustomRoles.NotAssigned)
                    plan[players[playerIndex].PlayerId] = role;

                currentMask = previousMask[currentMask];
            }

            return currentMask == 0;
        }
        internal static PlayerControl? TakeDesyncPlannedPlayer(
            CustomRoles role,
            List<PlayerControl> candidates)
        {
            if (!Enabled || candidates == null || candidates.Count == 0)
                return null;

            foreach (var player in candidates)
            {
                if (player != null &&
                    DesyncPlan.TryGetValue(player.PlayerId, out var plannedRole) &&
                    plannedRole == role)
                    return player;
            }

            return null;
        }

        private static RoleTypes GetPoolKeyFromNative(RoleTypes nativeRole)
        {
            // NATIVE_WRAPPER_SLOT_AB_V1:
            // Judge/Viper must keep their exact native slot. They are not interchangeable
            // with Scientist/Shapeshifter because their native abilities differ.
            return nativeRole;
        }

        private static RoleTypes GetPoolKeyForCustomRole(CustomRoles role)
        {
            return role.GetRoleTypes() switch
            {
                RoleTypes.Impostor => RoleTypes.Impostor,
                RoleTypes.Shapeshifter => RoleTypes.Shapeshifter,
                RoleTypes.Phantom => RoleTypes.Phantom,
                RoleTypes.Scientist => RoleTypes.Scientist,
                RoleTypes.Engineer => RoleTypes.Engineer,
                RoleTypes.Noisemaker => RoleTypes.Noisemaker,
                RoleTypes.Tracker => RoleTypes.Tracker,
                RoleTypes.GuardianAngel => RoleTypes.GuardianAngel,
                _ => RoleTypes.Crewmate,
            };
        }

        private static bool TryGetVanillaMainRole(
            RoleTypes nativeRole,
            out CustomRoles mainRole)
        {
            mainRole = nativeRole switch
            {
                RoleTypes.Crewmate => CustomRoles.Crewmate,
                RoleTypes.Impostor => CustomRoles.Impostor,
                RoleTypes.Scientist => CustomRoles.Scientist,
                RoleTypes.Judge => CustomRoles.NormalJudge,
                RoleTypes.Engineer => CustomRoles.Engineer,
                RoleTypes.GuardianAngel => CustomRoles.GuardianAngel,
                RoleTypes.Shapeshifter => CustomRoles.Shapeshifter,
                RoleTypes.Viper => CustomRoles.NormalViper,
                RoleTypes.Tracker => CustomRoles.Tracker,
                RoleTypes.Noisemaker => CustomRoles.Noisemaker,
                RoleTypes.Phantom => CustomRoles.NormalPhantom,
                _ => CustomRoles.NotAssigned,
            };

            return mainRole != CustomRoles.NotAssigned;
        }

        private sealed class CustomToken
        {
            internal CustomRoles Role;
            internal RoleTypes PoolKey;
            internal int InNode;
            internal int OutNode;
            internal FlowEdge? GateEdge;
            internal readonly List<(RoleTypes NativeRole, FlowEdge Edge)> NativeEdges = new();

            internal CustomToken(CustomRoles role, RoleTypes poolKey)
            {
                Role = role;
                PoolKey = poolKey;
            }
        }

        private sealed class PlayerLinks
        {
            internal PlayerControl Player;
            internal int Node;
            internal readonly List<(RoleTypes NativeRole, FlowEdge Edge)> VanillaEdges = new();
            internal readonly List<(CustomToken Token, FlowEdge Edge)> CustomEdges = new();

            internal PlayerLinks(PlayerControl player, int node)
            {
                Player = player;
                Node = node;
            }
        }

        private sealed class FlowEdge
        {
            internal int To;
            internal int Rev;
            internal int Cap;
            internal int Cost;
            internal int OriginalCap;

            internal FlowEdge(int to, int rev, int cap, int cost)
            {
                To = to;
                Rev = rev;
                Cap = cap;
                Cost = cost;
                OriginalCap = cap;
            }
        }

        private sealed class MinCostFlow
        {
            private readonly List<FlowEdge>[] graph;

            internal MinCostFlow(int nodeCount)
            {
                graph = new List<FlowEdge>[nodeCount];
                for (var i = 0; i < nodeCount; i++)
                    graph[i] = new List<FlowEdge>();
            }

            internal FlowEdge AddEdge(int from, int to, int cap, int cost)
            {
                var forward = new FlowEdge(to, graph[to].Count, cap, cost);
                var reverse = new FlowEdge(from, graph[from].Count, 0, -cost);
                graph[from].Add(forward);
                graph[to].Add(reverse);
                return forward;
            }

            internal (int Flow, int Cost) Run(int source, int sink, int requiredFlow)
            {
                var flow = 0;
                var cost = 0;
                var nodeCount = graph.Length;

                while (flow < requiredFlow)
                {
                    var distance = Enumerable.Repeat(InfiniteCost, nodeCount).ToArray();
                    var previousNode = Enumerable.Repeat(-1, nodeCount).ToArray();
                    var previousEdge = Enumerable.Repeat(-1, nodeCount).ToArray();
                    var inQueue = new bool[nodeCount];
                    var queue = new Queue<int>();

                    distance[source] = 0;
                    queue.Enqueue(source);
                    inQueue[source] = true;

                    while (queue.Count > 0)
                    {
                        var node = queue.Dequeue();
                        inQueue[node] = false;

                        if (distance[node] >= InfiniteCost)
                            continue;

                        for (var edgeIndex = 0; edgeIndex < graph[node].Count; edgeIndex++)
                        {
                            var edge = graph[node][edgeIndex];
                            if (edge.Cap <= 0) continue;

                            var nextDistance = distance[node] + edge.Cost;
                            if (nextDistance >= distance[edge.To])
                                continue;

                            distance[edge.To] = nextDistance;
                            previousNode[edge.To] = node;
                            previousEdge[edge.To] = edgeIndex;

                            if (!inQueue[edge.To])
                            {
                                inQueue[edge.To] = true;
                                queue.Enqueue(edge.To);
                            }
                        }
                    }

                    if (previousNode[sink] < 0)
                        break;

                    var current = sink;
                    while (current != source)
                    {
                        var from = previousNode[current];
                        var edgeIndex = previousEdge[current];
                        var edge = graph[from][edgeIndex];

                        edge.Cap--;
                        graph[current][edge.Rev].Cap++;

                        current = from;
                    }

                    flow++;
                    cost += distance[sink];
                }

                return (flow, cost);
            }
        }

        private static bool TryBuildStandardPlan(
            List<(PlayerControl Player, RoleTypes NativeRole)> entries,
            out Dictionary<byte, CustomRoles> mainPlan,
            out Dictionary<byte, RoleTypes> nativePlan,
            out int customTokenCount,
            out int crewRepeatCount)
        {
            mainPlan = new Dictionary<byte, CustomRoles>();
            nativePlan = new Dictionary<byte, RoleTypes>();
            customTokenCount = 0;
            crewRepeatCount = 0;

            var players = entries.Select(entry => entry.Player).ToList();
            if (players.Count == 0 || players.Count > 15)
                return false;

            Shuffle(players);

            var nativeCounts = new Dictionary<RoleTypes, int>();
            foreach (var entry in entries)
            {
                if (!TryGetVanillaMainRole(entry.NativeRole, out _))
                    return false;

                nativeCounts.TryGetValue(entry.NativeRole, out var count);
                nativeCounts[entry.NativeRole] = count + 1;
            }

            var poolRemaining = new Dictionary<RoleTypes, int>();
            foreach (var pair in nativeCounts)
            {
                var poolKey = GetPoolKeyFromNative(pair.Key);
                poolRemaining.TryGetValue(poolKey, out var count);
                poolRemaining[poolKey] = count + pair.Value;
            }

            var tokens = new List<CustomToken>();

            foreach (var role in CustomRolesHelper.AllStandardRoles)
            {
                if (role.IsVanilla()) continue;

                // NATIVE_WRAPPER_SLOT_AB_V1:
                // NormalJudge/NormalPhantom/NormalViper are represented directly by their native roles.
                // Do not create a second custom token that could consume another player slot.
                if (role is CustomRoles.NormalJudge or CustomRoles.NormalPhantom or CustomRoles.NormalViper) continue;

                var roleInfo = CustomRoleManager.GetRoleInfo(role);
                if (roleInfo?.IsDesyncImpostor == true) continue;

                if (role == CustomRoles.Opportunist &&
                    Opportunist.OptionCanKill.GetBool())
                    continue;

                var poolKey = GetPoolKeyForCustomRole(role);
                poolRemaining.TryGetValue(poolKey, out var remaining);

                var count = Math.Clamp(role.GetRealCount(), 0, remaining);
                for (var i = 0; i < count; i++)
                    tokens.Add(new CustomToken(role, poolKey));

                poolRemaining[poolKey] = remaining - count;
            }

            customTokenCount = tokens.Count;
            Shuffle(tokens);

            var nativeTypes = nativeCounts.Keys.ToList();
            Shuffle(nativeTypes);

            var playerCount = players.Count;
            var tokenCount = tokens.Count;
            var nativeCount = nativeTypes.Count;

            var source = 0;
            var playerStart = 1;
            var tokenInStart = playerStart + playerCount;
            var tokenOutStart = tokenInStart + tokenCount;
            var nativeStart = tokenOutStart + tokenCount;
            var sink = nativeStart + nativeCount;
            var flowGraph = new MinCostFlow(sink + 1);

            var playerLinks = new List<PlayerLinks>();
            var nativeNodeByRole = new Dictionary<RoleTypes, int>();

            for (var nativeIndex = 0; nativeIndex < nativeTypes.Count; nativeIndex++)
                nativeNodeByRole[nativeTypes[nativeIndex]] = nativeStart + nativeIndex;

            for (var playerIndex = 0; playerIndex < players.Count; playerIndex++)
            {
                var player = players[playerIndex];
                var playerNode = playerStart + playerIndex;
                var links = new PlayerLinks(player, playerNode);
                playerLinks.Add(links);

                flowGraph.AddEdge(source, playerNode, 1, 0);

                foreach (var nativeRole in nativeTypes)
                {
                    if (!TryGetVanillaMainRole(nativeRole, out var vanillaRole))
                        return false;

                    var historyPenalty =
                        GetHistoryPenalty(player, vanillaRole);

                    var edge = flowGraph.AddEdge(
                        playerNode,
                        nativeNodeByRole[nativeRole],
                        1,
                        historyPenalty);

                    links.VanillaEdges.Add((nativeRole, edge));
                }
            }

            for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
            {
                var token = tokens[tokenIndex];
                token.InNode = tokenInStart + tokenIndex;
                token.OutNode = tokenOutStart + tokenIndex;
                token.GateEdge = flowGraph.AddEdge(token.InNode, token.OutNode, 1, 0);

                foreach (var nativeRole in nativeTypes)
                {
                    if (GetPoolKeyFromNative(nativeRole) != token.PoolKey)
                        continue;

                    var edge = flowGraph.AddEdge(
                        token.OutNode,
                        nativeNodeByRole[nativeRole],
                        1,
                        0);
                    token.NativeEdges.Add((nativeRole, edge));
                }

                if (token.NativeEdges.Count == 0)
                    return false;
            }

            foreach (var links in playerLinks)
            {
                foreach (var token in tokens)
                {
                    var historyPenalty =
                        GetHistoryPenalty(links.Player, token.Role);

                    var edge = flowGraph.AddEdge(
                        links.Node,
                        token.InNode,
                        1,
                        historyPenalty - CustomRoleReward);

                    links.CustomEdges.Add((token, edge));
                }
            }

            foreach (var nativeRole in nativeTypes)
            {
                flowGraph.AddEdge(
                    nativeNodeByRole[nativeRole],
                    sink,
                    nativeCounts[nativeRole],
                    0);
            }

            var result = flowGraph.Run(source, sink, playerCount);
            if (result.Flow != playerCount)
                return false;

            if (tokens.Any(token => token.GateEdge == null || token.GateEdge.Cap != 0))
                return false;

            var tokenNative = new Dictionary<CustomToken, RoleTypes>();
            foreach (var token in tokens)
            {
                var used = token.NativeEdges
                    .Where(pair => pair.Edge.OriginalCap == 1 && pair.Edge.Cap == 0)
                    .ToList();

                if (used.Count != 1)
                    return false;

                tokenNative[token] = used[0].NativeRole;
            }

            foreach (var links in playerLinks)
            {
                var usedCustom = links.CustomEdges
                    .Where(pair => pair.Edge.OriginalCap == 1 && pair.Edge.Cap == 0)
                    .ToList();

                var usedVanilla = links.VanillaEdges
                    .Where(pair => pair.Edge.OriginalCap == 1 && pair.Edge.Cap == 0)
                    .ToList();

                if (usedCustom.Count + usedVanilla.Count != 1)
                    return false;

                CustomRoles plannedRole;
                RoleTypes plannedNative;

                if (usedCustom.Count == 1)
                {
                    plannedRole = usedCustom[0].Token.Role;
                    plannedNative = tokenNative[usedCustom[0].Token];
                }
                else
                {
                    plannedNative = usedVanilla[0].NativeRole;
                    if (!TryGetVanillaMainRole(plannedNative, out plannedRole))
                        return false;
                }

                if (GetHistoryPenalty(links.Player, plannedRole) > 0 &&
                    plannedRole == CustomRoles.Crewmate)
                    crewRepeatCount++;

                mainPlan[links.Player.PlayerId] = plannedRole;
                nativePlan[links.Player.PlayerId] = plannedNative;
            }

            var plannedNativeCounts = nativePlan.Values
                .GroupBy(role => role)
                .ToDictionary(group => group.Key, group => group.Count());

            if (plannedNativeCounts.Count != nativeCounts.Count)
                return false;

            foreach (var pair in nativeCounts)
            {
                if (!plannedNativeCounts.TryGetValue(pair.Key, out var count) ||
                    count != pair.Value)
                    return false;
            }

            return mainPlan.Count == playerCount &&
                   nativePlan.Count == playerCount;
        }

        internal static void PrepareStandardPlan(
            List<(PlayerControl, RoleTypes)> storagedData)
        {
            StandardMainPlan.Clear();
            StandardNativePlan.Clear();
            StandardPlanReady = false;

            if (!Enabled || storagedData == null || storagedData.Count == 0)
                return;

            var expectedPlayers = Main.AllPlayerControls
                .Where(player =>
                {
                    if (player == null || player.Data == null) return false;
                    var state = PlayerState.GetByPlayerId(player.PlayerId);
                    return state != null &&
                           state.MainRole == CustomRoles.NotAssigned;
                })
                .ToList();

            var eligibleEntries = storagedData
                .Where(entry =>
                {
                    if (entry.Item1 == null || entry.Item1.Data == null)
                        return false;
                    var state = PlayerState.GetByPlayerId(entry.Item1.PlayerId);
                    return state != null &&
                           state.MainRole == CustomRoles.NotAssigned;
                })
                .Select(entry => (Player: entry.Item1, NativeRole: entry.Item2))
                .ToList();

            var duplicateCount = eligibleEntries
                .GroupBy(entry => entry.Player.PlayerId)
                .Count(group => group.Count() != 1);

            var expectedIds = expectedPlayers
                .Select(player => player.PlayerId)
                .OrderBy(id => id)
                .ToArray();

            var entryIds = eligibleEntries
                .Select(entry => entry.Player.PlayerId)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();

            if (duplicateCount != 0 ||
                !expectedIds.SequenceEqual(entryIds))
            {
                Logger.Info(
                    $"Stage=STANDARD_FALLBACK | Reason=STORED_ROLE_SHAPE | " +
                    $"Expected={expectedIds.Length} | Entries={eligibleEntries.Count} | " +
                    $"DuplicateGroups={duplicateCount}",
                    LogMarker);
                return;
            }

            if (!TryBuildStandardPlan(
                    eligibleEntries,
                    out var mainPlan,
                    out var nativePlan,
                    out var customTokenCount,
                    out var crewRepeatCount))
            {
                Logger.Info(
                    $"Stage=STANDARD_FALLBACK | Reason=NO_VALID_FULL_MATCH | " +
                    $"Players={eligibleEntries.Count}",
                    LogMarker);
                return;
            }

            var originalNativeCounts = eligibleEntries
                .GroupBy(entry => entry.NativeRole)
                .ToDictionary(group => group.Key, group => group.Count());

            var plannedNativeCounts = nativePlan.Values
                .GroupBy(role => role)
                .ToDictionary(group => group.Key, group => group.Count());

            if (originalNativeCounts.Count != plannedNativeCounts.Count ||
                originalNativeCounts.Any(pair =>
                    !plannedNativeCounts.TryGetValue(pair.Key, out var count) ||
                    count != pair.Value))
            {
                Logger.Info(
                    "Stage=STANDARD_FALLBACK | Reason=NATIVE_MULTISET_MISMATCH",
                    LogMarker);
                return;
            }

            for (var index = 0; index < storagedData.Count; index++)
            {
                var entry = storagedData[index];
                if (entry.Item1 != null &&
                    nativePlan.TryGetValue(entry.Item1.PlayerId, out var nativeRole))
                {
                    storagedData[index] = (entry.Item1, nativeRole);
                }
            }

            foreach (var pair in mainPlan)
                StandardMainPlan[pair.Key] = pair.Value;
            foreach (var pair in nativePlan)
                StandardNativePlan[pair.Key] = pair.Value;

            StandardPlanReady = true;

            Logger.Info(
                $"Stage=STANDARD_PLAN | Players={mainPlan.Count} | " +
                $"CustomTokens={customTokenCount} | CrewRepeats={crewRepeatCount} | " +
                $"NativeMultiset=EXACT",
                LogMarker);
        }

        // EVILHACKER_INITIAL_OWNER_NATIVE_PHANTOM_AB_V4
        // StandardMainPlanを変更せず、initial native role bundleからplanned custom roleを読むだけ。
        internal static bool TryGetStandardPlannedRole(
            byte playerId,
            out CustomRoles role)
        {
            role = CustomRoles.NotAssigned;
            if (!Enabled || !StandardPlanReady)
                return false;

            return StandardMainPlan.TryGetValue(playerId, out role);
        }

        internal static PlayerControl? TakeStandardPlannedPlayer(
            CustomRoles role,
            List<PlayerControl> candidates)
        {
            if (!Enabled || !StandardPlanReady ||
                candidates == null || candidates.Count == 0)
                return null;

            foreach (var player in candidates)
            {
                if (player != null &&
                    StandardMainPlan.TryGetValue(player.PlayerId, out var plannedRole) &&
                    plannedRole == role)
                    return player;
            }

            Logger.Info(
                $"Stage=STANDARD_SELECTOR_FALLBACK | Role={role} | Candidates={candidates.Count}",
                LogMarker);
            return null;
        }

        internal static void CommitCurrentRoles()
        {
            if (Options.CurrentGameMode != CustomGameMode.Standard)
                return;

            var plannedCount = DesyncPlan.Count + StandardMainPlan.Count;
            var mismatchCount = 0;

            if (Enabled)
            {
                foreach (var pair in DesyncPlan)
                {
                    var state = PlayerState.GetByPlayerId(pair.Key);
                    if (state == null || state.MainRole != pair.Value)
                        mismatchCount++;
                }

                if (StandardPlanReady)
                {
                    foreach (var pair in StandardMainPlan)
                    {
                        var state = PlayerState.GetByPlayerId(pair.Key);
                        if (state == null || state.MainRole != pair.Value)
                            mismatchCount++;
                    }
                }
            }

            var currentRoles = new Dictionary<string, CustomRoles>();
            foreach (var player in Main.AllPlayerControls)
            {
                if (player == null || player.Data == null) continue;

                var state = PlayerState.GetByPlayerId(player.PlayerId);
                if (state == null ||
                    state.MainRole == CustomRoles.NotAssigned ||
                    state.MainRole == CustomRoles.GM)
                    continue;

                var key = GetIdentityKey(player);
                if (key != null)
                    currentRoles[key] = state.MainRole;
            }

            var nextHistory =
                new Dictionary<string, List<CustomRoles>>();

            foreach (var pair in currentRoles)
            {
                var history = new List<CustomRoles>
                {
                    pair.Value,
                };

                if (PreviousMainRoles.TryGetValue(pair.Key, out var previousHistory) &&
                    previousHistory != null)
                {
                    foreach (var previousRole in previousHistory.Take(HistoryDepth - 1))
                        history.Add(previousRole);
                }

                nextHistory[pair.Key] = history;
            }

            PreviousMainRoles.Clear();
            foreach (var pair in nextHistory)
                PreviousMainRoles[pair.Key] = pair.Value;
            Logger.Info(
                $"Stage=COMMIT | CurrentPlayers={PreviousMainRoles.Count} | HistoryDepth={HistoryDepth} | " +
                $"Planned={plannedCount} | FinalMismatch={mismatchCount}",
                LogMarker);
        }
    }
}
