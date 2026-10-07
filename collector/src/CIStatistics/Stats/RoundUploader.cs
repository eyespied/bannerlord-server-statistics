using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TaleWorlds.Library;

namespace CIStatistics.Stats
{
    internal sealed class RoundReport
    {
        public int schema_version { get; set; } = 1;
        public Guid match_id { get; set; }
        public int round_number { get; set; }
        public DateTimeOffset started_at { get; set; }
        public DateTimeOffset ended_at { get; set; }
        public string map_id { get; set; } = "";
        public string game_type { get; set; } = "";
        public List<RoundPlayerRpcPayload> players { get; set; } = new();
    }

    // One worker per process, independent of mission lifetime. Never log tokens or bodies.
    internal sealed class RoundUploader
    {
        private static RoundUploader? _instance;
        private static readonly object InstanceLock = new();
        private readonly string _directory;
        private readonly StatsConfig _config;
        private readonly HttpClient _http;
        private string _lastUpload = "No completed round uploaded yet.";
        internal static string StatusText
        {
            get
            {
                var instance = _instance;
                if (instance == null) return "Uploader has not started.";
                try
                {
                    int pending = Directory.GetFiles(instance._directory, "*.json").Length;
                    int rejected = Directory.GetFiles(instance._directory, "*.rejected").Length;
                    return $"Uploads: {pending} pending, {rejected} rejected. {instance._lastUpload}";
                }
                catch { return "Upload queue unreadable; check server logs."; }
            }
        }
        public static RoundUploader Get(StatsConfig config)
        {
            lock (InstanceLock) return _instance ??= new RoundUploader(config);
        }
        internal RoundUploader(StatsConfig config, HttpMessageHandler? handler = null, bool startWorker = true)
        {
            _config = config;
            _directory = Path.GetFullPath(config.QueueDirectory);
            Directory.CreateDirectory(_directory);
            _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.IngestToken);
            if (startWorker) _ = Task.Run(Worker);
        }
        public void EnqueueRound(RoundReport report)
        {
            try
            {
                string name = $"{report.match_id:N}-{report.round_number}.json";
                string path = Path.Combine(_directory, name);
                if (File.Exists(path)) return;
                string temp = path + ".tmp";
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report));
                using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }
                File.Move(temp, path);
            }
            catch (Exception ex) { Debug.Print($"[CIStatistics] Cannot persist round: {ex.GetType().Name}. Check queue disk/permissions."); }
        }
        private async Task Worker()
        {
            while (true)
            {
                await DrainOnce();
                await Task.Delay(TimeSpan.FromSeconds(15));
            }
        }
        internal async Task DrainOnce()
        {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(_directory, "*.json").OrderBy(File.GetLastWriteTimeUtc))
                    {
                        using var content = new StringContent(await File.ReadAllTextAsync(file), Encoding.UTF8, "application/json");
                        using var response = await _http.PostAsync(_config.IngestUrl, content);
                        int status = (int)response.StatusCode;
                        if (response.IsSuccessStatusCode)
                        {
                            string body = await response.Content.ReadAsStringAsync();
                            string? stateName = null;
                            try
                            {
                                using var result = JsonDocument.Parse(body);
                                if (result.RootElement.TryGetProperty("status", out var state))
                                    stateName = state.GetString();
                            }
                            catch { }
                            if (stateName != "accepted" && stateName != "duplicate" && stateName != "ignored")
                            {
                                _lastUpload = "Backend returned an unexpected response; report retained.";
                                Debug.Print("[CIStatistics] Unexpected upload acknowledgment; queue retained.");
                                break;
                            }
                            string outcome = stateName == "ignored" ? "ignored (no scheduled event on the round's date)" : "saved";
                            _lastUpload = $"Last report {outcome} at {DateTimeOffset.UtcNow:HH:mm:ss} UTC.";
                            File.Delete(file);
                        }
                        else if (status == 400 || status == 409 || status == 413 || status == 422)
                        {
                            // Invalid/conflicting reports are retained for inspection, never silently discarded.
                            File.Move(file, file + ".rejected", true);
                            _lastUpload = $"Last report rejected (HTTP {status}).";
                            Debug.Print($"[CIStatistics] Report rejected (HTTP {status}); retained in queue as .rejected.");
                        }
                        else
                        {
                            _lastUpload = $"Last upload failed (HTTP {status}); retrying.";
                            Debug.Print($"[CIStatistics] Upload unavailable (HTTP {status}); queue retained. Check credentials/backend.");
                            break;
                        }
                    }
                }
                catch (Exception ex) { _lastUpload = "Last upload failed; retrying."; Debug.Print($"[CIStatistics] Upload deferred: {ex.GetType().Name}; queue retained."); }
        }
    }
}
