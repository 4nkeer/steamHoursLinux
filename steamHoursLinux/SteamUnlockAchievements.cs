using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

namespace steamHoursLinux
{
    internal class SteamUnlockAchievements
    {
        public int UnlockedAchievements { get; set; }
        public int TotalAchievements { get; set; }
        public List<SteamAchievementInfo> Achievements { get; set; } = new List<SteamAchievementInfo>();

        private static readonly Dictionary<uint, SteamUnlockAchievements> _gameAchievementsCache = new Dictionary<uint, SteamUnlockAchievements>();
        private static readonly HttpClient _httpClient = new HttpClient();

        private static string GetApiKey()
        {
            try
            {
                string path = Path.Join("config", "webapi.txt");
                if (File.Exists(path))
                {
                    string key = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrEmpty(key))
                    {
                        return key;
                    }
                }
                Console.WriteLine("[API Warning] Файл config/webapi.txt не найден или пуст!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API Error] Не удалось прочитать ключ из файла: {ex.Message}");
            }
            return string.Empty;
        }

        public static SteamUnlockAchievements GetStatsForGame(uint appId)
        {
            if (_gameAchievementsCache.TryGetValue(appId, out var stats))
            {
                return stats;
            }
            return new SteamUnlockAchievements();
        }

        public static void SetGameAchievements(uint appId, int unlocked, int total, List<SteamAchievementInfo> achievements)
        {
            if (_gameAchievementsCache.TryGetValue(appId, out var existingStats))
            {
                existingStats.TotalAchievements = total;
                existingStats.UnlockedAchievements = unlocked;
                existingStats.Achievements = achievements;
            }
            else
            {
                _gameAchievementsCache[appId] = new SteamUnlockAchievements
                {
                    TotalAchievements = total,
                    UnlockedAchievements = unlocked,
                    Achievements = achievements
                };
            }
        }

