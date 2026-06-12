using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArmaLauncherClient.Services;

namespace ArmaLauncherClient.Services;

public class GameServerInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ScenarioName { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public int PlayerCount { get; set; }
    public int PlayerCountLimit { get; set; }
    public List<ServerMod> Mods { get; set; } = [];
    public List<ServerPlayer> Players { get; set; } = [];
    public string Ip { get; set; } = "";
    public string Port { get; set; } = "";
    public bool IsJoinable { get; set; }
    public DateTime LastUpdate { get; set; }
    
    // Computed properties
    public string OnlineStatus => $"{PlayerCount}/{PlayerCountLimit}";
    public string ModsCount => Mods.Count > 0 ? LocalizationManager.F("server_mods_count", Mods.Count) : LocalizationManager.S("server_no_mods");
    public bool HasPlayers => PlayerCount > 0;
    public string ModsList => Mods.Count > 0 ? string.Join(", ", Mods.ConvertAll(m => m.Name)) : "—";
}

public class ServerMod
{
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

public class ServerPlayer
{
    public string Username { get; set; } = "";
}

// JSON models for deserialization
internal class ServerApiResponse
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = "";
    
    [JsonPropertyName("serverID")]
    public string ServerId { get; set; } = "";
    
    [JsonPropertyName("data")]
    public ServerData? Data { get; set; }
    
    [JsonPropertyName("lastUpdate")]
    public string? LastUpdate { get; set; }
    
    [JsonPropertyName("ip")]
    public string? Ip { get; set; }
    
    [JsonPropertyName("port")]
    public string? Port { get; set; }
    
    [JsonPropertyName("players")]
    public List<PlayerData>? Players { get; set; }
}

internal class ServerData
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("scenarioName")]
    public string? ScenarioName { get; set; }
    
    [JsonPropertyName("gameVersion")]
    public string? GameVersion { get; set; }
    
    [JsonPropertyName("playerCount")]
    public int PlayerCount { get; set; }
    
    [JsonPropertyName("playerCountLimit")]
    public int PlayerCountLimit { get; set; }
    
    [JsonPropertyName("joinable")]
    public bool Joinable { get; set; }
    
    [JsonPropertyName("mods")]
    public List<ModData>? Mods { get; set; }
}

