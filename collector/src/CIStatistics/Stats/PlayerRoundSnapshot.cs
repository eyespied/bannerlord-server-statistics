using System;

namespace CIStatistics.Stats
{
    internal sealed class PlayerRoundSnapshot
    {
        public string SteamId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string TeamSide { get; set; } = string.Empty;
        public string ClassGroup { get; set; } = "Infantry";
        public string CultureId { get; set; } = string.Empty;

        public int BaselineKills { get; set; }
        public int BaselineDeaths { get; set; }
        public int BaselineAssists { get; set; }
        public int BaselineScore { get; set; }

        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Assists { get; set; }
        public int Score { get; set; }

        public bool IsMvp { get; set; }
        public bool IsWinner { get; set; }
        public bool FirstKill { get; set; }
        public bool FirstDeath { get; set; }

        public int HorseDamage { get; set; }
        public int HorseKills { get; set; }
        public int Kicks { get; set; }
        public int Couches { get; set; }

        public int Shots { get; set; }
        public int Hits { get; set; }
        public int Headshots { get; set; }

        public int ThrowingShots { get; set; }
        public int ThrowingHits { get; set; }
        public int ThrowingHeadshots { get; set; }

        public int Teamkills { get; set; }
        public int TeamHits { get; set; }
        public int TeamDamage { get; set; }
        public int TeamHitsReceived { get; set; }
        public int TeamDamageReceived { get; set; }
        public int DeathsByTeammate { get; set; }

        public int MeleeDamage { get; set; }
        public int MountedDamage { get; set; }
        public int RangedDamage { get; set; }
        public int ThrowingDamage { get; set; }
        public int MountedRangedDamage { get; set; }
        public int MountedThrowingDamage { get; set; }

        public int KillsMelee { get; set; }
        public int KillsRanged { get; set; }
        public int KillsThrowing { get; set; }
        public int KillsMountedMelee { get; set; }
        public int KillsMountedRanged { get; set; }
        public int KillsMountedThrowing { get; set; }

        public int DeathsMelee { get; set; }
        public int DeathsRanged { get; set; }
        public int DeathsThrowing { get; set; }
        public int DeathsMountedMelee { get; set; }
        public int DeathsMountedRanged { get; set; }
        public int DeathsMountedThrowing { get; set; }
        public int Suicides { get; set; }

        public int AliveMs { get; set; }
        public int SpawnCount { get; set; }
        public float? SpawnMissionTime { get; set; }

        public void ApplyMissionPeerDeltas(int kills, int deaths, int assists, int score)
        {
            Kills = Math.Max(0, kills - BaselineKills);
            Deaths = Math.Max(0, deaths - BaselineDeaths);
            Assists = Math.Max(0, assists - BaselineAssists);
            Score = Math.Max(0, score - BaselineScore);
        }
    }
}
