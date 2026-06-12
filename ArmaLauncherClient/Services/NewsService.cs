using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ArmaLauncherClient.Services;

public class NewsItem
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string ImageUrl { get; init; } = "";
    public string Url { get; init; } = "";
    public string Label { get; init; } = "";
}

public class NewsService
{
    private readonly HttpClient _httpClient;
    private const string NewsUrl = "https://zos.strikearena.ru/";
    
    public NewsService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<NewsItem>> GetNewsAsync(CancellationToken ct = default)
    {
        var news = new List<NewsItem>();
        
        try
        {
            FileLogger.Log($"[NEWS] Fetching news from {NewsUrl}");
            
            var html = await _httpClient.GetStringAsync(NewsUrl, ct);
            
            // Ищем секцию с новостями
            var sectionMatch = Regex.Match(html, 
                @"<section[^>]*class=""[^""]*homepage-last-news[^""]*""[^>]*>(.*?)</section>", 
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            
            if (!sectionMatch.Success)
            {
                FileLogger.Log("[NEWS] News section not found");
                return news;
            }
            
            var sectionHtml = sectionMatch.Groups[1].Value;
            
            // Ищем все post-block
            var postBlockPattern = @"<div[^>]*class=""post-block""[^>]*>(.*?)</div>\s*</div>";
            var postMatches = Regex.Matches(sectionHtml, postBlockPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            
            foreach (Match postMatch in postMatches)
            {
                var blockHtml = postMatch.Groups[1].Value;
                
                // URL новости
                var urlMatch = Regex.Match(blockHtml, @"<a\s+href=['""]([^'""]+)['""]", RegexOptions.IgnoreCase);
                var url = urlMatch.Success ? urlMatch.Groups[1].Value : "";
                
                // Картинка
                var imgMatch = Regex.Match(blockHtml, @"<img\s+src=['""]([^'""]+)['""]", RegexOptions.IgnoreCase);
                var imageUrl = imgMatch.Success ? imgMatch.Groups[1].Value : "";
                
                // Лейбл (тип новости)
                var labelMatch = Regex.Match(blockHtml, @"<span[^>]*class=""label[^""]*""[^>]*>([^<]+)</span>", RegexOptions.IgnoreCase);
                var label = labelMatch.Success ? DecodeHtml(labelMatch.Groups[1].Value.Trim()) : "";
                
                // Заголовок
                var titleMatch = Regex.Match(blockHtml, @"<h4>([^<]+)</h4>", RegexOptions.IgnoreCase);
                var title = titleMatch.Success ? DecodeHtml(titleMatch.Groups[1].Value.Trim()) : "";
                
                // Описание
                var descMatch = Regex.Match(blockHtml, @"<p>([^<]+)</p>", RegexOptions.IgnoreCase);
                var description = descMatch.Success ? DecodeHtml(descMatch.Groups[1].Value.Trim()) : "";
                
                if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(url))
                {
                    news.Add(new NewsItem
                    {
                        Title = title,
                        Description = description,
                        ImageUrl = imageUrl,
                        Url = url,
                        Label = label
                    });
                    
                    FileLogger.Log($"[NEWS] Found: {title}");
                }
            }
            
            FileLogger.Log($"[NEWS] Total news found: {news.Count}");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[NEWS] Error fetching news: {ex.Message}");
        }
        
        return news;
    }
    
    private static string DecodeHtml(string html)
    {
        return html
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Trim();
    }
}
