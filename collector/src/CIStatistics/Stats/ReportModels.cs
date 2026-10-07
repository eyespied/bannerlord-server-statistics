using System;
using System.Text.Json.Serialization;
namespace CIStatistics.Stats
{
    internal sealed class MatchRow
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("server_name")]
        public string ServerName { get; set; } = string.Empty;

        [JsonPropertyName("map_id")]
        public string MapId { get; set; } = string.Empty;

        [JsonPropertyName("game_type")]
        public string GameType { get; set; } = "Skirmish";

        [JsonPropertyName("started_at")]
        public DateTimeOffset StartedAt { get; set; }

        [JsonPropertyName("ended_at")]
        public DateTimeOffset? EndedAt { get; set; }

        [JsonPropertyName("rounds_played")]
        public int RoundsPlayed { get; set; }

        [JsonPropertyName("winning_side")]
        public string? WinningSide { get; set; }
    }

    /// <summary>PostgREST RPC body using snake_case parameter names expected by Postgres.</summary>
    internal sealed class RoundPlayerRpcPayload
    {
        public Guid p_match_id { get; set; }
        public int p_round_number { get; set; }
        public string p_steam_id { get; set; } = string.Empty;
        public string p_display_name { get; set; } = string.Empty;
        public string p_team_side { get; set; } = string.Empty;
        public string p_class_group { get; set; } = "Infantry";
        public string p_culture_id { get; set; } = string.Empty;
        public int p_score { get; set; }
        public int p_kills { get; set; }
        public int p_deaths { get; set; }
        public int p_assists { get; set; }
        public bool p_is_mvp { get; set; }
        public bool p_is_winner { get; set; }
        public bool p_first_kill { get; set; }
        public bool p_first_death { get; set; }
        public int p_horse_damage { get; set; }
        public int p_horse_kills { get; set; }
        public int p_kicks { get; set; }
        public int p_couches { get; set; }
        public int p_shots { get; set; }
        public int p_hits { get; set; }
        public int p_headshots { get; set; }
        public int p_teamkills { get; set; }
        public int p_team_hits { get; set; }
        public int p_team_damage { get; set; }
        public int p_melee_damage { get; set; }
        public int p_mounted_damage { get; set; }
        public int p_ranged_damage { get; set; }
        public int p_kills_melee { get; set; }
        public int p_kills_ranged { get; set; }
        public int p_kills_throwing { get; set; }
        public int p_kills_mounted_melee { get; set; }
        public int p_kills_mounted_ranged { get; set; }
        public int p_kills_mounted_throwing { get; set; }
        public int p_deaths_melee { get; set; }
        public int p_deaths_ranged { get; set; }
        public int p_deaths_throwing { get; set; }
        public int p_deaths_mounted_melee { get; set; }
        public int p_deaths_mounted_ranged { get; set; }
        public int p_deaths_mounted_throwing { get; set; }
        public int p_suicides { get; set; }
        public int p_throwing_damage { get; set; }
        public int p_mounted_ranged_damage { get; set; }
        public int p_mounted_throwing_damage { get; set; }
        public int p_throwing_shots { get; set; }
        public int p_throwing_hits { get; set; }
        public int p_throwing_headshots { get; set; }
        public int p_team_hits_received { get; set; }
        public int p_team_damage_received { get; set; }
        public int p_deaths_by_teammate { get; set; }
        public int p_alive_ms { get; set; }
        public int p_spawn_count { get; set; }
    }
}