        public static async Task LoadAchievementsAsync(ulong steamId, uint appId)
        {
            try
            {
                string apiKey = GetApiKey();
                if (string.IsNullOrEmpty(apiKey))
                {
                    SetGameAchievements(appId, 0, 0, new List<SteamAchievementInfo>());
                    return;
                }

                int total = 0;
                var achievementsMap = new Dictionary<string, (string Name, bool IsUnlocked, string IconUrl, string IconGrayUrl)>();

                // 1. Сначала получаем схему игры (общее количество и названия всех достижений)
                string schemaUrl = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v0002/?key={apiKey}&appid={appId}&format=json";
                var schemaResponse = await _httpClient.GetAsync(schemaUrl);

                if (schemaResponse.IsSuccessStatusCode)
                {
                    string schemaJson = await schemaResponse.Content.ReadAsStringAsync();
                    using var schemaDoc = JsonDocument.Parse(schemaJson);
                    var schemaRoot = schemaDoc.RootElement;

                    if (schemaRoot.TryGetProperty("game", out var gameProp) &&
                        gameProp.TryGetProperty("availableGameStats", out var statsProp) &&
                        statsProp.TryGetProperty("achievements", out var achievementsSchemaArray))
                    {
                        foreach (var ach in achievementsSchemaArray.EnumerateArray())
                        {
                            string apiName = ach.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                            string displayName = ach.TryGetProperty("displayName", out var dispProp) ? dispProp.GetString() ?? apiName : apiName;

                            // Считываем ссылки на иконки из ответа Steam
                            string icon = ach.TryGetProperty("icon", out var iconProp) ? iconProp.GetString() ?? "" : "";
                            string iconGray = ach.TryGetProperty("icongray", out var grayProp) ? grayProp.GetString() ?? "" : "";

                            if (!string.IsNullOrEmpty(apiName))
                            {
                                achievementsMap[apiName] = (displayName, false, icon, iconGray);
                                total++;
                            }
                        }
                    }
                }

                // 2. Затем получаем прогресс конкретного пользователя
                string userStatsUrl = $"https://api.steampowered.com/ISteamUserStats/GetUserStatsForGame/v0002/?key={apiKey}&steamid={steamId}&appid={appId}";
                var userResponse = await _httpClient.GetAsync(userStatsUrl);

                int unlocked = 0;
                if (userResponse.IsSuccessStatusCode)
                {
                    string userJson = await userResponse.Content.ReadAsStringAsync();
                    using var userDoc = JsonDocument.Parse(userJson);
                    var userRoot = userDoc.RootElement;

                    if (userRoot.TryGetProperty("playerstats", out var playerStats) &&
                        playerStats.TryGetProperty("achievements", out var achievementsArray))
                    {
                        foreach (var ach in achievementsArray.EnumerateArray())
                        {
                            string apiName = ach.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                            int achieved = ach.TryGetProperty("achieved", out var achievedProp) ? achievedProp.GetInt32() : 0;

                            if (achieved == 1)
                            {
                                if (achievementsMap.TryGetValue(apiName, out var val))
                                {
                                    achievementsMap[apiName] = (val.Name, true, val.IconUrl, val.IconGrayUrl);
                                }
                                unlocked++;
                            }
                        }
                    }
                }

                // Если схема по какой-то причине не вернулась, берем fallback из userstats
                if (total == 0 && achievementsMap.Count == 0)
                {
                    SetGameAchievements(appId, 0, 0, new List<SteamAchievementInfo>());
                    return;
                }

                var achievementsList = new List<SteamAchievementInfo>();
                foreach (var kvp in achievementsMap)
                {
                    // Выбираем цветную иконку, если достижение получено, или серую, если заблокировано
                    string selectedIcon = kvp.Value.IsUnlocked ? kvp.Value.IconUrl : kvp.Value.IconGrayUrl;

                    achievementsList.Add(new SteamAchievementInfo
                    {
                        Id = kvp.Key,
                        Name = kvp.Value.Name,
                        IsUnlocked = kvp.Value.IsUnlocked,
                        IconUrl = selectedIcon
                    });
                }

                SetGameAchievements(appId, unlocked, total, achievementsList);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API Exception] AppID {appId}: {ex.Message}");
                SetGameAchievements(appId, 0, 0, new List<SteamAchievementInfo>());
            }
        }

        public static async Task RenderAchievementsList(WrapPanel achievementsFlowPanel, ulong steamId, uint appId)
        {
            achievementsFlowPanel.Children.Clear();

            var stats = GetStatsForGame(appId);
            if (stats.TotalAchievements == 0)
            {
                await LoadAchievementsAsync(steamId, appId);
                stats = GetStatsForGame(appId);
            }

            if (stats.TotalAchievements == 0)
            {
                achievementsFlowPanel.Children.Add(new TextBlock
                {
                    Text = "У этой игры нет достижений или профиль закрыт.",
                    Foreground = Brushes.White,
                    Margin = new Avalonia.Thickness(10)
                });
                return;
            }

            foreach (var ach in stats.Achievements)
            {
                var card = new Border
                {
                    Width = 175,
                    Height = 85,
                    Margin = new Avalonia.Thickness(5),
                    Background = SolidColorBrush.Parse(ach.IsUnlocked ? "#1E382B" : "#2A313D"),
                    BorderBrush = ach.IsUnlocked ? SolidColorBrush.Parse("#4CAF50") : Brushes.Transparent,
                    BorderThickness = new Avalonia.Thickness(ach.IsUnlocked ? 2 : 0),
                    CornerRadius = new Avalonia.CornerRadius(4)
                };

                var textBlock = new TextBlock
                {
                    Text = $"{ach.Name}\n{(ach.IsUnlocked ? "✅ Получено" : "🔒 Заблокировано")}",
                    Foreground = Brushes.White,
                    FontSize = 11,
                    Margin = new Avalonia.Thickness(8),
                    TextWrapping = TextWrapping.Wrap
                };

                card.Child = textBlock;
                achievementsFlowPanel.Children.Add(card);
            }
        }

        public static int GetUnlockedCount(uint appId)
        {
            if (_gameAchievementsCache.TryGetValue(appId, out var stats))
            {
                return stats.UnlockedAchievements;
            }
            return 0;
        }

        public static int GetTotalCount(uint appId)
        {
            if (_gameAchievementsCache.TryGetValue(appId, out var stats))
            {
                return stats.TotalAchievements;
            }
            return 0;
        }

        public class SteamAchievementInfo
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public bool IsUnlocked { get; set; }
            public string IconUrl { get; set; } = string.Empty;
        }
    }
}