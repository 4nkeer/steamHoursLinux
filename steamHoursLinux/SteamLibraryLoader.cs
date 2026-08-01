using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace steamHoursLinux
{
    public class SteamGameInfo
    {
        [JsonPropertyName("appid")]
        public uint AppId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("playtime_forever")]
        public int PlaytimeForever { get; set; }

        [JsonPropertyName("img_icon_url")]
        public string IconUrl { get; set; }
    }

    internal class SteamLibraryLoader
    {
        private static readonly HttpClient httpClient = new HttpClient();

        public event Action<string> OnLogMessage;

        /// <summary>
        /// Загрузка библиотеки игр пользователя через Steam Web API
        /// </summary>
        public async Task<List<SteamGameInfo>> GetOwnedGamesAsync(ulong steamId64, string accessToken)
        {
            if (steamId64 == 0 || string.IsNullOrEmpty(accessToken))
            {
                Log("⚠️ Для загрузки библиотеки требуется SteamID и AccessToken.");
                return new List<SteamGameInfo>();
            }

            try
            {
                Log("📚 Загрузка библиотеки игр...");

                // Формируем запрос к Web API Steam для получения списка игр
                string url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?access_token={accessToken}&steamid={steamId64}&include_appinfo=true&include_played_free_games=true";

                HttpResponseMessage response = await httpClient.GetAsync(url).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("response", out var responseElem) &&
                        responseElem.TryGetProperty("games", out var gamesElem))
                    {
                        var games = JsonSerializer.Deserialize<List<SteamGameInfo>>(gamesElem.GetRawText());
                        Log($"✅ Успешно загружено игр: {games?.Count ?? 0}");
                        return games ?? new List<SteamGameInfo>();
                    }
                }

                Log("⚠️ В ответе Steam API не найден список игр (возможно, профиль скрыт).");
            }
            catch (Exception ex)
            {
                Log($"❌ Ошибка при загрузке библиотеки игр: {ex.Message}");
            }

            return new List<SteamGameInfo>();
        }

        private void Log(string message) => OnLogMessage?.Invoke(message);
    }
}