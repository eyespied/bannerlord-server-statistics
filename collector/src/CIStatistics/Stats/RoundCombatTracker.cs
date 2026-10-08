using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CIStatistics.Stats
{
    /// <summary>
    /// Tracks live-round combat stats and sends immutable reports to the website.
    /// Ignores warmup. No minimum player count.
    /// Snapshots MissionPeer K/D/A/Score at round start; custom hit/kill
    /// classification via OnScoreHit / OnAgentRemoved / OnAgentShootMissile.
    /// </summary>
    internal sealed class RoundCombatTracker : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        private DateTimeOffset _roundStartedAt;

        private readonly StatsConfig _config;
        private RoundUploader? _client;

        private MultiplayerRoundController? _roundController;
        private MultiplayerWarmupComponent? _warmup;
        private MissionScoreboardComponent? _scoreboard;

        private readonly Dictionary<string, PlayerRoundSnapshot> _roundStats = new Dictionary<string, PlayerRoundSnapshot>();
        private Guid? _matchId;
        private DateTimeOffset _matchStartedAt;
        private int _roundsPlayed;
        private bool _trackingRound;
        internal string StatusText => _client == null ? "Not ready: collector failed to start; check server logs."
            : _roundController == null ? "Not recording: this game mode has no round controller."
            : !IsLiveCombat ? "Ready; warmup is excluded."
            : _trackingRound ? $"Recording live round {_roundController.RoundCount} ({_roundStats.Count} player snapshots)."
            : "Ready; waiting for the next live round.";
        private bool _hasFirstKill;
        private bool _hasFirstDeath;
        private bool _matchClosed;
        private string? _lastWinningSide;

        public RoundCombatTracker(StatsConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public override void AfterStart()
        {
            base.AfterStart();
            try
            {
                if (!GameNetwork.IsDedicatedServer || !_config.IsReady)
                {
                    return;
                }

                _client = RoundUploader.Get(_config);
                _roundController = Mission.GetMissionBehavior<MultiplayerRoundController>();
                _warmup = Mission.GetMissionBehavior<MultiplayerWarmupComponent>();
                _scoreboard = Mission.GetMissionBehavior<MissionScoreboardComponent>();

                if (_roundController != null)
                {
                    _roundController.OnRoundStarted += OnRoundStarted;
                    _roundController.OnRoundEnding += OnRoundEnding;
                    _roundController.OnPostRoundEnded += OnPostRoundEnded;
                }

                if (_scoreboard != null)
                {
                    _scoreboard.OnMVPSelected += OnMvpSelected;
                }

                Debug.Print("[CIStatistics.Stats] RoundCombatTracker started.");
            }
            catch (Exception ex)
            {
                Swallow("AfterStart", ex);
                _client = null;
            }
        }

        public override void OnRemoveBehavior()
        {
            try
            {
                if (_roundController != null)
                {
                    _roundController.OnRoundStarted -= OnRoundStarted;
                    _roundController.OnRoundEnding -= OnRoundEnding;
                    _roundController.OnPostRoundEnded -= OnPostRoundEnded;
                }

                if (_scoreboard != null)
                {
                    _scoreboard.OnMVPSelected -= OnMvpSelected;
                }

                CloseMatchIfNeeded("mission_unload");
                // The process-wide uploader survives mission changes; queued rounds are durable.
                _client = null;
            }
            catch (Exception ex)
            {
                Swallow("OnRemoveBehavior", ex);
            }

            base.OnRemoveBehavior();
        }

        private bool IsLiveCombat =>
            GameNetwork.IsDedicatedServer
            && _config.IsReady
            && (_warmup == null || !_warmup.IsInWarmup);

        private void OnRoundStarted()
        {
            try
            {
                OnRoundStartedCore();
            }
            catch (Exception ex)
            {
                _trackingRound = false;
                Swallow("OnRoundStarted", ex);
            }
        }

        private void OnRoundStartedCore()
        {
            _roundStats.Clear();
            _hasFirstKill = false;
            _hasFirstDeath = false;
            _trackingRound = false;

            if (!IsLiveCombat || _roundController == null)
            {
                return;
            }

            CountPlayersPerSide(out int attackers, out int defenders);

            EnsureMatchStarted();
            _trackingRound = true;
            _roundStartedAt = DateTimeOffset.UtcNow;

            foreach (NetworkCommunicator peer in GameNetwork.NetworkPeers)
            {
                MissionPeer? missionPeer = peer.GetComponent<MissionPeer>();
                if (missionPeer == null || !peer.IsSynchronized)
                {
                    continue;
                }

                string steamId = GetSteamId(peer);
                if (string.IsNullOrEmpty(steamId))
                {
                    continue;
                }

                var snap = new PlayerRoundSnapshot
                {
                    SteamId = steamId,
                    DisplayName = peer.UserName ?? string.Empty,
                    TeamSide = missionPeer.Team?.Side.ToString() ?? BattleSideEnum.None.ToString(),
                    ClassGroup = ResolveClassGroup(missionPeer),
                    CultureId = ResolveCultureId(missionPeer),
                    BaselineKills = missionPeer.KillCount,
                    BaselineDeaths = missionPeer.DeathCount,
                    BaselineAssists = missionPeer.AssistCount,
                    BaselineScore = missionPeer.Score,
                };
                _roundStats[steamId] = snap;
            }

            SeedSpawnTimes();

            Debug.Print(
                $"[CIStatistics.Stats] Round {_roundController.RoundCount} tracking started " +
                $"({attackers}v{defenders}, {_roundStats.Count} peers).");
        }

        private static void CountPlayersPerSide(out int attackers, out int defenders)
        {
            attackers = 0;
            defenders = 0;

            Team? attackerTeam = Mission.Current?.AttackerTeam;
            Team? defenderTeam = Mission.Current?.DefenderTeam;

            foreach (NetworkCommunicator peer in GameNetwork.NetworkPeers)
            {
                // Same filters native uses when deciding if a side has players.
                if (!peer.IsSynchronized || !peer.IsConnectionActive)
                {
                    continue;
                }

                MissionPeer? missionPeer = peer.GetComponent<MissionPeer>();
                if (missionPeer?.Team == null || missionPeer.Team.Side == BattleSideEnum.None)
                {
                    continue;
                }

                if (attackerTeam != null && missionPeer.Team == attackerTeam)
                {
                    attackers++;
                }
                else if (defenderTeam != null && missionPeer.Team == defenderTeam)
                {
                    defenders++;
                }
                else if (missionPeer.Team.Side == BattleSideEnum.Attacker)
                {
                    attackers++;
                }
                else if (missionPeer.Team.Side == BattleSideEnum.Defender)
                {
                    defenders++;
                }
            }
        }

        private void OnRoundEnding()
        {
            try
            {
                OnRoundEndingCore();
            }
            catch (Exception ex)
            {
                _trackingRound = false;
                Swallow("OnRoundEnding", ex);
            }
        }

        private void OnRoundEndingCore()
        {
            if (!_trackingRound || _roundController == null || _client == null || _matchId == null)
            {
                return;
            }

            BattleSideEnum winner = _roundController.RoundWinner;
            if (winner != BattleSideEnum.None)
            {
                _lastWinningSide = winner.ToString();
            }

            int roundNumber = Math.Max(1, _roundController.RoundCount);
            CloseOpenSpawns();
            FinalizePeerDeltas(winner);

            var players = new System.Collections.Generic.List<RoundPlayerRpcPayload>();
            foreach (PlayerRoundSnapshot snap in _roundStats.Values)
                if (snap.HasAssignedTeam)
                    players.Add(ToRpcPayload(_matchId.Value, roundNumber, snap));
            if (players.Count > 0)
                _client.EnqueueRound(new RoundReport {
                    match_id = _matchId.Value, round_number = roundNumber,
                    started_at = _roundStartedAt, ended_at = DateTimeOffset.UtcNow,
                    map_id = BuildMatchRow(false).MapId, game_type = BuildMatchRow(false).GameType,
                    players = players
                });
            _roundsPlayed = Math.Max(_roundsPlayed, roundNumber);
            _trackingRound = false;
            Debug.Print($"[CIStatistics.Stats] Round {roundNumber} flushed ({_roundStats.Count} rows).");
        }

        private void OnPostRoundEnded()
        {
            try
            {
                if (_roundController != null && _roundController.IsMatchEnding)
                {
                    CloseMatchIfNeeded("match_ending");
                }
            }
            catch (Exception ex)
            {
                Swallow("OnPostRoundEnded", ex);
            }
        }

        private void OnMvpSelected(MissionPeer peer, int mvpCount)
        {
            try
            {
                if (!_trackingRound || peer?.Peer == null)
                {
                    return;
                }

                string steamId = NormalizeId(peer.Peer.Id.ToString());
                if (_roundStats.TryGetValue(steamId, out PlayerRoundSnapshot? snap))
                {
                    snap.IsMvp = true;
                }
            }
            catch (Exception ex)
            {
                Swallow("OnMvpSelected", ex);
            }
        }

        public override void OnScoreHit(
            Agent affectedAgent,
            Agent affectorAgent,
            WeaponComponentData attackerWeapon,
            bool isBlocked,
            bool isSiegeEngineHit,
            in Blow blow,
            in AttackCollisionData collisionData,
            float damagedHp,
            float hitDistance,
            float shotDifficulty)
        {
            base.OnScoreHit(affectedAgent, affectorAgent, attackerWeapon, isBlocked, isSiegeEngineHit, in blow, in collisionData, damagedHp, hitDistance, shotDifficulty);
            try
            {
                OnScoreHitCore(affectedAgent, affectorAgent, attackerWeapon, isBlocked, damagedHp, in blow);
            }
            catch (Exception ex)
            {
                Swallow("OnScoreHit", ex);
            }
        }

        private void OnScoreHitCore(
            Agent affectedAgent,
            Agent affectorAgent,
            WeaponComponentData attackerWeapon,
            bool isBlocked,
            float damagedHp,
            in Blow blow)
        {
            if (!_trackingRound || !IsLiveCombat || isBlocked || damagedHp <= 0f || affectedAgent == null)
            {
                return;
            }

            Agent? attacker = ResolvePlayerAgent(affectorAgent);
            if (attacker == null)
            {
                return;
            }

            PlayerRoundSnapshot? snap = GetOrCreateSnapshot(attacker);
            if (snap == null)
            {
                return;
            }

            int dmg = (int)Math.Max(1f, Math.Round(damagedHp));
            bool sameTeam = AreSameTeam(attacker, affectedAgent);
            bool isKick = blow.AttackType == AgentAttackType.Kick;
            bool isCouch = attacker.IsDoingPassiveAttack;
            bool isHeadshot = blow.IsHeadShot();
            CombatStyle style = Classify(attacker, attackerWeapon, blow.IsMissile, blow.WeaponRecord.WeaponClass);

            if (affectedAgent.IsMount)
            {
                snap.HorseDamage += dmg;
                if (isKick)
                {
                    snap.Kicks++;
                }

                if (isCouch)
                {
                    snap.Couches++;
                }

                return;
            }

            if (!affectedAgent.IsHuman)
            {
                return;
            }

            if (sameTeam)
            {
                snap.TeamHits++;
                snap.TeamDamage += dmg;
                PlayerRoundSnapshot? victimSnap = GetOrCreateSnapshot(affectedAgent);
                if (victimSnap != null)
                {
                    victimSnap.TeamHitsReceived++;
                    victimSnap.TeamDamageReceived += dmg;
                }

                return;
            }

            AddDamage(snap, style, dmg);
            AddMissileAccuracy(snap, style, isHeadshot);

            if (isKick)
            {
                snap.Kicks++;
            }

            if (isCouch)
            {
                snap.Couches++;
            }
        }

        public override void OnAgentShootMissile(
            Agent shooterAgent,
            EquipmentIndex weaponIndex,
            Vec3 position,
            Vec3 velocity,
            Mat3 orientation,
            bool hasRigidBody,
            int forcedMissileIndex)
        {
            base.OnAgentShootMissile(shooterAgent, weaponIndex, position, velocity, orientation, hasRigidBody, forcedMissileIndex);
            try
            {
                if (!_trackingRound || !IsLiveCombat)
                {
                    return;
                }

                Agent? shooter = ResolvePlayerAgent(shooterAgent);
                PlayerRoundSnapshot? snap = GetOrCreateSnapshot(shooter);
                if (snap == null || shooter == null)
                {
                    return;
                }

                if (IsThrowingClass(ResolveShotWeaponClass(shooter, weaponIndex)))
                {
                    snap.ThrowingShots++;
                }
                else
                {
                    snap.Shots++;
                }
            }
            catch (Exception ex)
            {
                Swallow("OnAgentShootMissile", ex);
            }
        }

        public override void OnAgentBuild(Agent agent, Banner banner)
        {
            base.OnAgentBuild(agent, banner);
            try
            {
                if (!_trackingRound || !IsLiveCombat || agent == null || !agent.IsHuman || !agent.IsPlayerControlled)
                {
                    return;
                }

                PlayerRoundSnapshot? snap = GetOrCreateSnapshot(agent);
                if (snap != null && snap.SpawnMissionTime == null)
                {
                    snap.SpawnMissionTime = Mission.CurrentTime;
                }
            }
            catch (Exception ex)
            {
                Swallow("OnAgentBuild", ex);
            }
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            base.OnAgentRemoved(affectedAgent, affectorAgent, agentState, blow);
            try
            {
                OnAgentRemovedCore(affectedAgent, affectorAgent, agentState, blow);
            }
            catch (Exception ex)
            {
                Swallow("OnAgentRemoved", ex);
            }
        }

        private void OnAgentRemovedCore(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            if (!_trackingRound || !IsLiveCombat || agentState != AgentState.Killed || affectedAgent == null)
            {
                return;
            }

            Agent? killer = ResolvePlayerAgent(affectorAgent);

            if (affectedAgent.IsMount)
            {
                PlayerRoundSnapshot? mountKillerSnap = GetOrCreateSnapshot(killer);
                if (mountKillerSnap != null && killer != null && !AreSameTeam(killer, affectedAgent.RiderAgent ?? affectedAgent))
                {
                    mountKillerSnap.HorseKills++;
                }

                return;
            }

            if (!affectedAgent.IsHuman || !affectedAgent.IsPlayerControlled)
            {
                return;
            }

            PlayerRoundSnapshot? victimSnap = GetOrCreateSnapshot(affectedAgent);
            if (victimSnap != null)
            {
                CloseSpawn(victimSnap);
                if (!_hasFirstDeath)
                {
                    victimSnap.FirstDeath = true;
                    _hasFirstDeath = true;
                }
            }

            if (killer == null || killer == affectedAgent)
            {
                if (victimSnap != null)
                {
                    victimSnap.Suicides++;
                }

                return;
            }

            PlayerRoundSnapshot? killerSnap = GetOrCreateSnapshot(killer);
            if (killerSnap == null)
            {
                return;
            }

            CombatStyle style = Classify(killer, null, blow.IsMissile, (WeaponClass)blow.WeaponClass);

            if (AreSameTeam(killer, affectedAgent))
            {
                killerSnap.Teamkills++;
                if (victimSnap != null)
                {
                    victimSnap.DeathsByTeammate++;
                }

                return;
            }

            AddKill(killerSnap, style);
            if (victimSnap != null)
            {
                AddDeath(victimSnap, style);
            }

            if (!_hasFirstKill)
            {
                killerSnap.FirstKill = true;
                _hasFirstKill = true;
            }
        }

        private void FinalizePeerDeltas(BattleSideEnum winner)
        {
            foreach (NetworkCommunicator peer in GameNetwork.NetworkPeers)
            {
                MissionPeer? missionPeer = peer.GetComponent<MissionPeer>();
                if (missionPeer == null)
                {
                    continue;
                }

                string steamId = GetSteamId(peer);
                if (string.IsNullOrEmpty(steamId))
                {
                    continue;
                }

                if (!_roundStats.TryGetValue(steamId, out PlayerRoundSnapshot? snap))
                {
                    snap = new PlayerRoundSnapshot
                    {
                        SteamId = steamId,
                        BaselineKills = missionPeer.KillCount,
                        BaselineDeaths = missionPeer.DeathCount,
                        BaselineAssists = missionPeer.AssistCount,
                        BaselineScore = missionPeer.Score,
                    };
                    _roundStats[steamId] = snap;
                }

                snap.DisplayName = peer.UserName ?? snap.DisplayName;
                snap.TeamSide = missionPeer.Team?.Side.ToString() ?? snap.TeamSide;
                snap.ClassGroup = ResolveClassGroup(missionPeer);
                string cultureId = ResolveCultureId(missionPeer);
                if (!string.IsNullOrEmpty(cultureId))
                {
                    snap.CultureId = cultureId;
                }
                snap.ApplyMissionPeerDeltas(
                    missionPeer.KillCount,
                    missionPeer.DeathCount,
                    missionPeer.AssistCount,
                    missionPeer.Score);
                snap.IsWinner = winner != BattleSideEnum.None
                    && missionPeer.Team != null
                    && missionPeer.Team.Side == winner;
            }
        }

        private void EnsureMatchStarted()
        {
            if (_matchId != null || _client == null)
            {
                return;
            }

            _matchId = Guid.NewGuid();
            _matchStartedAt = DateTimeOffset.UtcNow;
            _roundsPlayed = 0;
            _matchClosed = false;
            _lastWinningSide = null;
            Debug.Print($"[CIStatistics.Stats] Match {_matchId} started.");
        }

        private void CloseMatchIfNeeded(string reason)
        {
            if (_matchClosed || _matchId == null || _client == null)
            {
                return;
            }

            _matchClosed = true;
            MatchRow row = BuildMatchRow(ended: true);
            Debug.Print($"[CIStatistics.Stats] Match {_matchId} closed ({reason}).");
        }

        private MatchRow BuildMatchRow(bool ended)
        {
            string mapId = string.Empty;
            try
            {
                mapId = MultiplayerOptions.OptionType.Map.GetStrValue() ?? string.Empty;
            }
            catch
            {
                mapId = Mission?.SceneName ?? string.Empty;
            }

            string gameType = "Skirmish";
            try
            {
                MissionLobbyComponent? lobby = Mission?.GetMissionBehavior<MissionLobbyComponent>();
                if (lobby != null)
                {
                    gameType = lobby.MissionType.ToString();
                }
            }
            catch
            {
                // keep default
            }

            return new MatchRow
            {
                Id = _matchId ?? Guid.Empty,
                ServerName = _config.ServerName,
                MapId = mapId,
                GameType = gameType,
                StartedAt = _matchStartedAt == default ? DateTimeOffset.UtcNow : _matchStartedAt,
                EndedAt = ended ? DateTimeOffset.UtcNow : null,
                RoundsPlayed = _roundsPlayed,
                WinningSide = ended ? _lastWinningSide : null,
            };
        }

        private PlayerRoundSnapshot? GetOrCreateSnapshot(Agent? agent)
        {
            if (agent?.MissionPeer?.Peer == null)
            {
                return null;
            }

            string steamId = NormalizeId(agent.MissionPeer.Peer.Id.ToString());
            if (string.IsNullOrEmpty(steamId))
            {
                return null;
            }

            if (!_roundStats.TryGetValue(steamId, out PlayerRoundSnapshot? snap))
            {
                MissionPeer peer = agent.MissionPeer;
                snap = new PlayerRoundSnapshot
                {
                    SteamId = steamId,
                    DisplayName = peer.Name ?? string.Empty,
                    TeamSide = peer.Team?.Side.ToString() ?? BattleSideEnum.None.ToString(),
                    ClassGroup = ResolveClassGroup(peer),
                    CultureId = ResolveCultureId(peer),
                    BaselineKills = peer.KillCount,
                    BaselineDeaths = peer.DeathCount,
                    BaselineAssists = peer.AssistCount,
                    BaselineScore = peer.Score,
                };
                _roundStats[steamId] = snap;
            }

            return snap;
        }

        private static Agent? ResolvePlayerAgent(Agent? agent)
        {
            if (agent == null)
            {
                return null;
            }

            if (agent.IsMount)
            {
                agent = agent.RiderAgent;
            }

            if (agent == null || !agent.IsHuman || !agent.IsPlayerControlled)
            {
                return null;
            }

            return agent;
        }

        private static bool AreSameTeam(Agent a, Agent b)
        {
            if (a?.Team == null || b?.Team == null)
            {
                return false;
            }

            return a.Team.Side == b.Team.Side && a.Team.Side != BattleSideEnum.None;
        }

        private static string NormalizeId(string id)
        {
            string last = id.Substring(id.LastIndexOf('.') + 1);
            return last.Length == 17 && ulong.TryParse(last, out _) ? last : id;
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            // Keep disconnected players' last observed scoreboard deltas.
            if (_trackingRound) FinalizePeerDeltas(BattleSideEnum.None);
        }

        private static string GetSteamId(NetworkCommunicator peer)
        {
            try
            {
                return NormalizeId(peer.VirtualPlayer?.Id.ToString() ?? string.Empty);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ResolveClassGroup(MissionPeer peer)
        {
            try
            {
                MultiplayerClassDivisions.MPHeroClass? heroClass = MultiplayerClassDivisions.GetMPHeroClassForPeer(peer);
                string? groupId = heroClass?.ClassGroup?.StringId;
                if (string.IsNullOrEmpty(groupId))
                {
                    return "Infantry";
                }

                // HorseArcher counts as Cavalry per plan.
                if (string.Equals(groupId, "HorseArcher", StringComparison.OrdinalIgnoreCase))
                {
                    return "Cavalry";
                }

                if (string.Equals(groupId, "Infantry", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(groupId, "Ranged", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(groupId, "Cavalry", StringComparison.OrdinalIgnoreCase))
                {
                    return groupId;
                }

                return "Infantry";
            }
            catch
            {
                return "Infantry";
            }
        }

        private static RoundPlayerRpcPayload ToRpcPayload(Guid matchId, int roundNumber, PlayerRoundSnapshot snap)
        {
            return new RoundPlayerRpcPayload
            {
                p_match_id = matchId,
                p_round_number = roundNumber,
                p_steam_id = snap.SteamId,
                p_display_name = snap.DisplayName,
                p_team_side = snap.TeamSide,
                p_class_group = snap.ClassGroup,
                p_culture_id = snap.CultureId,
                p_score = snap.Score,
                p_kills = snap.Kills,
                p_deaths = snap.Deaths,
                p_assists = snap.Assists,
                p_is_mvp = snap.IsMvp,
                p_is_winner = snap.IsWinner,
                p_first_kill = snap.FirstKill,
                p_first_death = snap.FirstDeath,
                p_horse_damage = snap.HorseDamage,
                p_horse_kills = snap.HorseKills,
                p_kicks = snap.Kicks,
                p_couches = snap.Couches,
                p_shots = snap.Shots,
                p_hits = snap.Hits,
                p_headshots = snap.Headshots,
                p_teamkills = snap.Teamkills,
                p_team_hits = snap.TeamHits,
                p_team_damage = snap.TeamDamage,
                p_melee_damage = snap.MeleeDamage,
                p_mounted_damage = snap.MountedDamage,
                p_ranged_damage = snap.RangedDamage,
                p_kills_melee = snap.KillsMelee,
                p_kills_ranged = snap.KillsRanged,
                p_kills_throwing = snap.KillsThrowing,
                p_kills_mounted_melee = snap.KillsMountedMelee,
                p_kills_mounted_ranged = snap.KillsMountedRanged,
                p_kills_mounted_throwing = snap.KillsMountedThrowing,
                p_deaths_melee = snap.DeathsMelee,
                p_deaths_ranged = snap.DeathsRanged,
                p_deaths_throwing = snap.DeathsThrowing,
                p_deaths_mounted_melee = snap.DeathsMountedMelee,
                p_deaths_mounted_ranged = snap.DeathsMountedRanged,
                p_deaths_mounted_throwing = snap.DeathsMountedThrowing,
                p_suicides = snap.Suicides,
                p_throwing_damage = snap.ThrowingDamage,
                p_mounted_ranged_damage = snap.MountedRangedDamage,
                p_mounted_throwing_damage = snap.MountedThrowingDamage,
                p_throwing_shots = snap.ThrowingShots,
                p_throwing_hits = snap.ThrowingHits,
                p_throwing_headshots = snap.ThrowingHeadshots,
                p_team_hits_received = snap.TeamHitsReceived,
                p_team_damage_received = snap.TeamDamageReceived,
                p_deaths_by_teammate = snap.DeathsByTeammate,
                p_alive_ms = snap.AliveMs,
                p_spawn_count = snap.SpawnCount,
            };
        }

        private void SeedSpawnTimes()
        {
            if (Mission?.Agents == null)
            {
                return;
            }

            float now = Mission.CurrentTime;
            foreach (Agent agent in Mission.Agents)
            {
                if (agent == null || !agent.IsActive() || !agent.IsHuman || !agent.IsPlayerControlled)
                {
                    continue;
                }

                PlayerRoundSnapshot? snap = GetOrCreateSnapshot(agent);
                if (snap != null && snap.SpawnMissionTime == null)
                {
                    snap.SpawnMissionTime = now;
                }
            }
        }

        private void CloseOpenSpawns()
        {
            float now = Mission.CurrentTime;
            foreach (PlayerRoundSnapshot snap in _roundStats.Values)
            {
                CloseSpawn(snap, now);
            }
        }

        private void CloseSpawn(PlayerRoundSnapshot snap)
        {
            CloseSpawn(snap, Mission.CurrentTime);
        }

        private static void CloseSpawn(PlayerRoundSnapshot snap, float now)
        {
            if (snap.SpawnMissionTime == null)
            {
                return;
            }

            int ms = Math.Max(0, (int)Math.Round((now - snap.SpawnMissionTime.Value) * 1000f));
            snap.AliveMs += ms;
            snap.SpawnCount++;
            snap.SpawnMissionTime = null;
        }

        private enum CombatStyle
        {
            Melee,
            Ranged,
            Throwing,
            MountedMelee,
            MountedRanged,
            MountedThrowing,
        }

        private static CombatStyle Classify(Agent? attacker, WeaponComponentData? weapon, bool isMissile, WeaponClass weaponClass)
        {
            bool mounted = attacker != null && attacker.HasMount;
            bool throwing = isMissile && (IsThrowingClass(weaponClass) || IsThrowingWeapon(weapon));
            if (throwing)
            {
                return mounted ? CombatStyle.MountedThrowing : CombatStyle.Throwing;
            }

            bool ranged = isMissile || (weapon != null && weapon.IsRangedWeapon && !weapon.IsConsumable);
            if (ranged)
            {
                return mounted ? CombatStyle.MountedRanged : CombatStyle.Ranged;
            }

            return mounted ? CombatStyle.MountedMelee : CombatStyle.Melee;
        }

        private static bool IsThrowingWeapon(WeaponComponentData? weapon)
        {
            if (weapon == null)
            {
                return false;
            }

            return IsThrowingClass(weapon.WeaponClass)
                || (weapon.IsRangedWeapon && weapon.IsConsumable);
        }

        private static bool IsThrowingClass(WeaponClass weaponClass)
        {
            return weaponClass == WeaponClass.ThrowingAxe
                || weaponClass == WeaponClass.ThrowingKnife
                || weaponClass == WeaponClass.Javelin
                || weaponClass == WeaponClass.Stone;
        }

        private static WeaponClass ResolveShotWeaponClass(Agent shooter, EquipmentIndex weaponIndex)
        {
            try
            {
                if (weaponIndex != EquipmentIndex.None)
                {
                    MissionWeapon mw = shooter.Equipment[weaponIndex];
                    if (!mw.IsEmpty && mw.CurrentUsageItem != null)
                    {
                        return mw.CurrentUsageItem.WeaponClass;
                    }
                }

                if (!shooter.WieldedWeapon.IsEmpty && shooter.WieldedWeapon.CurrentUsageItem != null)
                {
                    return shooter.WieldedWeapon.CurrentUsageItem.WeaponClass;
                }
            }
            catch
            {
                // fall through
            }

            return WeaponClass.Undefined;
        }

        private static void AddDamage(PlayerRoundSnapshot snap, CombatStyle style, int dmg)
        {
            switch (style)
            {
                case CombatStyle.Melee:
                    snap.MeleeDamage += dmg;
                    break;
                case CombatStyle.Ranged:
                    snap.RangedDamage += dmg;
                    break;
                case CombatStyle.Throwing:
                    snap.ThrowingDamage += dmg;
                    break;
                case CombatStyle.MountedMelee:
                    snap.MountedDamage += dmg;
                    break;
                case CombatStyle.MountedRanged:
                    snap.MountedRangedDamage += dmg;
                    break;
                case CombatStyle.MountedThrowing:
                    snap.MountedThrowingDamage += dmg;
                    break;
            }
        }

        private static void AddMissileAccuracy(PlayerRoundSnapshot snap, CombatStyle style, bool isHeadshot)
        {
            if (style == CombatStyle.Ranged || style == CombatStyle.MountedRanged)
            {
                snap.Hits++;
                if (isHeadshot)
                {
                    snap.Headshots++;
                }
            }
            else if (style == CombatStyle.Throwing || style == CombatStyle.MountedThrowing)
            {
                snap.ThrowingHits++;
                if (isHeadshot)
                {
                    snap.ThrowingHeadshots++;
                }
            }
        }

        private static void AddKill(PlayerRoundSnapshot snap, CombatStyle style)
        {
            switch (style)
            {
                case CombatStyle.Melee:
                    snap.KillsMelee++;
                    break;
                case CombatStyle.Ranged:
                    snap.KillsRanged++;
                    break;
                case CombatStyle.Throwing:
                    snap.KillsThrowing++;
                    break;
                case CombatStyle.MountedMelee:
                    snap.KillsMountedMelee++;
                    break;
                case CombatStyle.MountedRanged:
                    snap.KillsMountedRanged++;
                    break;
                case CombatStyle.MountedThrowing:
                    snap.KillsMountedThrowing++;
                    break;
            }
        }

        private static void AddDeath(PlayerRoundSnapshot snap, CombatStyle style)
        {
            switch (style)
            {
                case CombatStyle.Melee:
                    snap.DeathsMelee++;
                    break;
                case CombatStyle.Ranged:
                    snap.DeathsRanged++;
                    break;
                case CombatStyle.Throwing:
                    snap.DeathsThrowing++;
                    break;
                case CombatStyle.MountedMelee:
                    snap.DeathsMountedMelee++;
                    break;
                case CombatStyle.MountedRanged:
                    snap.DeathsMountedRanged++;
                    break;
                case CombatStyle.MountedThrowing:
                    snap.DeathsMountedThrowing++;
                    break;
            }
        }

        private static string ResolveCultureId(MissionPeer peer)
        {
            try
            {
                if (peer.Culture != null && !string.IsNullOrEmpty(peer.Culture.StringId))
                {
                    return peer.Culture.StringId;
                }
            }
            catch
            {
                // fall through to team option
            }

            try
            {
                BattleSideEnum side = peer.Team?.Side ?? BattleSideEnum.None;
                if (side == BattleSideEnum.Attacker)
                {
                    return MultiplayerOptions.OptionType.CultureTeam1.GetStrValue() ?? string.Empty;
                }

                if (side == BattleSideEnum.Defender)
                {
                    return MultiplayerOptions.OptionType.CultureTeam2.GetStrValue() ?? string.Empty;
                }
            }
            catch
            {
                // ignore
            }

            return string.Empty;
        }

        private static void Swallow(string name, Exception ex)
        {
            try
            {
                Debug.Print($"[CIStatistics.Stats] {name} failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
                // never let logging take down the dedicated server
            }
        }
    }
}