internal class ModData
{
    [JsonPropertyName("modId")]
    public string? ModId { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

internal class PlayerData
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

public class ServerMonitorService
{
    private readonly HttpClient _httpClient;
    private const string ApiUrl = "https://zos.strikearena.ru/arma/getServers.php";
    
    public ServerMonitorService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<GameServerInfo>> GetServersAsync(CancellationToken ct = default)
    {
        var servers = new List<GameServerInfo>();
        
        try
        {
            FileLogger.Log($"[MONITOR] Fetching servers from {ApiUrl}");
            
            var response = await _httpClient.GetStringAsync(ApiUrl, ct);
            var apiServers = JsonSerializer.Deserialize<List<ServerApiResponse>>(response);
            
            if (apiServers == null)
            {
                FileLogger.Log("[MONITOR] No servers found or invalid response");
                return servers;
            }

            foreach (var apiServer in apiServers)
            {
                if (apiServer.Data == null) continue;
                
                var server = new GameServerInfo
                {
                    Id = apiServer.ServerId,
                    Name = apiServer.Data.Name ?? "Unknown",
                    ScenarioName = CleanScenarioName(apiServer.Data.ScenarioName ?? ""),
                    GameVersion = apiServer.Data.GameVersion ?? "",
                    PlayerCount = apiServer.Data.PlayerCount,
                    PlayerCountLimit = apiServer.Data.PlayerCountLimit,
                    IsJoinable = apiServer.Data.Joinable,
                    Ip = apiServer.Ip ?? "",
                    Port = apiServer.Port ?? ""
                };
                
                // Parse last update
                if (DateTime.TryParse(apiServer.LastUpdate, out var lastUpdate))
                {
                    server.LastUpdate = lastUpdate;
                }
                
                // Parse mods
                if (apiServer.Data.Mods != null)
                {
                    foreach (var mod in apiServer.Data.Mods)
                    {
                        server.Mods.Add(new ServerMod
                        {
                            ModId = mod.ModId ?? "",
                            Name = mod.Name ?? "",
                            Version = mod.Version ?? ""
                        });
                    }
                }
                
                // Parse players
                if (apiServer.Players != null)
                {
                    foreach (var player in apiServer.Players)
                    {
                        server.Players.Add(new ServerPlayer
                        {
                            Username = player.Username ?? "Unknown"
                        });
                    }
                }
                
                servers.Add(server);
            }
            
            // Sort by player count descending
            servers.Sort((a, b) => b.PlayerCount.CompareTo(a.PlayerCount));
            
            FileLogger.Log($"[MONITOR] Found {servers.Count} servers, total players: {servers.Sum(s => s.PlayerCount)}");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[MONITOR] Error fetching servers: {ex.Message}");
        }
        
        return servers;
    }
    
    public async Task<ServerStatsData?> GetServerStatsAsync(string ip, string port, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://zos.strikearena.ru/arma/stats/graph.php?ip={ip}:{port}";
            FileLogger.Log($"[MONITOR] Fetching stats from {url}");
            
            var html = await _httpClient.GetStringAsync(url, ct);
            return ParseStatsHtml(html, ip, port);
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[MONITOR] Error fetching stats: {ex.Message}");
            return null;
        }
    }
    
    private static ServerStatsData? ParseStatsHtml(string html, string ip, string port)
    {
        try
        {
            // Extract labels array: labels: ["2026-01-12 09:14:24", ...]
            var labelsMatch = Regex.Match(html, @"labels:\s*\[(.*?)\]", RegexOptions.Singleline);
            // Extract data array: "data": [0, 0, 0, 1, 2, ...]
            var dataMatch = Regex.Match(html, @"""data"":\s*\[([\d,\s]+)\]", RegexOptions.Singleline);
            
            if (!labelsMatch.Success || !dataMatch.Success)
            {
                FileLogger.Log("[MONITOR] Could not parse stats HTML - regex failed");
                return null;
            }
            
            // Parse labels
            var labelsRaw = labelsMatch.Groups[1].Value;
            var labels = Regex.Matches(labelsRaw, @"""([^""]+)""")
                .Select(m => m.Groups[1].Value)
                .ToList();
            
            // Parse data
            var dataRaw = dataMatch.Groups[1].Value;
            var data = dataRaw.Split(',')
                .Select(s => int.TryParse(s.Trim(), out var v) ? v : 0)
                .ToList();
            
            if (labels.Count == 0 || data.Count == 0)
            {
                FileLogger.Log("[MONITOR] Parsed empty labels or data");
                return null;
            }
            
            FileLogger.Log($"[MONITOR] Parsed {labels.Count} labels and {data.Count} data points");
            
            return new ServerStatsData
            {
                Ip = ip,
                Port = port,
                Labels = labels,
                PlayerCounts = data,
                MaxPlayers = data.Count > 0 ? data.Max() : 0,
                AvgPlayers = data.Count > 0 ? data.Average() : 0
            };
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[MONITOR] Error parsing stats HTML: {ex.Message}");
            return null;
        }
    }
    
    private static string CleanScenarioName(string name)
    {
        // Remove AR- prefix and localization markers
        if (name.StartsWith("#AR-"))
        {
            name = name.Substring(4);
        }
        return name.Replace("_", " ");
    }
}

public class ServerStatsData
{
    public string Ip { get; set; } = "";
    public string Port { get; set; } = "";
    public List<string> Labels { get; set; } = [];
    public List<int> PlayerCounts { get; set; } = [];
    public int MaxPlayers { get; set; }
    public double AvgPlayers { get; set; }
    
    public string Address => $"{Ip}:{Port}";
}
