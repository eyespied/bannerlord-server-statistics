using System;
using System.IO;
using System.Text.Json;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CIStatistics.Stats
{
    internal sealed class StatsConfig
    {
        private static readonly object Sync = new object();
        private static StatsConfig? _cached;

        public bool Enabled { get; set; }
        public string IngestUrl { get; set; } = string.Empty;
        public string IngestToken { get; set; } = string.Empty;
        public string ServerName { get; set; } = "Server";
        public string QueueDirectory { get; set; } = "ci-statistics-queue";

        public bool IsReady =>
            Enabled
            && !string.IsNullOrWhiteSpace(IngestToken)
            && !string.Equals(IngestToken, "REPLACE_ME", StringComparison.Ordinal)
            && IsHttpsUrl(IngestUrl);

        private static bool IsHttpsUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                && uri.Scheme == Uri.UriSchemeHttps;
        }

        public static StatsConfig Load()
        {
            lock (Sync)
            {
                if (_cached != null)
                {
                    return _cached;
                }

                foreach (string path in CandidatePaths())
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    try
                    {
                        string json = File.ReadAllText(path);
                        StatsConfig? parsed = JsonSerializer.Deserialize<StatsConfig>(json, JsonOptions());
                        if (parsed != null)
                        {
                            _cached = parsed;
                            Debug.Print($"[CIStatistics.Stats] Loaded config from {path} (enabled={parsed.Enabled}).");
                            return _cached;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.Print($"[CIStatistics.Stats] Failed to read {path}: {ex.GetType().Name}");
                    }
                }

                _cached = new StatsConfig { Enabled = false };
                Debug.Print("[CIStatistics.Stats] No stats_config.json found; stats disabled.");
                return _cached;
            }
        }

        public static void Reload()
        {
            lock (Sync)
            {
                _cached = null;
            }

            Load();
        }

        private static string[] CandidatePaths()
        {
            string? moduleRoot = TryFindModuleRoot();
            string assemblyDir = Path.GetDirectoryName(typeof(StatsConfig).Assembly.Location) ?? ".";
            return new[]
            {
                moduleRoot == null ? string.Empty : Path.Combine(moduleRoot, "stats_config.json"),
                Path.Combine(assemblyDir, "stats_config.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "Modules", "CIStatistics", "stats_config.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "stats_config.json"),
            };
        }

        private static string? TryFindModuleRoot()
        {
            string? dir = Path.GetDirectoryName(typeof(StatsConfig).Assembly.Location);
            for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
            {
                string subModule = Path.Combine(dir, "SubModule.xml");
                if (File.Exists(subModule))
                {
                    return dir;
                }

                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }

        private static JsonSerializerOptions JsonOptions() => new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
    }
}
