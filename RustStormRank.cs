
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RustStormRank", "Milestorme", "0.5.5")]
    [Description("Low-overhead leaderboard and ranking core for RustStorm with current wipe, lifetime, team scopes, and polished rebuilt clean UI from stable core.")]
    public class RustStormRank : RustPlugin
    {
        #region Fields

        private PluginConfig _config;
        private StoredData _data;

        private readonly HashSet<ulong> _dirtyPlayers = new HashSet<ulong>();
        private readonly HashSet<ulong> _dirtyTeams = new HashSet<ulong>();
        private readonly Dictionary<string, List<LeaderboardEntry>> _leaderboardCache = new Dictionary<string, List<LeaderboardEntry>>();
        private readonly Dictionary<ulong, RuntimePlayerState> _runtimeStates = new Dictionary<ulong, RuntimePlayerState>();
        private readonly Dictionary<ulong, Vector3> _lastPositionByPlayer = new Dictionary<ulong, Vector3>();
        private readonly Dictionary<ulong, int> _playersPageByViewer = new Dictionary<ulong, int>();

        private Timer _saveTimer;
        private Timer _recalcTimer;
        private Timer _activityTimer;
        private Timer _cleanupTimer;
        private Timer _dailyDiscordTimer;
        private DateTime _lastDailyDiscordPostUtc = DateTime.MinValue;
        private string _lastDailyDiscordPostSlotKey = string.Empty;

        private const string DataFileName = "RustStormRank_Data";
        private const string ArchiveFilePrefix = "RustStormRank_Archive_";
        private const string DefaultAdminPermission = "ruststormrank.admin";
        private const string UiRoot = "RustStormRank.UI";

        private const string UiBgMain = "0.025 0.030 0.040 0.985";
        private const string UiBgHeader = "0.055 0.070 0.095 0.985";
        private const string UiBgPanel = "0.070 0.085 0.110 0.955";
        private const string UiBgCard = "0.090 0.110 0.145 0.965";
        private const string UiBgStat = "0.145 0.165 0.205 0.940";
        private const string UiBgStatSoft = "0.115 0.135 0.170 0.930";
        private const string UiBgRow = "0.105 0.125 0.160 0.950";
        private const string UiBgRowAlt = "0.090 0.108 0.140 0.950";
        private const string UiBgRowTop = "0.135 0.125 0.095 0.955";
        private const string UiBgRowSelf = "0.180 0.255 0.340 0.975";
        private const string UiTextPrimary = "0.930 0.950 0.985 1.000";
        private const string UiTextMuted = "0.660 0.710 0.780 1.000";
        private const string UiTextSoft = "0.780 0.820 0.880 1.000";
        private const string UiAccentBlue = "0.360 0.770 1.000 1.000";
        private const string UiAccentGold = "0.930 0.720 0.370 1.000";

        #endregion

        #region Lifecycle

        private void Init()
        {
            LoadConfig();
            LoadData();

            permission.RegisterPermission(_config.General.AdminPermission, this);

            AddCovalenceCommand(_config.General.ChatCommand, nameof(CommandRank));
            AddCovalenceCommand("rankadmin", nameof(CommandRankAdmin));
        }

        private void OnServerInitialized()
        {
            EnsureDataContainers();
            DetectAndHandleWipe();
            PrimeOnlinePlayers();
            RemoveNpcRecords();
            StartTimers();
            RebuildAllCaches();
            Puts($"[{Name}] Ready. Players={_data.Players.Count}, Teams={_data.Teams.Count}");
        }

        private void Unload()
        {
            DestroyTimers();
            foreach (var player in BasePlayer.activePlayerList)
                DestroyUi(player);
            SaveData();
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void OnNewSave(string filename)
        {
            if (!_config.Wipe.AutoDetectMapWipe)
                return;

            DetectAndHandleWipe();
            RebuildAllCaches();
        }

        #endregion

        #region Player Hooks

        private void OnPlayerConnected(BasePlayer player)
        {
            if (!IsValidPlayer(player))
                return;

            var record = GetOrCreatePlayerRecord(player.userID, player.displayName);
            record.LastKnownName = player.displayName;
            record.IsNpc = player.IsNpc;
            record.IsNpc = player.IsNpc;
            record.LastSeenUtc = DateTime.UtcNow;

            EnsureRuntimeState(player);
            _lastPositionByPlayer[player.userID] = player.transform.position;
            MarkPlayerDirty(player.userID);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (!IsValidPlayer(player))
                return;

            var record = GetOrCreatePlayerRecord(player.userID, player.displayName);
            record.LastKnownName = player.displayName;
            record.IsNpc = player.IsNpc;
            record.IsNpc = player.IsNpc;
            record.LastSeenUtc = DateTime.UtcNow;

            MarkPlayerDirty(player.userID);
            _lastPositionByPlayer.Remove(player.userID);
            _runtimeStates.Remove(player.userID);
            DestroyUi(player);
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (!IsValidPlayer(player))
                return;

            var record = GetOrCreatePlayerRecord(player.userID, player.displayName);
            record.LastKnownName = player.displayName;
            record.IsNpc = player.IsNpc;
            record.IsNpc = player.IsNpc;
            record.LastSeenUtc = DateTime.UtcNow;

            var state = EnsureRuntimeState(player);
            state.CurrentLifeStartUtc = DateTime.UtcNow;
            _lastPositionByPlayer[player.userID] = player.transform.position;

            MarkPlayerDirty(player.userID);
        }

        #endregion

        #region Combat / Resource / Build Hooks

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            try
            {
                if (entity == null || info == null)
                    return;

                var victimPlayer = entity as BasePlayer;
                var attackerPlayer = info.InitiatorPlayer;

                if (!IsValidPlayer(victimPlayer) || !IsValidPlayer(attackerPlayer) || attackerPlayer == victimPlayer)
                    return;

                var totalDamage = info.damageTypes != null ? info.damageTypes.Total() : 0f;
                if (totalDamage <= 0f)
                    return;

                var victimRecord = GetOrCreatePlayerRecord(victimPlayer.userID, victimPlayer.displayName);
                victimRecord.IsNpc = victimPlayer.IsNpc;
                victimRecord.CurrentWipe.PvP.DamageTaken += totalDamage;
                victimRecord.Lifetime.PvP.DamageTaken += totalDamage;
                MarkPlayerDirty(victimPlayer.userID);

                var attackerRecord = GetOrCreatePlayerRecord(attackerPlayer.userID, attackerPlayer.displayName);
                attackerRecord.IsNpc = attackerPlayer.IsNpc;
                attackerRecord.CurrentWipe.PvP.DamageDealt += totalDamage;
                attackerRecord.Lifetime.PvP.DamageDealt += totalDamage;
                MarkPlayerDirty(attackerPlayer.userID);
            }
            catch (Exception ex)
            {
                PrintError($"OnEntityTakeDamage error: {ex}");
            }
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            try
            {
                if (entity == null)
                    return;

                var victimPlayer = entity as BasePlayer;
                var attackerPlayer = info?.InitiatorPlayer;
                var isTruePvPDeath = IsValidPlayer(victimPlayer) && IsValidPlayer(attackerPlayer) && attackerPlayer.userID != victimPlayer.userID;

                if (IsValidPlayer(victimPlayer))
                {
                    var victimRecord = GetOrCreatePlayerRecord(victimPlayer.userID, victimPlayer.displayName);
                    victimRecord.IsNpc = victimPlayer.IsNpc;
                    victimRecord.CurrentWipe.Survival.Respawns++;
                    victimRecord.Lifetime.Survival.Respawns++;

                    if (isTruePvPDeath)
                    {
                        victimRecord.CurrentWipe.PvP.Deaths++;
                        victimRecord.Lifetime.PvP.Deaths++;
                        victimRecord.CurrentWipe.PvP.KillStreakCurrent = 0;
                        victimRecord.Lifetime.PvP.KillStreakCurrent = 0;
                    }

                    FinalizeLife(victimPlayer, victimRecord);
                    MarkPlayerDirty(victimPlayer.userID);
                }

                if (isTruePvPDeath)
                {
                    var attackerRecord = GetOrCreatePlayerRecord(attackerPlayer.userID, attackerPlayer.displayName);
                    attackerRecord.IsNpc = attackerPlayer.IsNpc;
                    attackerRecord.CurrentWipe.PvP.Kills++;
                    attackerRecord.Lifetime.PvP.Kills++;

                    if (info != null && info.isHeadshot)
                    {
                        attackerRecord.CurrentWipe.PvP.Headshots++;
                        attackerRecord.Lifetime.PvP.Headshots++;
                    }

                    attackerRecord.CurrentWipe.PvP.KillStreakCurrent++;
                    attackerRecord.Lifetime.PvP.KillStreakCurrent++;

                    if (attackerRecord.CurrentWipe.PvP.KillStreakCurrent > attackerRecord.CurrentWipe.PvP.KillStreakBest)
                        attackerRecord.CurrentWipe.PvP.KillStreakBest = attackerRecord.CurrentWipe.PvP.KillStreakCurrent;

                    if (attackerRecord.Lifetime.PvP.KillStreakCurrent > attackerRecord.Lifetime.PvP.KillStreakBest)
                        attackerRecord.Lifetime.PvP.KillStreakBest = attackerRecord.Lifetime.PvP.KillStreakCurrent;

                    MarkPlayerDirty(attackerPlayer.userID);
                }
            }
            catch (Exception ex)
            {
                PrintError($"OnEntityDeath error: {ex}");
            }
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            try
            {
                if (!IsValidPlayer(player) || item == null || item.info == null)
                    return;

                if (item.amount <= 0)
                    return;

                var record = GetOrCreatePlayerRecord(player.userID, player.displayName);
                record.IsNpc = player.IsNpc;
                ApplyGather(record.CurrentWipe.Farm, item.info.shortname, item.amount);
                ApplyGather(record.Lifetime.Farm, item.info.shortname, item.amount);
                MarkPlayerDirty(player.userID);
            }
            catch (Exception ex)
            {
                PrintError($"OnDispenserGather error: {ex}");
            }
        }

        private void OnCollectiblePickup(Item item, BasePlayer player, CollectibleEntity collectible)
        {
            try
            {
                if (!IsValidPlayer(player) || item == null || item.info == null)
                    return;

                if (item.amount <= 0)
                    return;

                var record = GetOrCreatePlayerRecord(player.userID, player.displayName);
                record.IsNpc = player.IsNpc;
                ApplyGather(record.CurrentWipe.Farm, item.info.shortname, item.amount);
                ApplyGather(record.Lifetime.Farm, item.info.shortname, item.amount);
                MarkPlayerDirty(player.userID);
            }
            catch (Exception ex)
            {
                PrintError($"OnCollectiblePickup error: {ex}");
            }
        }

        private void OnEntityBuilt(Planner planner, GameObject gameObject)
        {
            try
            {
                var player = planner?.GetOwnerPlayer();
                if (!IsValidPlayer(player) || gameObject == null)
                    return;

                var record = GetOrCreatePlayerRecord(player.userID, player.displayName);
                record.IsNpc = player.IsNpc;
                record.CurrentWipe.Build.StructuresBuilt++;
                record.Lifetime.Build.StructuresBuilt++;
                MarkPlayerDirty(player.userID);
            }
            catch (Exception ex)
            {
                PrintError($"OnEntityBuilt error: {ex}");
            }
        }

        private void OnStructureUpgrade(BuildingBlock block, BasePlayer player, BuildingGrade.Enum grade)
        {
            try
            {
                if (!IsValidPlayer(player) || block == null)
                    return;

                var record = GetOrCreatePlayerRecord(player.userID, player.displayName);
                record.IsNpc = player.IsNpc;
                record.CurrentWipe.Build.StructuresUpgraded++;
                record.Lifetime.Build.StructuresUpgraded++;
                MarkPlayerDirty(player.userID);
            }
            catch (Exception ex)
            {
                PrintError($"OnStructureUpgrade error: {ex}");
            }
        }

        private void OnStructureRepair(BaseCombatEntity entity, BasePlayer player)
        {
            try
            {
                if (!IsValidPlayer(player) || entity == null)
                    return;

                var record = GetOrCreatePlayerRecord(player.userID, player.displayName);
                record.IsNpc = player.IsNpc;
                record.CurrentWipe.Build.RepairsPerformed++;
                record.Lifetime.Build.RepairsPerformed++;
                MarkPlayerDirty(player.userID);
            }
            catch (Exception ex)
            {
                PrintError($"OnStructureRepair error: {ex}");
            }
        }

        #endregion

        #region Activity Tracking

        private void PrimeOnlinePlayers()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (!IsValidPlayer(player))
                    continue;

                GetOrCreatePlayerRecord(player.userID, player.displayName);
                EnsureRuntimeState(player);
                _lastPositionByPlayer[player.userID] = player.transform.position;
            }
        }

        private void TickPlayerActivity()
        {
            try
            {
                foreach (var player in BasePlayer.activePlayerList)
                {
                    if (!IsValidPlayer(player))
                        continue;

                    var record = GetOrCreatePlayerRecord(player.userID, player.displayName);
                    record.IsNpc = player.IsNpc;
                    var state = EnsureRuntimeState(player);

                    record.CurrentWipe.Survival.SecondsPlayed += _config.Performance.ActivityTickSeconds;
                    record.Lifetime.Survival.SecondsPlayed += _config.Performance.ActivityTickSeconds;

                    var currentPos = player.transform.position;
                    Vector3 lastPos;
                    if (_lastPositionByPlayer.TryGetValue(player.userID, out lastPos))
                    {
                        var moved = Vector3.Distance(lastPos, currentPos);
                        if (!float.IsNaN(moved) && !float.IsInfinity(moved) && moved > 0f && moved < 500f)
                        {
                            record.CurrentWipe.Survival.DistanceTraveled += moved;
                            record.Lifetime.Survival.DistanceTraveled += moved;
                        }
                    }

                    _lastPositionByPlayer[player.userID] = currentPos;

                    var lifeSeconds = (float)(DateTime.UtcNow - state.CurrentLifeStartUtc).TotalSeconds;
                    if (lifeSeconds > record.CurrentWipe.Survival.LongestLifeSeconds)
                        record.CurrentWipe.Survival.LongestLifeSeconds = lifeSeconds;
                    if (lifeSeconds > record.Lifetime.Survival.LongestLifeSeconds)
                        record.Lifetime.Survival.LongestLifeSeconds = lifeSeconds;

                    UpdateTeamStateForPlayer(player);
                    MarkPlayerDirty(player.userID);
                }
            }
            catch (Exception ex)
            {
                PrintError($"TickPlayerActivity error: {ex}");
            }
        }

        private RuntimePlayerState EnsureRuntimeState(BasePlayer player)
        {
            RuntimePlayerState state;
            if (!_runtimeStates.TryGetValue(player.userID, out state))
            {
                state = new RuntimePlayerState
                {
                    CurrentLifeStartUtc = DateTime.UtcNow,
                    LastKnownTeamId = player.currentTeam
                };
                _runtimeStates[player.userID] = state;
            }

            return state;
        }

        private void FinalizeLife(BasePlayer player, PlayerRecord record)
        {
            RuntimePlayerState state;
            if (!_runtimeStates.TryGetValue(player.userID, out state))
            {
                state = new RuntimePlayerState
                {
                    CurrentLifeStartUtc = DateTime.UtcNow,
                    LastKnownTeamId = player.currentTeam
                };
                _runtimeStates[player.userID] = state;
                return;
            }

            var lifeSeconds = (float)(DateTime.UtcNow - state.CurrentLifeStartUtc).TotalSeconds;
            if (lifeSeconds > record.CurrentWipe.Survival.LongestLifeSeconds)
                record.CurrentWipe.Survival.LongestLifeSeconds = lifeSeconds;
            if (lifeSeconds > record.Lifetime.Survival.LongestLifeSeconds)
                record.Lifetime.Survival.LongestLifeSeconds = lifeSeconds;

            state.CurrentLifeStartUtc = DateTime.UtcNow;
        }

        private void UpdateTeamStateForPlayer(BasePlayer player)
        {
            var state = EnsureRuntimeState(player);
            var currentTeam = player.currentTeam;

            if (state.LastKnownTeamId == currentTeam)
                return;

            if (state.LastKnownTeamId != 0UL)
                MarkTeamDirty(state.LastKnownTeamId);

            if (currentTeam != 0UL)
                MarkTeamDirty(currentTeam);

            state.LastKnownTeamId = currentTeam;
        }

        #endregion

        #region Commands

        private void CommandRank(IPlayer iplayer, string command, string[] args)
        {
            var player = iplayer?.Object as BasePlayer;
            if (!IsValidPlayer(player))
            {
                iplayer?.Reply("This command can only be used by an in-game player.");
                return;
            }

            var view = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "overview";
            var scope = ResolveScope(args);

            switch (view)
            {
                case "top":
                    OpenRankUi(player, scope == RankScope.Team ? RankScope.Team : scope, "top", player.userID);
                    return;
                case "pvp":
                case "farm":
                case "build":
                case "survival":
                case "overview":
                    OpenRankUi(player, scope, view, player.userID);
                    return;
                case "players":
                    OpenRankUi(player, scope == RankScope.Team ? RankScope.CurrentWipe : scope, "players", player.userID);
                    return;
                case "team":
                    OpenRankUi(player, RankScope.Team, "top", player.userID);
                    return;
                case "lifetime":
                    OpenRankUi(player, RankScope.Lifetime, "overview", player.userID);
                    return;
                default:
                    OpenRankUi(player, scope, "overview");
                    return;
            }
        }

        private void CommandRankAdmin(IPlayer iplayer, string command, string[] args)
        {
            if (iplayer == null || !iplayer.HasPermission(_config.General.AdminPermission))
            {
                iplayer?.Reply("You do not have permission to use this command.");
                return;
            }

            if (args == null || args.Length == 0)
            {
                iplayer.Reply("Usage: /rankadmin rebuild | wipe | webhook <overall|pvp|farm|build|survival|team>");
                return;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "rebuild":
                    RebuildAllCaches();
                    iplayer.Reply("Rank caches rebuilt.");
                    return;

                case "wipe":
                    ForceWipeRollover();
                    iplayer.Reply("Current wipe data rolled over.");
                    return;

                case "webhook":
                    if (args.Length < 2)
                    {
                        iplayer.Reply("Usage: /rankadmin webhook <overall|pvp|farm|build|survival|team>");
                        return;
                    }

                    var scope = args[1].Equals("team", StringComparison.OrdinalIgnoreCase) ? RankScope.Team : RankScope.CurrentWipe;
                    PostDiscordSummary(args[1].ToLowerInvariant(), scope);
                    iplayer.Reply($"Webhook attempted for {args[1]}.");
                    return;

                default:
                    iplayer.Reply("Unknown subcommand.");
                    return;
            }
        }

        [ConsoleCommand("ruststormrank.ui")]
        private void ConsoleCommandRankUi(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (!IsValidPlayer(player))
                return;

            var args = arg.Args;
            var page = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "overview";
            var scope = args != null && args.Length > 1 ? ParseScope(args[1]) : RankScope.CurrentWipe;
            var targetUserId = player.userID;

            if (args != null && args.Length > 2)
            {
                ulong parsedTarget;
                if (ulong.TryParse(args[2], out parsedTarget) && parsedTarget != 0UL)
                    targetUserId = parsedTarget;
            }

            OpenRankUi(player, scope, page, targetUserId);
        }

        [ConsoleCommand("ruststormrank.playerspage")]
        private void ConsoleCommandPlayersPage(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (!IsValidPlayer(player))
                return;

            var args = arg.Args;
            var requestedPage = 0;
            if (args != null && args.Length > 0)
                int.TryParse(args[0], out requestedPage);

            if (requestedPage < 0)
                requestedPage = 0;

            _playersPageByViewer[player.userID] = requestedPage;

            var scope = args != null && args.Length > 1 ? ParseScope(args[1]) : RankScope.CurrentWipe;
            var targetUserId = player.userID;

            if (args != null && args.Length > 2)
            {
                ulong parsedTarget;
                if (ulong.TryParse(args[2], out parsedTarget) && parsedTarget != 0UL)
                    targetUserId = parsedTarget;
            }

            OpenRankUi(player, scope, "players", targetUserId);
        }

        [ConsoleCommand("ruststormrank.close")]
        private void ConsoleCommandCloseUi(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (!IsValidPlayer(player))
                return;

            DestroyUi(player);
        }

        #endregion

        #region UI

        private void OpenRankUi(BasePlayer player, RankScope scope, string page, ulong targetUserId = 0UL)
        {
            DestroyUi(player);

            var record = GetDisplayPlayerRecord(player, targetUserId);
            if (record == null)
                return;

            var normalizedTargetUserId = record.UserId;
            var stats = GetScopeStats(record, scope == RankScope.Team ? RankScope.CurrentWipe : scope);

            var container = new CuiElementContainer();
            var panel = container.Add(new CuiPanel
            {
                Image = { Color = UiBgMain },
                RectTransform = { AnchorMin = "0.17 0.13", AnchorMax = "0.83 0.87" },
                CursorEnabled = true
            }, "Overlay", UiRoot);

            container.Add(new CuiPanel
            {
                Image = { Color = UiBgHeader },
                RectTransform = { AnchorMin = "0 0.90", AnchorMax = "1 1" }
            }, panel, UiRoot + ".Header");

            AddLabel(container, panel, _config.General.UiTitle, 22, "0.03 0.925", "0.35 0.985", UiAccentBlue, TextAnchor.MiddleLeft);
            AddLabel(container, panel, $"{GetScopeTitle(scope)}", 18, "0.36 0.925", "0.65 0.985", UiTextPrimary, TextAnchor.MiddleLeft);

            AddButton(container, panel, "Overview", BuildUiCommand("overview", scope, player.userID, normalizedTargetUserId), "0.03 0.84", "0.155 0.89", false, page == "overview");
            AddButton(container, panel, "PvP", BuildUiCommand("pvp", scope, player.userID, normalizedTargetUserId), "0.165 0.84", "0.255 0.89", false, page == "pvp");
            AddButton(container, panel, "Farm", BuildUiCommand("farm", scope, player.userID, normalizedTargetUserId), "0.265 0.84", "0.355 0.89", false, page == "farm");
            AddButton(container, panel, "Build", BuildUiCommand("build", scope, player.userID, normalizedTargetUserId), "0.365 0.84", "0.455 0.89", false, page == "build");
            AddButton(container, panel, "Survival", BuildUiCommand("survival", scope, player.userID, normalizedTargetUserId), "0.465 0.84", "0.59 0.89", false, page == "survival");
            AddButton(container, panel, "Top", BuildUiCommand("top", scope, player.userID, normalizedTargetUserId), "0.60 0.84", "0.69 0.89", false, page == "top");
            AddButton(container, panel, "Lifetime", scope == RankScope.Lifetime ? BuildUiCommand("overview", RankScope.CurrentWipe, player.userID, normalizedTargetUserId) : BuildUiCommand("overview", RankScope.Lifetime, player.userID, normalizedTargetUserId), "0.70 0.84", "0.81 0.89", false, scope == RankScope.Lifetime);

            if (scope != RankScope.Team)
                AddButton(container, panel, "Players", "ruststormrank.playerspage 0 " + ScopeToArg(scope) + " " + normalizedTargetUserId, "0.82 0.84", "0.91 0.89", false, page == "players");

            AddButton(container, panel, "Close", "ruststormrank.close", "0.89 0.925", "0.97 0.975", true);

            AddLabel(container, panel, "Players Online: " + BasePlayer.activePlayerList.Count, 14, "0.68 0.925", "0.84 0.985", UiTextSoft, TextAnchor.MiddleRight);

            if (normalizedTargetUserId != player.userID)
                AddLabel(container, panel, "Viewing: " + record.LastKnownName, 13, "0.03 0.805", "0.30 0.835", UiAccentGold, TextAnchor.MiddleLeft);

            AddSummaryCards(container, panel, stats, record, scope);

            if (page == "top")
                AddLeaderboardSection(container, panel, player, scope, normalizedTargetUserId);
            else if (page == "players" && scope != RankScope.Team)
                AddPlayersSection(container, panel, player, scope, normalizedTargetUserId);
            else
                AddStatsSection(container, panel, stats, scope, page, record.UserId);

            CuiHelper.AddUi(player, container);
        }

        private void AddSummaryCards(CuiElementContainer container, string parent, ScopeStats stats, PlayerRecord record, RankScope scope)
        {
            AddCard(container, parent, "Overall", stats.ScoreCache.OverallScore.ToString("F1"), "0.03 0.68", "0.20 0.81", "0.91 0.70 0.35 1.00");
            AddCard(container, parent, "PvP", stats.ScoreCache.PvPScore.ToString("F1"), "0.215 0.68", "0.385 0.81", "0.33 0.76 1.00 1.00");
            AddCard(container, parent, "Farm", stats.ScoreCache.FarmScore.ToString("F1"), "0.40 0.68", "0.57 0.81", "0.33 0.76 1.00 1.00");
            AddCard(container, parent, "Build", stats.ScoreCache.BuildScore.ToString("F1"), "0.585 0.68", "0.755 0.81", "0.33 0.76 1.00 1.00");
            AddCard(container, parent, "Survival", stats.ScoreCache.SurvivalScore.ToString("F1"), "0.77 0.68", "0.94 0.81", "0.33 0.76 1.00 1.00");

            var currentTier = GetRankTier(stats.ScoreCache.OverallScore);
            var currentTierColor = GetTierColor(currentTier);

            AddLabel(container, parent, $"Player: {record.LastKnownName}", 15, "0.04 0.63", "0.34 0.67", "0.85 0.92 1.00 1.00", TextAnchor.MiddleLeft);
            AddLabel(container, parent, $"Rank: #{GetPlayerRank(record.UserId, scope)}", 15, "0.35 0.63", "0.50 0.67", currentTierColor, TextAnchor.MiddleLeft);
            AddLabel(container, parent, $"KDR: {GetKdr(stats.PvP):F2}", 15, "0.51 0.63", "0.63 0.67", UiAccentBlue, TextAnchor.MiddleLeft);
            AddLabel(container, parent, $"Played: {FormatDuration(stats.Survival.SecondsPlayed)}", 15, "0.64 0.63", "0.94 0.67", UiAccentGold, TextAnchor.MiddleRight);
        }

        private void AddStatsSection(CuiElementContainer container, string parent, ScopeStats stats, RankScope scope, string page, ulong recordUserId)
        {
            var body = container.Add(new CuiPanel
            {
                Image = { Color = UiBgPanel },
                RectTransform = { AnchorMin = "0.03 0.05", AnchorMax = "0.97 0.62" }
            }, parent);

            AddLabel(container, body, GetPageTitle(page, scope), 19, "0.03 0.90", "0.97 0.98", UiTextPrimary, TextAnchor.MiddleLeft);

            switch (page)
            {
                case "pvp":
                    AddStatRow(container, body, 0.78f, "Kills", stats.PvP.Kills.ToString(), "Deaths", stats.PvP.Deaths.ToString(), "Headshot Kills", stats.PvP.Headshots.ToString());
                    AddStatRow(container, body, 0.62f, "KDR", GetKdr(stats.PvP).ToString("F2"), "Damage Dealt", stats.PvP.DamageDealt.ToString("F0"), "Damage Taken", stats.PvP.DamageTaken.ToString("F0"));
                    AddStatRow(container, body, 0.46f, "Best Streak", stats.PvP.KillStreakBest.ToString(), "Current Streak", stats.PvP.KillStreakCurrent.ToString(), "PvP Score", stats.ScoreCache.PvPScore.ToString("F1"));
                    break;

                case "farm":
                    AddStatRow(container, body, 0.78f, "Wood", stats.Farm.WoodGathered.ToString(), "Stone", stats.Farm.StoneGathered.ToString(), "Metal", stats.Farm.MetalGathered.ToString());
                    AddStatRow(container, body, 0.62f, "Sulfur", stats.Farm.SulfurGathered.ToString(), "Nodes", stats.Farm.NodesHarvested.ToString(), "Farm Score", stats.ScoreCache.FarmScore.ToString("F1"));
                    AddStatRow(container, body, 0.46f, "Gather / Hour", CalculateGatherPerHour(stats).ToString("F0"), "Played", FormatDuration(stats.Survival.SecondsPlayed), "Scope", GetScopeTitle(scope));
                    break;

                case "build":
                    AddStatRow(container, body, 0.78f, "Built", stats.Build.StructuresBuilt.ToString(), "Upgraded", stats.Build.StructuresUpgraded.ToString(), "Repaired", stats.Build.RepairsPerformed.ToString());
                    AddStatRow(container, body, 0.62f, "Build / Hour", CalculateBuildPerHour(stats).ToString("F1"), "Build Score", stats.ScoreCache.BuildScore.ToString("F1"), "Scope", GetScopeTitle(scope));
                    break;

                case "survival":
                    AddStatRow(container, body, 0.78f, "Played", FormatDuration(stats.Survival.SecondsPlayed), "Longest Life", FormatDuration(stats.Survival.LongestLifeSeconds), "Respawns", stats.Survival.Respawns.ToString());
                    AddStatRow(container, body, 0.62f, "Distance", stats.Survival.DistanceTraveled.ToString("F0") + "m", "Survival Score", stats.ScoreCache.SurvivalScore.ToString("F1"), "Scope", GetScopeTitle(scope));
                    break;

                default:
                    AddLabel(container, body, "Score Summary", 16, "0.04 0.82", "0.30 0.88", UiAccentBlue, TextAnchor.MiddleLeft);
                    AddDivider(container, body, "0.04 0.795", "0.96 0.799");

                    AddMiniStat(container, body, "Overall Score", stats.ScoreCache.OverallScore.ToString("F1"), "0.04 0.71", "0.31 0.78");
                    AddMiniStat(container, body, "PvP Score", stats.ScoreCache.PvPScore.ToString("F1"), "0.365 0.71", "0.635 0.78");
                    AddMiniStat(container, body, "Farm Score", stats.ScoreCache.FarmScore.ToString("F1"), "0.69 0.71", "0.96 0.78");

                    AddMiniStat(container, body, "Build Score", stats.ScoreCache.BuildScore.ToString("F1"), "0.04 0.62", "0.31 0.69");
                    AddMiniStat(container, body, "Survival Score", stats.ScoreCache.SurvivalScore.ToString("F1"), "0.365 0.62", "0.635 0.69");
                    AddMiniStat(container, body, "Rank", "#" + GetPlayerRank(recordUserId, scope), "0.69 0.62", "0.96 0.69");

                    var currentTier = GetRankTier(stats.ScoreCache.OverallScore);
                    var nextTier = GetNextRankTier(stats.ScoreCache.OverallScore);
                    var progressPct = GetTierProgressPercent(stats.ScoreCache.OverallScore);

                    AddLabel(container, body, "Tier Progression", 16, "0.04 0.49", "0.30 0.55", UiAccentBlue, TextAnchor.MiddleLeft);
                    AddDivider(container, body, "0.04 0.465", "0.96 0.469");
                    AddMiniStat(container, body, "Current Tier", GetTierDisplay(currentTier), "0.04 0.38", "0.31 0.45");
                    AddMiniStat(container, body, "Next Tier", GetTierDisplay(nextTier), "0.365 0.38", "0.635 0.45");
                    AddMiniStat(container, body, "Progress", progressPct.ToString("F0") + "%", "0.69 0.38", "0.96 0.45");
                    AddProgressBar(container, body, progressPct, "0.69 0.345", "0.96 0.36", GetTierBarColor(currentTier, nextTier));

                    AddLabel(container, body, "Activity Snapshot", 16, "0.04 0.25", "0.30 0.31", UiAccentBlue, TextAnchor.MiddleLeft);
                    AddDivider(container, body, "0.04 0.225", "0.96 0.229");
                    AddMiniStat(container, body, "Kills", stats.PvP.Kills.ToString(), "0.04 0.14", "0.31 0.21");
                    AddMiniStat(container, body, "Nodes Gathered", stats.Farm.NodesHarvested.ToString(), "0.365 0.14", "0.635 0.21");
                    AddMiniStat(container, body, "Structures Built", stats.Build.StructuresBuilt.ToString(), "0.69 0.14", "0.96 0.21");
                    break;
            }
        }

        private void AddLeaderboardSection(CuiElementContainer container, string parent, BasePlayer player, RankScope scope, ulong targetUserId)
        {
            var body = container.Add(new CuiPanel
            {
                Image = { Color = UiBgPanel },
                RectTransform = { AnchorMin = "0.03 0.04", AnchorMax = "0.97 0.60" }
            }, parent);

            var category = scope == RankScope.Team ? "team" : "overall";
            var key = GetLeaderboardCacheKey(scope, category);
            List<LeaderboardEntry> entries;
            if (!_leaderboardCache.TryGetValue(key, out entries))
                entries = new List<LeaderboardEntry>();

            AddLabel(container, body, "Top Rankings", 19, "0.03 0.90", "0.97 0.98", UiTextPrimary, TextAnchor.MiddleLeft);
            AddLabel(container, body, "Competitive ladder for " + GetScopeTitle(scope), 14, "0.03 0.84", "0.60 0.90", UiTextMuted, TextAnchor.MiddleLeft);

            if (entries.Count == 0)
            {
                AddDivider(container, body, "0.05 0.775", "0.95 0.779");
                AddLabel(container, body, "No leaderboard data available yet.", 17, "0.05 0.45", "0.95 0.60", UiTextSoft, TextAnchor.MiddleCenter);
                return;
            }

            var selfRank = -1;
            if (scope != RankScope.Team)
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    if (entries[i].EntryType == "player" && entries[i].Id == player.userID)
                    {
                        selfRank = i + 1;
                        break;
                    }
                }
            }

            var max = Mathf.Min(20, entries.Count);
            const int rowsPerColumn = 10;
            const float rowHeight = 0.060f;
            const float rowGap = 0.010f;
            const float yTop = 0.75f;
            var useTwoColumns = max > rowsPerColumn;

            if (useTwoColumns)
            {
                AddLabel(container, body, "Rank", 12, "0.06 0.79", "0.14 0.84", UiTextMuted, TextAnchor.MiddleLeft);
                AddLabel(container, body, "Name", 12, "0.15 0.79", "0.38 0.84", UiTextMuted, TextAnchor.MiddleLeft);
                AddLabel(container, body, "Score", 12, "0.39 0.79", "0.46 0.84", UiTextMuted, TextAnchor.MiddleRight);
                AddLabel(container, body, "Rank", 12, "0.53 0.79", "0.61 0.84", UiTextMuted, TextAnchor.MiddleLeft);
                AddLabel(container, body, "Name", 12, "0.62 0.79", "0.85 0.84", UiTextMuted, TextAnchor.MiddleLeft);
                AddLabel(container, body, "Score", 12, "0.86 0.79", "0.93 0.84", UiTextMuted, TextAnchor.MiddleRight);
            }
            else
            {
                AddLabel(container, body, "Rank", 12, "0.06 0.79", "0.16 0.84", UiTextMuted, TextAnchor.MiddleLeft);
                AddLabel(container, body, "Name", 12, "0.17 0.79", "0.74 0.84", UiTextMuted, TextAnchor.MiddleLeft);
                AddLabel(container, body, "Score", 12, "0.76 0.79", "0.93 0.84", UiTextMuted, TextAnchor.MiddleRight);
            }

            AddDivider(container, body, "0.05 0.775", "0.95 0.779");

            for (var i = 0; i < max; i++)
            {
                var entry = entries[i];
                var columnIndex = useTwoColumns ? i / rowsPerColumn : 0;
                var rowIndex = useTwoColumns ? i % rowsPerColumn : i;

                var top = yTop - (rowIndex * (rowHeight + rowGap));
                var bottom = top - rowHeight;

                var xMin = useTwoColumns
                    ? (columnIndex == 0 ? 0.05f : 0.52f)
                    : 0.05f;
                var xMax = useTwoColumns
                    ? (columnIndex == 0 ? 0.48f : 0.95f)
                    : 0.95f;
                var placeColor = GetLeaderboardPlaceColor(i);
                var isSelf = scope != RankScope.Team && entry.EntryType == "player" && entry.Id == targetUserId;
                var rowColor = isSelf ? UiBgRowSelf : (i < 3 ? UiBgRowTop : (i % 2 == 0 ? UiBgRow : UiBgRowAlt));

                var row = container.Add(new CuiPanel
                {
                    Image = { Color = rowColor },
                    RectTransform =
                    {
                        AnchorMin = xMin.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " " + bottom.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                        AnchorMax = xMax.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " " + top.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    }
                }, body);

                container.Add(new CuiPanel
                {
                    Image = { Color = placeColor },
                    RectTransform = { AnchorMin = "0.00 0.00", AnchorMax = "0.012 1.00" }
                }, row);

                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = "#" + (i + 1),
                        FontSize = 13,
                        Align = TextAnchor.MiddleLeft,
                        Color = placeColor
                    },
                    RectTransform = { AnchorMin = "0.03 0.08", AnchorMax = useTwoColumns ? "0.18 0.92" : "0.13 0.92" }
                }, row);

                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = entry.DisplayName,
                        FontSize = 14,
                        Align = TextAnchor.MiddleLeft,
                        Color = isSelf ? UiTextPrimary : UiTextSoft
                    },
                    RectTransform = { AnchorMin = useTwoColumns ? "0.18 0.08" : "0.14 0.08", AnchorMax = useTwoColumns ? "0.74 0.92" : "0.82 0.92" }
                }, row);

                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = entry.Score.ToString("F1"),
                        FontSize = 14,
                        Align = TextAnchor.MiddleRight,
                        Color = isSelf ? UiAccentGold : UiAccentBlue
                    },
                    RectTransform = { AnchorMin = useTwoColumns ? "0.76 0.08" : "0.83 0.08", AnchorMax = "0.96 0.92" }
                }, row);

                if (scope != RankScope.Team && entry.EntryType == "player")
                {
                    container.Add(new CuiButton
                    {
                        Button =
                        {
                            Color = "0 0 0 0",
                            Command = BuildUiCommand("overview", scope, player.userID, entry.Id)
                        },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                        Text = { Text = string.Empty, FontSize = 1, Align = TextAnchor.MiddleCenter, Color = "0 0 0 0" }
                    }, row);
                }
            }

            if (selfRank > max && selfRank <= entries.Count)
            {
                AddLabel(container, body, "Your Position", 14, "0.05 0.03", "0.20 0.08", UiTextSoft, TextAnchor.MiddleLeft);

                var selfEntry = entries[selfRank - 1];
                var selfRow = container.Add(new CuiPanel
                {
                    Image = { Color = UiBgRowSelf },
                    RectTransform = { AnchorMin = "0.22 0.02", AnchorMax = "0.95 0.09" }
                }, body);

                container.Add(new CuiPanel
                {
                    Image = { Color = UiAccentGold },
                    RectTransform = { AnchorMin = "0.00 0.00", AnchorMax = "0.008 1.00" }
                }, selfRow);

                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = "#" + selfRank,
                        FontSize = 13,
                        Align = TextAnchor.MiddleLeft,
                        Color = UiAccentGold
                    },
                    RectTransform = { AnchorMin = "0.03 0.08", AnchorMax = "0.14 0.92" }
                }, selfRow);

                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = selfEntry.DisplayName,
                        FontSize = 14,
                        Align = TextAnchor.MiddleLeft,
                        Color = UiTextPrimary
                    },
                    RectTransform = { AnchorMin = "0.15 0.08", AnchorMax = "0.80 0.92" }
                }, selfRow);

                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = selfEntry.Score.ToString("F1"),
                        FontSize = 14,
                        Align = TextAnchor.MiddleRight,
                        Color = UiAccentGold
                    },
                    RectTransform = { AnchorMin = "0.81 0.08", AnchorMax = "0.96 0.92" }
                }, selfRow);

                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = "0 0 0 0",
                        Command = BuildUiCommand("overview", scope, player.userID, selfEntry.Id)
                    },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    Text = { Text = string.Empty, FontSize = 1, Align = TextAnchor.MiddleCenter, Color = "0 0 0 0" }
                }, selfRow);
            }
        }

        private void AddCard(CuiElementContainer container, string parent, string title, string value, string min, string max, string accentColor)
        {
            var card = container.Add(new CuiPanel
            {
                Image = { Color = UiBgCard },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);

            AddLabel(container, card, title, 13, "0.06 0.56", "0.94 0.92", UiTextSoft, TextAnchor.MiddleCenter);
            AddLabel(container, card, value, 20, "0.08 0.10", "0.92 0.64", accentColor, TextAnchor.MiddleCenter);
        }

        private void AddMiniStat(CuiElementContainer container, string parent, string label, string value, string min, string max)
        {
            var box = container.Add(new CuiPanel
            {
                Image = { Color = UiBgStat },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);

            var valueColor = GetHighlightValueColor(label, value);

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = label,
                    FontSize = 12,
                    Align = TextAnchor.MiddleLeft,
                    Color = "0.55 0.65 0.75 1.00"
                },
                RectTransform = { AnchorMin = "0.04 0.08", AnchorMax = "0.50 0.92" }
            }, box);

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = value,
                    FontSize = 16,
                    Align = TextAnchor.MiddleRight,
                    Color = valueColor
                },
                RectTransform = { AnchorMin = "0.52 0.08", AnchorMax = "0.96 0.92" }
            }, box);
        }

        private void AddProgressBar(CuiElementContainer container, string parent, float percent, string min, string max, string fillColor = null)
        {
            var safePercent = Mathf.Clamp(percent, 0f, 100f);

            var partsMin = min.Split(' ');
            var partsMax = max.Split(' ');
            if (partsMin.Length != 2 || partsMax.Length != 2)
                return;

            float xMin, yMin, xMax, yMax;
            if (!float.TryParse(partsMin[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out xMin))
                return;
            if (!float.TryParse(partsMin[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out yMin))
                return;
            if (!float.TryParse(partsMax[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out xMax))
                return;
            if (!float.TryParse(partsMax[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out yMax))
                return;

            var width = xMax - xMin;
            var fillMax = xMin + (width * (safePercent / 100f));

            container.Add(new CuiPanel
            {
                Image = { Color = UiBgStatSoft },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);

            container.Add(new CuiPanel
            {
                Image = { Color = string.IsNullOrWhiteSpace(fillColor) ? UiAccentBlue : fillColor },
                RectTransform =
                {
                    AnchorMin = xMin.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + yMin.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    AnchorMax = fillMax.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + yMax.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }
            }, parent);
        }

        private void AddDivider(CuiElementContainer container, string parent, string min, string max)
        {
            container.Add(new CuiPanel
            {
                Image = { Color = "0.18 0.22 0.28 0.55" },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);
        }

        private string GetHighlightValueColor(string label, string value)
        {
            switch (label)
            {
                case "Overall Score":
                case "Rank":
                case "Progress":
                    return UiAccentGold;
                case "Survival Score":
                case "PvP Score":
                case "Farm Score":
                case "Build Score":
                    return UiAccentBlue;
                case "Current Tier":
                case "Next Tier":
                    return GetTierColor(value);
                default:
                    return "0.90 0.95 1.00 1.00";
            }
        }

        private string GetTierColor(string tier)
        {
            switch (tier)
            {
                case "RustGod": return "1.00 0.20 0.20 1.00";
                case "Diamond": return "0.50 0.85 1.00 1.00";
                case "Platinum": return "0.60 1.00 0.80 1.00";
                case "Gold": return "0.95 0.75 0.25 1.00";
                case "Silver": return "0.75 0.80 0.90 1.00";
                case "Bronze": return "0.80 0.55 0.35 1.00";
                case "MAX": return UiAccentGold;
                default: return UiTextMuted;
            }
        }

        private string GetTierDisplay(string tier)
        {
            return GetTierIcon(tier) + " " + tier;
        }

        private string GetTierIcon(string tier)
        {
            switch (tier)
            {
                case "RustGod": return "⚡";
                case "Diamond": return "◆";
                case "Platinum": return "✦";
                case "Gold": return "▲";
                case "Silver": return "◈";
                case "Bronze": return "◉";
                case "MAX": return "★";
                default: return "•";
            }
        }

        private string GetTierBarColor(string currentTier, string nextTier)
        {
            var sourceTier = currentTier == "Unranked" ? nextTier : currentTier;
            return GetTierColor(sourceTier);
        }


        private int GetTierOrder(string tier)
        {
            switch (tier)
            {
                case "Bronze": return 1;
                case "Silver": return 2;
                case "Gold": return 3;
                case "Platinum": return 4;
                case "Diamond": return 5;
                case "RustGod": return 6;
                case "MAX": return 7;
                default: return 0;
            }
        }

        private void HandleTierProgression(ulong userId, PlayerRecord record)
        {
            if (record == null)
                return;

            var newTier = GetRankTier(record.CurrentWipe.ScoreCache.OverallScore);
            var oldTier = record.LastKnownCurrentWipeTier;

            if (string.IsNullOrEmpty(oldTier))
            {
                record.LastKnownCurrentWipeTier = newTier;
                return;
            }

            if (string.Equals(oldTier, newTier, StringComparison.Ordinal))
                return;

            var oldOrder = GetTierOrder(oldTier);
            var newOrder = GetTierOrder(newTier);

            record.LastKnownCurrentWipeTier = newTier;

            if (newOrder <= oldOrder || newOrder <= 0)
                return;

            var player = BasePlayer.FindByID(userId);
            if (player == null || !player.IsConnected)
                return;

            if (_config.General.EnableTierUpChatMessage)
                player.ChatMessage($"<color=#33C8FF>Tier Up!</color> You reached <color=#FFD166>{newTier}</color>.");

            if (_config.General.EnableTierUpEffect &&
                !string.IsNullOrWhiteSpace(_config.General.TierUpEffectPrefab) &&
                player.net != null &&
                player.net.connection != null)
            {
                EffectNetwork.Send(new Effect(_config.General.TierUpEffectPrefab, player.transform.position, player.transform.position), player.net.connection);
            }
        }

        private string GetLeaderboardPlaceColor(int index)
        {
            switch (index)
            {
                case 0: return "0.98 0.84 0.22 1.00";
                case 1: return "0.80 0.83 0.89 1.00";
                case 2: return "0.78 0.56 0.33 1.00";
                default: return UiAccentGold;
            }
        }

        private string GetRankTier(float overallScore)
        {
            if (overallScore >= 90f) return "RustGod";
            if (overallScore >= 75f) return "Diamond";
            if (overallScore >= 60f) return "Platinum";
            if (overallScore >= 45f) return "Gold";
            if (overallScore >= 30f) return "Silver";
            if (overallScore >= 15f) return "Bronze";
            return "Unranked";
        }

        private string GetNextRankTier(float overallScore)
        {
            if (overallScore >= 90f) return "MAX";
            if (overallScore >= 75f) return "RustGod";
            if (overallScore >= 60f) return "Diamond";
            if (overallScore >= 45f) return "Platinum";
            if (overallScore >= 30f) return "Gold";
            if (overallScore >= 15f) return "Silver";
            return "Bronze";
        }

        private float GetTierProgressPercent(float overallScore)
        {
            if (overallScore >= 90f) return 100f;
            if (overallScore >= 75f) return ((overallScore - 75f) / 15f) * 100f;
            if (overallScore >= 60f) return ((overallScore - 60f) / 15f) * 100f;
            if (overallScore >= 45f) return ((overallScore - 45f) / 15f) * 100f;
            if (overallScore >= 30f) return ((overallScore - 30f) / 15f) * 100f;
            if (overallScore >= 15f) return ((overallScore - 15f) / 15f) * 100f;
            return (overallScore / 15f) * 100f;
        }

        private void AddStatRow(CuiElementContainer container, string parent, float y, string label1, string value1, string label2, string value2, string label3, string value3)
        {
            AddMiniStat(container, parent, label1, value1, "0.04 " + (y - 0.13f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "0.31 " + y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            AddMiniStat(container, parent, label2, value2, "0.365 " + (y - 0.13f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "0.635 " + y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            AddMiniStat(container, parent, label3, value3, "0.69 " + (y - 0.13f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "0.96 " + y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        private void DestroyUi(BasePlayer player)
        {
            if (player == null)
                return;

            CuiHelper.DestroyUi(player, UiRoot);
        }

#endregion

        #region Scoring / Leaderboards

        private void RecalculateDirty()
        {
            var processed = 0;
            var playerIds = new List<ulong>(_dirtyPlayers);

            foreach (var userId in playerIds)
            {
                if (processed >= _config.Performance.MaxDirtyPlayersPerPass)
                    break;

                RecalculatePlayer(userId);
                _dirtyPlayers.Remove(userId);
                processed++;
            }

            var teamIds = new List<ulong>(_dirtyTeams);
            foreach (var teamId in teamIds)
            {
                RecalculateTeam(teamId);
                _dirtyTeams.Remove(teamId);
            }

            RebuildLeaderboards();
        }

        private void RebuildAllCaches()
        {
            foreach (var userId in new List<ulong>(_data.Players.Keys))
                RecalculatePlayer(userId);

            foreach (var teamId in CollectAllKnownTeamIds())
                RecalculateTeam(teamId);

            RebuildLeaderboards();
        }

        private void RecalculatePlayer(ulong userId)
        {
            PlayerRecord record;
            if (!_data.Players.TryGetValue(userId, out record))
                return;

            CalculateScopeScores(record.CurrentWipe);
            CalculateScopeScores(record.Lifetime);
            HandleTierProgression(userId, record);

            var teamId = GetCurrentTeamId(userId);
            if (teamId != 0UL)
                MarkTeamDirty(teamId);
        }

        private void RecalculateTeam(ulong teamId)
        {
            if (teamId == 0UL)
                return;

            var teamRecord = GetOrCreateTeamRecord(teamId);
            teamRecord.MemberUserIds = GetTeamMembers(teamId);

            AggregateTeamScores(teamRecord, RankScope.CurrentWipe);
            AggregateTeamScores(teamRecord, RankScope.Lifetime);
        }

        private void CalculateScopeScores(ScopeStats stats)
        {
            var confidence = CalculateConfidence(stats);

            stats.ScoreCache.PvPScore = CalculatePvPScore(stats, confidence);
            stats.ScoreCache.FarmScore = CalculateFarmScore(stats, confidence);
            stats.ScoreCache.BuildScore = CalculateBuildScore(stats, confidence);
            stats.ScoreCache.SurvivalScore = CalculateSurvivalScore(stats, confidence);

            stats.ScoreCache.OverallScore =
                (stats.ScoreCache.PvPScore * _config.Ratings.OverallWeights.PvP) +
                (stats.ScoreCache.FarmScore * _config.Ratings.OverallWeights.Farm) +
                (stats.ScoreCache.BuildScore * _config.Ratings.OverallWeights.Build) +
                (stats.ScoreCache.SurvivalScore * _config.Ratings.OverallWeights.Survival);
        }

        private float CalculateConfidence(ScopeStats stats)
        {
            if (!_config.Ratings.UseConfidenceScaling)
                return 1f;

            var activity = 0f;
            activity += stats.PvP.Kills + stats.PvP.Deaths;
            activity += stats.Farm.NodesHarvested * 0.25f;
            activity += stats.Build.StructuresBuilt * 0.50f;
            activity += stats.Survival.SecondsPlayed / 600f;

            var threshold = Mathf.Max(1f, _config.Ratings.ConfidenceThreshold);
            return Mathf.Clamp01(Mathf.Sqrt(activity / threshold));
        }

        private float CalculatePvPScore(ScopeStats stats, float confidence)
        {
            var kills = stats.PvP.Kills;
            var deaths = Mathf.Max(1, stats.PvP.Deaths);
            var kdr = kills / (float)deaths;
            var headshotRate = kills > 0 ? stats.PvP.Headshots / (float)kills : 0f;
            var streak = stats.PvP.KillStreakBest;
            var damageEfficiency = stats.PvP.DamageDealt > 0f
                ? stats.PvP.DamageDealt / Mathf.Max(1f, stats.PvP.DamageTaken)
                : 0f;

            var total =
                (LogNormalized(kills, 100f) * 0.35f) +
                (CappedNormalized(kdr, 3f) * 0.25f) +
                (CappedNormalized(headshotRate, 0.70f) * 0.15f) +
                (CappedNormalized(damageEfficiency, 3f) * 0.15f) +
                (CappedNormalized(streak, 15f) * 0.10f);

            return total * confidence;
        }

        private float CalculateFarmScore(ScopeStats stats, float confidence)
        {
            var totalResources = stats.Farm.WoodGathered + stats.Farm.StoneGathered + stats.Farm.MetalGathered + stats.Farm.SulfurGathered;
            var hours = Mathf.Max(1f, stats.Survival.SecondsPlayed / 3600f);
            var gatherPerHour = totalResources / hours;

            var total =
                (LogNormalized(totalResources, 250000f) * 0.45f) +
                (LogNormalized(stats.Farm.SulfurGathered, 50000f) * 0.20f) +
                (LogNormalized(stats.Farm.NodesHarvested, 1500f) * 0.15f) +
                (LogNormalized(gatherPerHour, 100000f) * 0.20f);

            return total * confidence;
        }

        private float CalculateBuildScore(ScopeStats stats, float confidence)
        {
            var hours = Mathf.Max(1f, stats.Survival.SecondsPlayed / 3600f);
            var buildPerHour = stats.Build.StructuresBuilt / hours;

            var total =
                (LogNormalized(stats.Build.StructuresBuilt, 3000f) * 0.35f) +
                (LogNormalized(stats.Build.StructuresUpgraded, 1500f) * 0.25f) +
                (LogNormalized(stats.Build.RepairsPerformed, 1000f) * 0.15f) +
                (LogNormalized(buildPerHour, 500f) * 0.25f);

            return total * confidence;
        }

        private float CalculateSurvivalScore(ScopeStats stats, float confidence)
        {
            var hoursPlayed = stats.Survival.SecondsPlayed / 3600f;
            var total =
                (LogNormalized(hoursPlayed, 150f) * 0.35f) +
                (LogNormalized(stats.Survival.LongestLifeSeconds / 60f, 720f) * 0.25f) +
                ((100f - CappedNormalized(stats.Survival.Respawns, 200f)) * 0.20f) +
                (LogNormalized(stats.Survival.DistanceTraveled, 500000f) * 0.20f);

            return total * confidence;
        }

        private float LogNormalized(float value, float cap)
        {
            if (value <= 0f || cap <= 1f)
                return 0f;

            return Mathf.Clamp((Mathf.Log10(value + 1f) / Mathf.Log10(cap + 1f)) * 100f, 0f, 100f);
        }

        private float CappedNormalized(float value, float cap)
        {
            if (cap <= 0f)
                return 0f;

            return Mathf.Clamp((value / cap) * 100f, 0f, 100f);
        }

        private void RebuildLeaderboards()
        {
            BuildPlayerLeaderboard(RankScope.CurrentWipe, "overall", s => s.ScoreCache.OverallScore);
            BuildPlayerLeaderboard(RankScope.CurrentWipe, "pvp", s => s.ScoreCache.PvPScore);
            BuildPlayerLeaderboard(RankScope.CurrentWipe, "farm", s => s.ScoreCache.FarmScore);
            BuildPlayerLeaderboard(RankScope.CurrentWipe, "build", s => s.ScoreCache.BuildScore);
            BuildPlayerLeaderboard(RankScope.CurrentWipe, "survival", s => s.ScoreCache.SurvivalScore);

            BuildPlayerLeaderboard(RankScope.Lifetime, "overall", s => s.ScoreCache.OverallScore);
            BuildPlayerLeaderboard(RankScope.Lifetime, "pvp", s => s.ScoreCache.PvPScore);
            BuildPlayerLeaderboard(RankScope.Lifetime, "farm", s => s.ScoreCache.FarmScore);
            BuildPlayerLeaderboard(RankScope.Lifetime, "build", s => s.ScoreCache.BuildScore);
            BuildPlayerLeaderboard(RankScope.Lifetime, "survival", s => s.ScoreCache.SurvivalScore);

            BuildTeamLeaderboard(RankScope.Team, RankScope.CurrentWipe, "team");
            BuildTeamLeaderboard(RankScope.Team, RankScope.Lifetime, "team_lifetime");
        }

        private void BuildPlayerLeaderboard(RankScope scope, string category, Func<ScopeStats, float> scoreSelector)
        {
            var list = new List<LeaderboardEntry>();
            var cacheKey = GetLeaderboardCacheKey(scope, category);
            var previousRanks = GetPreviousRankLookup(cacheKey);

            foreach (var pair in _data.Players)
            {
                if (IsNpcRecord(pair.Value))
                    continue;

                var stats = GetScopeStats(pair.Value, scope);
                var score = scoreSelector(stats);
                if (score <= 0f)
                    continue;

                list.Add(new LeaderboardEntry
                {
                    Id = pair.Key,
                    DisplayName = pair.Value.LastKnownName,
                    Score = score,
                    EntryType = "player"
                });
            }

            list.Sort((a, b) => b.Score.CompareTo(a.Score));
            ApplyRankHistory(list, previousRanks);
            TrimLeaderboard(list);
            _leaderboardCache[cacheKey] = list;
        }

        private void BuildTeamLeaderboard(RankScope keyScope, RankScope dataScope, string category)
        {
            var list = new List<LeaderboardEntry>();
            var cacheKey = GetLeaderboardCacheKey(keyScope, category);
            var previousRanks = GetPreviousRankLookup(cacheKey);

            foreach (var pair in _data.Teams)
            {
                var stats = dataScope == RankScope.Lifetime ? pair.Value.Lifetime : pair.Value.CurrentWipe;
                if (stats.ScoreCache.TeamScore <= 0f)
                    continue;

                list.Add(new LeaderboardEntry
                {
                    Id = pair.Key,
                    DisplayName = "Team " + pair.Key,
                    Score = stats.ScoreCache.TeamScore,
                    EntryType = "team"
                });
            }

            list.Sort((a, b) => b.Score.CompareTo(a.Score));
            ApplyRankHistory(list, previousRanks);
            TrimLeaderboard(list);
            _leaderboardCache[cacheKey] = list;
        }

        private Dictionary<ulong, int> GetPreviousRankLookup(string cacheKey)
        {
            List<LeaderboardEntry> previous;
            if (!_leaderboardCache.TryGetValue(cacheKey, out previous) || previous == null)
                return new Dictionary<ulong, int>();

            var lookup = new Dictionary<ulong, int>();
            for (var i = 0; i < previous.Count; i++)
            {
                if (!lookup.ContainsKey(previous[i].Id))
                    lookup[previous[i].Id] = i + 1;
            }

            return lookup;
        }

        private void ApplyRankHistory(List<LeaderboardEntry> list, Dictionary<ulong, int> previousRanks)
        {
            if (list == null || previousRanks == null)
                return;

            for (var i = 0; i < list.Count; i++)
            {
                int previousRank;
                if (previousRanks.TryGetValue(list[i].Id, out previousRank))
                {
                    list[i].PreviousRank = previousRank;
                    list[i].RankDelta = previousRank - (i + 1);
                }
                else
                {
                    list[i].PreviousRank = 0;
                    list[i].RankDelta = 0;
                }
            }
        }

        private void TrimLeaderboard(List<LeaderboardEntry> list)
        {
            if (list.Count > _config.Performance.CacheLeaderboardEntries)
                list.RemoveRange(_config.Performance.CacheLeaderboardEntries, list.Count - _config.Performance.CacheLeaderboardEntries);
        }

        private string GetLeaderboardCacheKey(RankScope scope, string category)
        {
            return scope + ":" + category;
        }


        private void AddPlayersSection(CuiElementContainer container, string parent, BasePlayer viewer, RankScope scope, ulong targetUserId)
        {
            var body = container.Add(new CuiPanel
            {
                Image = { Color = UiBgPanel },
                RectTransform = { AnchorMin = "0.03 0.04", AnchorMax = "0.97 0.60" }
            }, parent);

            AddLabel(container, body, "Player Directory", 19, "0.03 0.90", "0.97 0.98", UiTextPrimary, TextAnchor.MiddleLeft);
            AddLabel(container, body, "Select a player to inspect their rankings and score breakdown.", 14, "0.03 0.84", "0.97 0.90", UiTextMuted, TextAnchor.MiddleLeft);

            var entries = GetSelectablePlayers(scope);
            if (entries.Count == 0)
            {
                AddDivider(container, body, "0.05 0.775", "0.95 0.779");
                AddLabel(container, body, "No player records available yet.", 17, "0.05 0.45", "0.95 0.60", UiTextSoft, TextAnchor.MiddleCenter);
                return;
            }

            const int rowsPerPage = 8;
            const float rowHeight = 0.066f;
            const float rowGap = 0.008f;
            const float yTop = 0.74f;

            var currentPage = 0;
            _playersPageByViewer.TryGetValue(viewer.userID, out currentPage);

            var totalPages = Mathf.Max(1, Mathf.CeilToInt(entries.Count / (float)rowsPerPage));
            currentPage = Mathf.Clamp(currentPage, 0, totalPages - 1);
            _playersPageByViewer[viewer.userID] = currentPage;

            AddDivider(container, body, "0.05 0.775", "0.95 0.779");
            AddLabel(container, body, "Rank", 12, "0.06 0.79", "0.14 0.84", UiTextMuted, TextAnchor.MiddleLeft);
            AddLabel(container, body, "Player", 12, "0.15 0.79", "0.66 0.84", UiTextMuted, TextAnchor.MiddleLeft);
            AddLabel(container, body, "Score", 12, "0.68 0.79", "0.80 0.84", UiTextMuted, TextAnchor.MiddleRight);
            AddLabel(container, body, "Action", 12, "0.82 0.79", "0.93 0.84", UiTextMuted, TextAnchor.MiddleCenter);

            var startIndex = currentPage * rowsPerPage;
            var endIndex = Mathf.Min(startIndex + rowsPerPage, entries.Count);

            for (var i = startIndex; i < endIndex; i++)
            {
                var localIndex = i - startIndex;
                var entry = entries[i];
                var top = yTop - (localIndex * (rowHeight + rowGap));
                var bottom = top - rowHeight;
                var isSelected = entry.Id == targetUserId;
                var placeColor = GetLeaderboardPlaceColor(i);
                var rowColor = isSelected ? UiBgRowSelf : (i < 3 ? UiBgRowTop : (i % 2 == 0 ? UiBgRow : UiBgRowAlt));

                var row = container.Add(new CuiPanel
                {
                    Image = { Color = rowColor },
                    RectTransform =
                    {
                        AnchorMin = "0.05 " + bottom.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                        AnchorMax = "0.95 " + top.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    }
                }, body);

                container.Add(new CuiPanel
                {
                    Image = { Color = placeColor },
                    RectTransform = { AnchorMin = "0.00 0.00", AnchorMax = "0.012 1.00" }
                }, row);

                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = "#" + (i + 1),
                        FontSize = 12,
                        Align = TextAnchor.MiddleLeft,
                        Color = placeColor
                    },
                    RectTransform = { AnchorMin = "0.03 0.08", AnchorMax = "0.13 0.92" }
                }, row);

                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = entry.DisplayName,
                        FontSize = 13,
                        Align = TextAnchor.MiddleLeft,
                        Color = isSelected ? UiTextPrimary : UiTextSoft
                    },
                    RectTransform = { AnchorMin = "0.15 0.08", AnchorMax = "0.75 0.92" }
                }, row);

                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = entry.Score.ToString("F1"),
                        FontSize = 13,
                        Align = TextAnchor.MiddleRight,
                        Color = isSelected ? UiAccentGold : UiAccentBlue
                    },
                    RectTransform = { AnchorMin = "0.69 0.08", AnchorMax = "0.82 0.92" }
                }, row);

                var command = isSelected ? string.Empty : BuildUiCommand("overview", scope, viewer.userID, entry.Id, true);
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Color = isSelected ? "0.24 0.47 0.72 0.98" : "0.08 0.12 0.22 0.94",
                        Command = command
                    },
                    RectTransform = { AnchorMin = "0.84 0.12", AnchorMax = "0.96 0.88" },
                    Text =
                    {
                        Text = isSelected ? "Viewing" : "View",
                        FontSize = 12,
                        Align = TextAnchor.MiddleCenter,
                        Color = UiTextPrimary
                    }
                }, row);
            }

            var prevPage = Mathf.Max(0, currentPage - 1);
            var nextPage = Mathf.Min(totalPages - 1, currentPage + 1);
            AddButton(container, body, "◀ Prev", "ruststormrank.playerspage " + prevPage + " " + ScopeToArg(scope) + " " + targetUserId, "0.05 0.05", "0.15 0.10", false, false);
            AddLabel(container, body, "Page " + (currentPage + 1) + " / " + totalPages, 13, "0.42 0.048", "0.58 0.102", UiTextSoft, TextAnchor.MiddleCenter);
            AddButton(container, body, "Next ▶", "ruststormrank.playerspage " + nextPage + " " + ScopeToArg(scope) + " " + targetUserId, "0.85 0.05", "0.95 0.10", false, false);

            AddLabel(container, body, "Tip: leaderboard rows in Top are clickable too.", 13, "0.05 0.005", "0.45 0.045", UiTextMuted, TextAnchor.MiddleLeft);
        }


        private List<LeaderboardEntry> GetSelectablePlayers(RankScope scope)
        {
            var key = GetLeaderboardCacheKey(scope == RankScope.Team ? RankScope.CurrentWipe : scope, "overall");
            List<LeaderboardEntry> entries;
            if (_leaderboardCache.TryGetValue(key, out entries) && entries != null && entries.Count > 0)
                return entries;

            var fallback = new List<LeaderboardEntry>();
            foreach (var pair in _data.Players)
            {
                if (IsNpcRecord(pair.Value))
                    continue;

                var stats = GetScopeStats(pair.Value, scope == RankScope.Team ? RankScope.CurrentWipe : scope);
                fallback.Add(new LeaderboardEntry
                {
                    Id = pair.Key,
                    DisplayName = pair.Value.LastKnownName,
                    Score = stats.ScoreCache.OverallScore,
                    EntryType = "player"
                });
            }

            fallback.Sort((a, b) =>
            {
                var byScore = b.Score.CompareTo(a.Score);
                if (byScore != 0)
                    return byScore;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            return fallback;
        }

        private PlayerRecord GetDisplayPlayerRecord(BasePlayer viewer, ulong targetUserId)
        {
            PlayerRecord record;
            if (targetUserId != 0UL && _data.Players.TryGetValue(targetUserId, out record) && !IsNpcRecord(record))
                return record;

            return GetOrCreatePlayerRecord(viewer.userID, viewer.displayName);
        }

        private string BuildUiCommand(string page, RankScope scope, ulong viewerUserId, ulong targetUserId, bool alwaysIncludeTarget = false)
        {
            var command = "ruststormrank.ui " + page + " " + ScopeToArg(scope);
            if (alwaysIncludeTarget || targetUserId != 0UL && targetUserId != viewerUserId)
                command += " " + targetUserId;
            return command;
        }

        private void AddButton(CuiElementContainer container, string parent, string text, string command, string min, string max, bool danger = false, bool active = false)
        {
            var color = danger
                ? "0.46 0.20 0.20 0.95"
                : (active ? "0.22 0.44 0.66 0.98" : "0.11 0.14 0.19 0.95");

            container.Add(new CuiButton
            {
                Button = { Color = color, Command = command },
                RectTransform = { AnchorMin = min, AnchorMax = max },
                Text = { Text = text, FontSize = 13, Align = TextAnchor.MiddleCenter, Color = UiTextPrimary }
            }, parent);
        }


        private void AddLabel(CuiElementContainer container, string parent, string text, int size, string min, string max, string color, TextAnchor align)
        {
            container.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = size, Align = align, Color = color },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);
        }

        #endregion

        #region Team Aggregation

        private void AggregateTeamScores(TeamRecord teamRecord, RankScope scope)
        {
            var memberScores = new List<float>();
            var teamworkBonus = 0f;

            foreach (var userId in teamRecord.MemberUserIds)
            {
                PlayerRecord record;
                if (!_data.Players.TryGetValue(userId, out record))
                    continue;

                var stats = GetScopeStats(record, scope);
                memberScores.Add(stats.ScoreCache.OverallScore);
                teamworkBonus += stats.Support.TeamContributions;
            }

            var target = scope == RankScope.Lifetime ? teamRecord.Lifetime : teamRecord.CurrentWipe;
            if (memberScores.Count == 0)
            {
                target.ScoreCache.TeamScore = 0f;
                return;
            }

            memberScores.Sort((a, b) => b.CompareTo(a));

            var topCount = Mathf.Min(3, memberScores.Count);
            var topAverage = 0f;
            for (var i = 0; i < topCount; i++)
                topAverage += memberScores[i];
            topAverage /= Mathf.Max(1, topCount);

            var median = memberScores[memberScores.Count / 2];
            var teamworkScore = CappedNormalized(teamworkBonus, 100f);

            target.ScoreCache.TeamScore = (topAverage * 0.55f) + (median * 0.25f) + (teamworkScore * 0.20f);
        }

        private ulong GetCurrentTeamId(ulong userId)
        {
            var active = BasePlayer.FindByID(userId);
            if (active != null)
                return active.currentTeam;

            var sleeping = BasePlayer.FindSleeping(userId);
            return sleeping != null ? sleeping.currentTeam : 0UL;
        }

        private List<ulong> GetTeamMembers(ulong teamId)
        {
            var members = new List<ulong>();
            var manager = RelationshipManager.ServerInstance;
            var team = manager != null ? manager.FindTeam(teamId) : null;
            if (team != null && team.members != null)
                members.AddRange(team.members);
            return members;
        }

        private HashSet<ulong> CollectAllKnownTeamIds()
        {
            var ids = new HashSet<ulong>(_data.Teams.Keys);
            foreach (var pair in _data.Players)
            {
                var teamId = GetCurrentTeamId(pair.Key);
                if (teamId != 0UL)
                    ids.Add(teamId);
            }
            return ids;
        }

        #endregion

        #region Wipe

        private void DetectAndHandleWipe()
        {
            if (!_config.Wipe.AutoDetectMapWipe)
                return;

            var current = BuildCurrentMapFingerprint();

            if (_data.MapFingerprint == null || !_data.MapFingerprint.Initialized)
            {
                _data.MapFingerprint = current;
                SaveData();
                return;
            }

            if (_data.MapFingerprint.Equals(current))
                return;

            Puts($"[{Name}] Wipe detected from map fingerprint change.");
            ArchiveAndResetCurrentWipe();
            _data.MapFingerprint = current;
            SaveData();
        }

        private void ForceWipeRollover()
        {
            ArchiveAndResetCurrentWipe();
            _data.MapFingerprint = BuildCurrentMapFingerprint();
            SaveData();
            RebuildAllCaches();
        }

        private void ArchiveAndResetCurrentWipe()
        {
            if (_config.Wipe.ArchivePreviousWipe)
                Interface.Oxide.DataFileSystem.WriteObject(ArchiveFilePrefix + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"), _data);

            foreach (var pair in _data.Players)
            {
                pair.Value.CurrentWipe = new ScopeStats();
                pair.Value.LastKnownCurrentWipeTier = "Unranked";
            }

            foreach (var pair in _data.Teams)
                pair.Value.CurrentWipe = new ScopeStats();

            _dirtyPlayers.Clear();
            _dirtyTeams.Clear();
            _leaderboardCache.Clear();

            foreach (var state in _runtimeStates.Values)
                state.CurrentLifeStartUtc = DateTime.UtcNow;
        }

        private MapFingerprint BuildCurrentMapFingerprint()
        {
            return new MapFingerprint
            {
                Seed = (int)World.Seed,
                Size = (int)World.Size,
                Url = World.Url ?? string.Empty,
                Name = World.Name ?? string.Empty,
                Initialized = true
            };
        }

        #endregion

        #region Discord

        private void PostDiscordSummary(string category, RankScope scope)
        {
            if (!_config.Discord.Enabled || string.IsNullOrWhiteSpace(_config.Discord.WebhookURL))
                return;

            var normalizedCategory = NormalizeWebhookCategory(category);
            var cacheCategory = normalizedCategory == "team"
                ? (scope == RankScope.Lifetime ? "team_lifetime" : "team")
                : normalizedCategory;

            var key = GetLeaderboardCacheKey(scope == RankScope.Team ? RankScope.Team : scope, cacheCategory);

            List<LeaderboardEntry> entries;
            if (!_leaderboardCache.TryGetValue(key, out entries) || entries.Count == 0)
                return;

            var max = Mathf.Min(entries.Count, 10);
            var lines = new List<string>();
            for (var i = 0; i < max; i++)
            {
                var medal = GetWebhookMedal(i);
                var entry = entries[i];
                var rankDelta = GetWebhookRankDelta(entry);
                lines.Add($"{medal} **#{i + 1} {GetWebhookEntryDisplay(entry)}** — `{entry.Score:F1}`{rankDelta}");
            }

            var displayName = string.IsNullOrWhiteSpace(_config.Discord.AuthorName) ? "RustStorm" : _config.Discord.AuthorName;
            var descriptionParts = new List<string>();

            var playerOfDay = GetWebhookPlayerOfTheDay(entries, normalizedCategory);
            if (!string.IsNullOrWhiteSpace(playerOfDay))
                descriptionParts.Add(playerOfDay);

            var dominatingLeader = GetWebhookDominatingLeader(entries);
            if (!string.IsNullOrWhiteSpace(dominatingLeader))
                descriptionParts.Add(dominatingLeader);

            descriptionParts.Add(string.Join("\n", lines.ToArray()));

            var payload = new DiscordWebhookPayload
            {
                Username = displayName,
                AvatarUrl = string.IsNullOrWhiteSpace(_config.Discord.AvatarUrl) ? _config.Discord.ThumbnailUrl : _config.Discord.AvatarUrl,
                Embeds = new List<DiscordEmbed>
                {
                    new DiscordEmbed
                    {
                        Title = GetWebhookTitle(normalizedCategory, scope),
                        Description = string.Join("\n\n", descriptionParts.ToArray()),
                        Color = ResolveWebhookColor(normalizedCategory),
                        Footer = new DiscordFooter
                        {
                            Text = GetWebhookFooter(scope, normalizedCategory)
                        },
                        Author = new DiscordAuthor
                        {
                            Name = displayName
                        },
                        Thumbnail = string.IsNullOrWhiteSpace(_config.Discord.ThumbnailUrl)
                            ? null
                            : new DiscordImage { Url = _config.Discord.ThumbnailUrl },
                        Image = string.IsNullOrWhiteSpace(_config.Discord.BannerImageUrl)
                            ? null
                            : new DiscordImage { Url = _config.Discord.BannerImageUrl },
                    }
                }
            };

            webrequest.Enqueue(
                _config.Discord.WebhookURL,
                JsonConvert.SerializeObject(payload),
                (code, response) =>
                {
                    if (code < 200 || code >= 300)
                        PrintWarning("Discord webhook failed (" + code + "): " + response);
                },
                this,
                Core.Libraries.RequestMethod.POST,
                new Dictionary<string, string> { ["Content-Type"] = "application/json" }
            );
        }

        private void CheckDailyDiscordPost()
        {
            try
            {
                if (_config == null || _config.Discord == null || _config.Discord.DailyPost == null)
                    return;

                if (!_config.Discord.Enabled || !_config.Discord.DailyPost.Enabled || string.IsNullOrWhiteSpace(_config.Discord.WebhookURL))
                    return;

                var now = GetDailyPostNow();
                var scheduledToday = new DateTime(now.Year, now.Month, now.Day, _config.Discord.DailyPost.PostHour, _config.Discord.DailyPost.PostMinute, 0);
                var category = NormalizeWebhookCategory(_config.Discord.DailyPost.Category);
                var scope = _config.Discord.DailyPost.Scope != null &&
                            _config.Discord.DailyPost.Scope.Equals("lifetime", StringComparison.OrdinalIgnoreCase)
                    ? RankScope.Lifetime
                    : RankScope.CurrentWipe;

                var slotKey = scheduledToday.ToString("yyyy-MM-dd HH:mm") + "|" + category + "|" + scope;

                if (string.Equals(_lastDailyDiscordPostSlotKey, slotKey, StringComparison.Ordinal))
                    return;

                if (now < scheduledToday)
                    return;

                if (_lastDailyDiscordPostUtc != DateTime.MinValue &&
                    (DateTime.UtcNow - _lastDailyDiscordPostUtc).TotalMinutes < 10d)
                    return;

                var cacheCategory = category == "team"
                    ? (scope == RankScope.Lifetime ? "team_lifetime" : "team")
                    : category;
                var key = GetLeaderboardCacheKey(scope == RankScope.Team ? RankScope.Team : scope, cacheCategory);

                List<LeaderboardEntry> entries;
                if (!_leaderboardCache.TryGetValue(key, out entries) || entries == null || entries.Count == 0)
                    return;

                PostDiscordSummary(category, scope);
                _lastDailyDiscordPostUtc = DateTime.UtcNow;
                _lastDailyDiscordPostSlotKey = slotKey;

                Puts($"[{Name}] Daily Discord leaderboard posted ({category}, {scope}) for slot {slotKey}.");
            }
            catch (Exception ex)
            {
                PrintError($"CheckDailyDiscordPost error: {ex}");
            }
        }

        private DateTime GetDailyPostNow()
        {
            var timeZoneId = _config != null &&
                             _config.Discord != null &&
                             _config.Discord.DailyPost != null &&
                             !string.IsNullOrWhiteSpace(_config.Discord.DailyPost.TimeZoneId)
                ? _config.Discord.DailyPost.TimeZoneId
                : "Australia/Perth";

            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            }
            catch (Exception ex)
            {
                PrintWarning($"[{Name}] Invalid DailyPost TimeZoneId '{timeZoneId}', falling back to UTC. {ex.Message}");
                return DateTime.UtcNow;
            }
        }

        private string NormalizeWebhookCategory(string category)
        {
            switch ((category ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "pvp":
                case "farm":
                case "build":
                case "survival":
                case "team":
                    return category.Trim().ToLowerInvariant();
                default:
                    return "overall";
            }
        }

        private string GetWebhookTitle(string category, RankScope scope)
        {
            var scopeText = scope == RankScope.Lifetime ? "Lifetime" : "Current Wipe";

            switch (category)
            {
                case "pvp":
                    return $"⚔ RustStorm PvP Ladder — {scopeText}";
                case "farm":
                    return $"⛏ RustStorm Farm Ladder — {scopeText}";
                case "build":
                    return $"🏗 RustStorm Build Ladder — {scopeText}";
                case "survival":
                    return $"🧭 RustStorm Survival Ladder — {scopeText}";
                case "team":
                    return $"👥 RustStorm Team Ladder — {scopeText}";
                default:
                    return $"🏆 RustStorm Rankings — {scopeText}";
            }
        }

        private string GetWebhookFooter(RankScope scope, string category)
        {
            var scopeText = scope == RankScope.Lifetime ? "Lifetime" : "Current Wipe";
            var categoryText = char.ToUpper(category[0]) + category.Substring(1);
            return $"RustStorm Rank • {scopeText} • {categoryText} Top 10";
        }

        private int ResolveWebhookColor(string category)
        {
            switch (category)
            {
                case "pvp":
                    return unchecked((int)0xFF6A2B);
                case "farm":
                    return unchecked((int)0x5CCB8A);
                case "build":
                    return unchecked((int)0x6FB5FF);
                case "survival":
                    return unchecked((int)0x8E7CFF);
                case "team":
                    return unchecked((int)0xFF9F43);
                default:
                    return _config.Discord.EmbedColor;
            }
        }

        private string GetWebhookMedal(int index)
        {
            switch (index)
            {
                case 0: return "🥇";
                case 1: return "🥈";
                case 2: return "🥉";
                default: return "•";
            }
        }

        private string GetWebhookRankDelta(LeaderboardEntry entry)
        {
            if (entry == null)
                return string.Empty;

            if (entry.PreviousRank <= 0)
                return " *(new)*";

            if (entry.RankDelta > 0)
                return $" *(+{entry.RankDelta})*";

            if (entry.RankDelta < 0)
                return $" *({entry.RankDelta})*";

            return string.Empty;
        }

        private string GetWebhookPlayerOfTheDay(List<LeaderboardEntry> entries, string category)
        {
            if (entries == null || entries.Count == 0)
                return string.Empty;

            var leader = entries[0];
            var label = leader.EntryType == "team" ? "Team of the Day" : "Player of the Day";
            return $"🔥 **{label}:** {GetWebhookEntryDisplay(leader)} — `{leader.Score:F1}` {GetWebhookCategorySuffix(category)}";
        }

        private string GetWebhookDominatingLeader(List<LeaderboardEntry> entries)
        {
            if (entries == null || entries.Count < 2)
                return string.Empty;

            var first = entries[0];
            var second = entries[1];
            if (first.Score <= 0f)
                return string.Empty;

            var leadPercent = ((first.Score - second.Score) / Mathf.Max(1f, second.Score)) * 100f;
            if (leadPercent < 15f)
                return string.Empty;

            return $"👑 **Dominating:** {GetWebhookEntryDisplay(first)} *(+{leadPercent:F0}% lead)*";
        }

        private string GetWebhookEntryDisplay(LeaderboardEntry entry)
        {
            if (entry == null)
                return "Unknown";

            if (entry.EntryType == "player")
                return $"[{EscapeDiscord(entry.DisplayName)}]({GetSteamProfileUrl(entry.Id)})";

            return EscapeDiscord(entry.DisplayName);
        }

        private string GetWebhookCategorySuffix(string category)
        {
            switch (category)
            {
                case "pvp": return "PvP";
                case "farm": return "Farm";
                case "build": return "Build";
                case "survival": return "Survival";
                case "team": return "Team";
                default: return "Overall";
            }
        }

        private string GetSteamProfileUrl(ulong userId)
        {
            if (userId == 0UL)
                return "https://steamcommunity.com";
            return "https://steamcommunity.com/profiles/" + userId;
        }


        private string EscapeDiscord(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Unknown";

            return value.Replace("`", "").Replace("*", "").Replace("_", "").Replace("~", "");
        }

        #endregion

        #region Timers

        private void StartTimers()
        {
            DestroyTimers();
            _saveTimer = timer.Every(_config.Performance.SaveIntervalSeconds, SaveData);
            _recalcTimer = timer.Every(_config.Performance.RecalculateIntervalSeconds, RecalculateDirty);
            _activityTimer = timer.Every(_config.Performance.ActivityTickSeconds, TickPlayerActivity);
            _cleanupTimer = timer.Every(_config.Performance.CleanupIntervalSeconds, CleanupInactiveTeams);
            _dailyDiscordTimer = timer.Every(30f, CheckDailyDiscordPost);
        }

        private void DestroyTimers()
        {
            _saveTimer?.Destroy();
            _recalcTimer?.Destroy();
            _activityTimer?.Destroy();
            _cleanupTimer?.Destroy();
            _dailyDiscordTimer?.Destroy();
        }

        private void CleanupInactiveTeams()
        {
            var toRemove = new List<ulong>();
            foreach (var pair in _data.Teams)
            {
                if (pair.Value.MemberUserIds == null || pair.Value.MemberUserIds.Count == 0)
                    toRemove.Add(pair.Key);
            }

            foreach (var teamId in toRemove)
                _data.Teams.Remove(teamId);
        }

        #endregion

        #region Storage / Config

        protected override void LoadDefaultConfig()
        {
            _config = PluginConfig.CreateDefault();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                    throw new Exception("Config read returned null.");
            }
            catch (Exception ex)
            {
                PrintWarning("Config load failed, using defaults: " + ex.Message);
                _config = PluginConfig.CreateDefault();
            }

            SanitizeConfig();
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void LoadData()
        {
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFileName);
            }
            catch
            {
                _data = new StoredData();
            }

            EnsureDataContainers();
        }

        private void SaveData()
        {
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject(DataFileName, _data);
            }
            catch (Exception ex)
            {
                PrintError("Failed to save data: " + ex);
            }
        }

        private void EnsureDataContainers()
        {
            if (_data == null)
                _data = new StoredData();
            if (_data.Players == null)
                _data.Players = new Dictionary<ulong, PlayerRecord>();
            if (_data.Teams == null)
                _data.Teams = new Dictionary<ulong, TeamRecord>();
            if (_data.MapFingerprint == null)
                _data.MapFingerprint = new MapFingerprint();
        }

        private void SanitizeConfig()
        {
            if (_config == null)
                _config = PluginConfig.CreateDefault();

            if (_config.General == null)
                _config.General = new GeneralSettings();
            if (_config.Performance == null)
                _config.Performance = new PerformanceSettings();
            if (_config.Ratings == null)
                _config.Ratings = new RatingSettings();
            if (_config.Ratings.OverallWeights == null)
                _config.Ratings.OverallWeights = new OverallWeightSettings();
            if (_config.Discord == null)
                _config.Discord = new DiscordSettings();
            if (_config.Discord.DailyPost == null)
                _config.Discord.DailyPost = new DailyPostSettings();
            if (_config.Wipe == null)
                _config.Wipe = new WipeSettings();

            if (string.IsNullOrWhiteSpace(_config.General.ChatCommand))
                _config.General.ChatCommand = "rank";
            if (string.IsNullOrWhiteSpace(_config.General.UiTitle))
                _config.General.UiTitle = "RustStorm Rank";
            if (string.IsNullOrWhiteSpace(_config.General.ChatCommand))
                _config.General.ChatCommand = "rank";
            if (string.IsNullOrWhiteSpace(_config.General.AdminPermission))
                _config.General.AdminPermission = DefaultAdminPermission;
            if (string.IsNullOrWhiteSpace(_config.General.TierUpEffectPrefab))
                _config.General.TierUpEffectPrefab = "assets/prefabs/misc/halloween/lootbag/effects/gold_open.prefab";

            _config.Performance.SaveIntervalSeconds = Mathf.Max(60f, _config.Performance.SaveIntervalSeconds);
            _config.Performance.RecalculateIntervalSeconds = Mathf.Max(15f, _config.Performance.RecalculateIntervalSeconds);
            _config.Performance.ActivityTickSeconds = Mathf.Clamp(_config.Performance.ActivityTickSeconds, 5f, 60f);
            _config.Performance.CleanupIntervalSeconds = Mathf.Max(60f, _config.Performance.CleanupIntervalSeconds);
            _config.Performance.MaxDirtyPlayersPerPass = Mathf.Max(5, _config.Performance.MaxDirtyPlayersPerPass);
            _config.Performance.CacheLeaderboardEntries = Mathf.Clamp(_config.Performance.CacheLeaderboardEntries, 10, 200);

            _config.Ratings.ConfidenceThreshold = Mathf.Max(1f, _config.Ratings.ConfidenceThreshold);

            _config.Discord.EmbedColor = _config.Discord.EmbedColor == 0 ? unchecked((int)0x55C1FF) : _config.Discord.EmbedColor;
            _config.Discord.DailyPost.PostHour = Mathf.Clamp(_config.Discord.DailyPost.PostHour, 0, 23);
            _config.Discord.DailyPost.PostMinute = Mathf.Clamp(_config.Discord.DailyPost.PostMinute, 0, 59);
            _config.Discord.DailyPost.Category = NormalizeWebhookCategory(_config.Discord.DailyPost.Category);
            if (string.IsNullOrWhiteSpace(_config.Discord.DailyPost.Scope))
                _config.Discord.DailyPost.Scope = "current";
            if (string.IsNullOrWhiteSpace(_config.Discord.DailyPost.TimeZoneId))
                _config.Discord.DailyPost.TimeZoneId = "Australia/Perth";
        }

        #endregion

        #region Helpers

        private bool IsValidPlayer(BasePlayer player)
        {
            return player != null && player.userID != 0UL && !player.IsNpc;
        }

        private bool IsNpcRecord(PlayerRecord record)
        {
            if (record == null)
                return false;

            if (record.IsNpc)
                return true;

            var name = (record.LastKnownName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name))
                return false;

            switch (name)
            {
                case "Scientist":
                case "Murderer":
                case "Tunnel Dweller":
                case "Underwater Dweller":
                case "Bandit Guard":
                case "Scarecrow":
                case "Heavy Scientist":
                    return true;
                default:
                    return false;
            }
        }

        private void RemoveNpcRecords()
        {
            var toRemove = new List<ulong>();
            foreach (var pair in _data.Players)
            {
                if (IsNpcRecord(pair.Value))
                    toRemove.Add(pair.Key);
            }

            foreach (var userId in toRemove)
                _data.Players.Remove(userId);
        }

        private void MarkPlayerDirty(ulong userId)
        {
            if (userId == 0UL)
                return;

            _dirtyPlayers.Add(userId);

            var teamId = GetCurrentTeamId(userId);
            if (teamId != 0UL)
                _dirtyTeams.Add(teamId);
        }

        private void MarkTeamDirty(ulong teamId)
        {
            if (teamId != 0UL)
                _dirtyTeams.Add(teamId);
        }

        private PlayerRecord GetOrCreatePlayerRecord(ulong userId, string name)
        {
            PlayerRecord record;
            if (!_data.Players.TryGetValue(userId, out record))
            {
                record = new PlayerRecord
                {
                    UserId = userId,
                    LastKnownName = string.IsNullOrWhiteSpace(name) ? userId.ToString() : name,
                    LastSeenUtc = DateTime.UtcNow,
                    CurrentWipe = new ScopeStats(),
                    Lifetime = new ScopeStats()
                };
                _data.Players[userId] = record;
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                record.LastKnownName = name;
            }

            return record;
        }

        private TeamRecord GetOrCreateTeamRecord(ulong teamId)
        {
            TeamRecord record;
            if (!_data.Teams.TryGetValue(teamId, out record))
            {
                record = new TeamRecord
                {
                    TeamId = teamId,
                    MemberUserIds = new List<ulong>(),
                    CurrentWipe = new ScopeStats(),
                    Lifetime = new ScopeStats()
                };
                _data.Teams[teamId] = record;
            }

            return record;
        }

        private ScopeStats GetScopeStats(PlayerRecord record, RankScope scope)
        {
            return scope == RankScope.Lifetime ? record.Lifetime : record.CurrentWipe;
        }

        private RankScope ResolveScope(string[] args)
        {
            if (args == null)
                return RankScope.CurrentWipe;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "lifetime":
                        return RankScope.Lifetime;
                    case "team":
                        return RankScope.Team;
                    case "wipe":
                    case "current":
                    case "currentwipe":
                        return RankScope.CurrentWipe;
                }
            }

            return RankScope.CurrentWipe;
        }

        private RankScope ParseScope(string value)
        {
            switch ((value ?? string.Empty).ToLowerInvariant())
            {
                case "lifetime":
                    return RankScope.Lifetime;
                case "team":
                    return RankScope.Team;
                default:
                    return RankScope.CurrentWipe;
            }
        }

        private string ScopeToArg(RankScope scope)
        {
            switch (scope)
            {
                case RankScope.Lifetime:
                    return "lifetime";
                case RankScope.Team:
                    return "team";
                default:
                    return "current";
            }
        }

        private string GetScopeTitle(RankScope scope)
        {
            switch (scope)
            {
                case RankScope.Lifetime:
                    return "Lifetime";
                case RankScope.Team:
                    return "Team";
                default:
                    return "Current Wipe";
            }
        }

        private string GetPageTitle(string page, RankScope scope)
        {
            switch (page)
            {
                case "pvp":
                    return "PvP Metrics • " + GetScopeTitle(scope);
                case "farm":
                    return "Farming Metrics • " + GetScopeTitle(scope);
                case "build":
                    return "Building Metrics • " + GetScopeTitle(scope);
                case "survival":
                    return "Survival Metrics • " + GetScopeTitle(scope);
                case "top":
                    return "Top Rankings • " + GetScopeTitle(scope);
                case "players":
                    return "Players • " + GetScopeTitle(scope);
                default:
                    return "Overview • " + GetScopeTitle(scope);
            }
        }

        private void ApplyGather(FarmStats farm, string shortname, int amount)
        {
            if (farm == null || string.IsNullOrWhiteSpace(shortname) || amount <= 0)
                return;

            switch (shortname)
            {
                case "wood":
                    farm.WoodGathered += amount;
                    break;
                case "stones":
                    farm.StoneGathered += amount;
                    break;
                case "metal.ore":
                    farm.MetalGathered += amount;
                    break;
                case "sulfur.ore":
                    farm.SulfurGathered += amount;
                    break;
            }

            farm.NodesHarvested++;
        }

        private float GetKdr(PvPStats pvp)
        {
            if (pvp == null)
                return 0f;

            return pvp.Kills / (float)Mathf.Max(1, pvp.Deaths);
        }

        private string FormatDuration(float totalSeconds)
        {
            var ts = TimeSpan.FromSeconds(Mathf.Max(0f, totalSeconds));
            if (ts.TotalHours >= 1d)
                return string.Format("{0:%h}h {0:%m}m", ts);

            return string.Format("{0:%m}m {0:%s}s", ts);
        }

        private float CalculateGatherPerHour(ScopeStats stats)
        {
            var total = stats.Farm.WoodGathered + stats.Farm.StoneGathered + stats.Farm.MetalGathered + stats.Farm.SulfurGathered;
            var hours = Mathf.Max(1f, stats.Survival.SecondsPlayed / 3600f);
            return total / hours;
        }

        private float CalculateBuildPerHour(ScopeStats stats)
        {
            var hours = Mathf.Max(1f, stats.Survival.SecondsPlayed / 3600f);
            return stats.Build.StructuresBuilt / hours;
        }

        private int GetPlayerRank(ulong userId, RankScope scope)
        {
            var key = GetLeaderboardCacheKey(scope == RankScope.Team ? RankScope.CurrentWipe : scope, "overall");
            List<LeaderboardEntry> entries;
            if (!_leaderboardCache.TryGetValue(key, out entries))
                return 0;

            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].EntryType == "player" && entries[i].Id == userId)
                    return i + 1;
            }

            return 0;
        }

        #endregion

        #region Models

        private enum RankScope
        {
            CurrentWipe,
            Lifetime,
            Team
        }

        private class RuntimePlayerState
        {
            public DateTime CurrentLifeStartUtc;
            public ulong LastKnownTeamId;
        }

        private class StoredData
        {
            public Dictionary<ulong, PlayerRecord> Players = new Dictionary<ulong, PlayerRecord>();
            public Dictionary<ulong, TeamRecord> Teams = new Dictionary<ulong, TeamRecord>();
            public MapFingerprint MapFingerprint = new MapFingerprint();
        }

        private class PlayerRecord
        {
            public ulong UserId;
            public string LastKnownName;
            public DateTime LastSeenUtc;
            public bool IsNpc;
            public string LastKnownCurrentWipeTier = string.Empty;
            public ScopeStats CurrentWipe = new ScopeStats();
            public ScopeStats Lifetime = new ScopeStats();
        }

        private class TeamRecord
        {
            public ulong TeamId;
            public List<ulong> MemberUserIds = new List<ulong>();
            public ScopeStats CurrentWipe = new ScopeStats();
            public ScopeStats Lifetime = new ScopeStats();
        }

        private class ScopeStats
        {
            public PvPStats PvP = new PvPStats();
            public FarmStats Farm = new FarmStats();
            public BuildStats Build = new BuildStats();
            public SurvivalStats Survival = new SurvivalStats();
            public SupportStats Support = new SupportStats();
            public ScoreCache ScoreCache = new ScoreCache();
        }

        private class PvPStats
        {
            public int Kills;
            public int Deaths;
            public int Headshots;
            public float DamageDealt;
            public float DamageTaken;
            public int KillStreakCurrent;
            public int KillStreakBest;
        }

        private class FarmStats
        {
            public int WoodGathered;
            public int StoneGathered;
            public int MetalGathered;
            public int SulfurGathered;
            public int NodesHarvested;
        }

        private class BuildStats
        {
            public int StructuresBuilt;
            public int StructuresUpgraded;
            public int RepairsPerformed;
        }

        private class SurvivalStats
        {
            public float SecondsPlayed;
            public float LongestLifeSeconds;
            public float DistanceTraveled;
            public int Respawns;
        }

        private class SupportStats
        {
            public float TeamContributions;
        }

        private class ScoreCache
        {
            public float PvPScore;
            public float FarmScore;
            public float BuildScore;
            public float SurvivalScore;
            public float TeamScore;
            public float OverallScore;
        }

        private class LeaderboardEntry
        {
            public ulong Id;
            public string DisplayName;
            public float Score;
            public string EntryType;
            public int PreviousRank;
            public int RankDelta;
        }

        private class MapFingerprint
        {
            public int Seed;
            public int Size;
            public string Url;
            public string Name;
            public bool Initialized;

            public override bool Equals(object obj)
            {
                var other = obj as MapFingerprint;
                if (other == null)
                    return false;

                return Seed == other.Seed &&
                       Size == other.Size &&
                       string.Equals(Url, other.Url, StringComparison.Ordinal) &&
                       string.Equals(Name, other.Name, StringComparison.Ordinal);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = Seed;
                    hash = (hash * 397) ^ Size;
                    hash = (hash * 397) ^ (Url != null ? Url.GetHashCode() : 0);
                    hash = (hash * 397) ^ (Name != null ? Name.GetHashCode() : 0);
                    return hash;
                }
            }
        }

        #endregion

        #region Config

        private class PluginConfig
        {
            [JsonProperty(PropertyName = "General")]
            public GeneralSettings General = new GeneralSettings();

            [JsonProperty(PropertyName = "Performance")]
            public PerformanceSettings Performance = new PerformanceSettings();

            [JsonProperty(PropertyName = "Ratings")]
            public RatingSettings Ratings = new RatingSettings();

            [JsonProperty(PropertyName = "Discord")]
            public DiscordSettings Discord = new DiscordSettings();

            [JsonProperty(PropertyName = "Wipe")]
            public WipeSettings Wipe = new WipeSettings();

            public static PluginConfig CreateDefault()
            {
                return new PluginConfig();
            }
        }

        private class GeneralSettings
        {
            [JsonProperty(PropertyName = "UiTitle")]
            public string UiTitle = "RustStorm Rank";
            [JsonProperty(PropertyName = "ChatCommand")]
            public string ChatCommand = "rank";

            [JsonProperty(PropertyName = "AdminPermission")]
            public string AdminPermission = DefaultAdminPermission;

            [JsonProperty(PropertyName = "EnableTierUpChatMessage")]
            public bool EnableTierUpChatMessage = true;

            [JsonProperty(PropertyName = "EnableTierUpEffect")]
            public bool EnableTierUpEffect = true;

            [JsonProperty(PropertyName = "TierUpEffectPrefab")]
            public string TierUpEffectPrefab = "assets/prefabs/misc/halloween/lootbag/effects/gold_open.prefab";
        }

        private class PerformanceSettings
        {
            [JsonProperty(PropertyName = "SaveIntervalSeconds")]
            public float SaveIntervalSeconds = 300f;

            [JsonProperty(PropertyName = "RecalculateIntervalSeconds")]
            public float RecalculateIntervalSeconds = 120f;

            [JsonProperty(PropertyName = "ActivityTickSeconds")]
            public float ActivityTickSeconds = 15f;

            [JsonProperty(PropertyName = "CleanupIntervalSeconds")]
            public float CleanupIntervalSeconds = 600f;

            [JsonProperty(PropertyName = "MaxDirtyPlayersPerPass")]
            public int MaxDirtyPlayersPerPass = 50;

            [JsonProperty(PropertyName = "CacheLeaderboardEntries")]
            public int CacheLeaderboardEntries = 50;
        }

        private class RatingSettings
        {
            [JsonProperty(PropertyName = "UseConfidenceScaling")]
            public bool UseConfidenceScaling = true;

            [JsonProperty(PropertyName = "ConfidenceThreshold")]
            public float ConfidenceThreshold = 100f;

            [JsonProperty(PropertyName = "OverallWeights")]
            public OverallWeightSettings OverallWeights = new OverallWeightSettings();
        }

        private class OverallWeightSettings
        {
            [JsonProperty(PropertyName = "PvP")]
            public float PvP = 0.30f;

            [JsonProperty(PropertyName = "Farm")]
            public float Farm = 0.20f;

            [JsonProperty(PropertyName = "Build")]
            public float Build = 0.20f;

            [JsonProperty(PropertyName = "Survival")]
            public float Survival = 0.15f;
        }

        private class DiscordSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled = false;

            [JsonProperty(PropertyName = "WebhookURL")]
            public string WebhookURL = string.Empty;

            [JsonProperty(PropertyName = "EmbedColor")]
            public int EmbedColor = unchecked((int)0x55C1FF);

            [JsonProperty(PropertyName = "AuthorName")]
            public string AuthorName = "RustStorm";

            [JsonProperty(PropertyName = "BannerImageUrl")]
            public string BannerImageUrl = string.Empty;

            [JsonProperty(PropertyName = "ThumbnailUrl")]
            public string ThumbnailUrl = string.Empty;

            [JsonProperty(PropertyName = "AvatarUrl")]
            public string AvatarUrl = string.Empty;

            [JsonProperty(PropertyName = "DailyPost")]
            public DailyPostSettings DailyPost = new DailyPostSettings();
        }

        private class DailyPostSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled = false;

            [JsonProperty(PropertyName = "PostHour")]
            public int PostHour = 18;

            [JsonProperty(PropertyName = "PostMinute")]
            public int PostMinute = 0;

            [JsonProperty(PropertyName = "Category")]
            public string Category = "overall";

            [JsonProperty(PropertyName = "Scope")]
            public string Scope = "current";

            [JsonProperty(PropertyName = "TimeZoneId")]
            public string TimeZoneId = "Australia/Perth";
        }

        private class WipeSettings
        {
            [JsonProperty(PropertyName = "AutoDetectMapWipe")]
            public bool AutoDetectMapWipe = true;

            [JsonProperty(PropertyName = "ArchivePreviousWipe")]
            public bool ArchivePreviousWipe = true;
        }

        #endregion

        #region Discord Models

        private class DiscordWebhookPayload
        {
            [JsonProperty("username")]
            public string Username;

            [JsonProperty("avatar_url")]
            public string AvatarUrl;
[JsonProperty("embeds")]
            public List<DiscordEmbed> Embeds;
        }

        private class DiscordEmbed
        {
            [JsonProperty("title")]
            public string Title;

            [JsonProperty("description")]
            public string Description;

            [JsonProperty("color")]
            public int Color;

            [JsonProperty("footer")]
            public DiscordFooter Footer;

            [JsonProperty("author")]
            public DiscordAuthor Author;

            [JsonProperty("thumbnail")]
            public DiscordImage Thumbnail;

            [JsonProperty("image")]
            public DiscordImage Image;

            [JsonProperty("timestamp")]
            public string Timestamp;
        }

        private class DiscordFooter
        {
            [JsonProperty("text")]
            public string Text;
        }

        private class DiscordAuthor
        {
            [JsonProperty("name")]
            public string Name;
        }

        private class DiscordImage
        {
            [JsonProperty("url")]
            public string Url;
        }

        #endregion
    }
}
