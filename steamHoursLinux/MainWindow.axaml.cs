using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace steamHoursLinux
{
    // Класс для структуры сохранения настроек
    public class AppConfig
    {
        public string Login { get; set; } = "";
        public string Password { get; set; } = "";
        public bool RememberMe { get; set; } = false;
    }

    public partial class MainWindow : Window
    {
        string avatarHash = string.Empty;
        string avatarUrl = string.Empty;
        private SteamWorker? worker;
        private List<SteamGameInfo> loadedGames = new List<SteamGameInfo>();

        // Множества для отслеживания выбранных карточек и активного фарма
        private readonly HashSet<uint> selectedAppIds = new HashSet<uint>();
        private readonly HashSet<uint> activeFarmingAppIds = new HashSet<uint>();

        // Таймер отслеживания времени фарма
        private DispatcherTimer? farmTimer;
        private DateTime farmStartTime;

        // Палитра оформления
        private readonly IBrush bgCardDefault = SolidColorBrush.Parse("#2A313F");
        private readonly IBrush bgCardSelected = SolidColorBrush.Parse("#415069");
        private readonly IBrush accentBlue = SolidColorBrush.Parse("#00A2FF");
        private readonly IBrush accentGreen = SolidColorBrush.Parse("#5CAC22");
        private readonly IBrush accentRed = SolidColorBrush.Parse("#DC3545");
        private readonly IBrush textDim = SolidColorBrush.Parse("#8C96A5");
        private readonly IBrush borderDefault = SolidColorBrush.Parse("#00A2FF");

        private static readonly HttpClient httpClient = new HttpClient();
        private static readonly string configPath = "config.json";

        public MainWindow()
        {
            InitializeComponent();

            // Загружаем сохраненные данные при запуске
            LoadConfig();
            logBox.Text = "Событий нет. Жду твоих действий...\n";
            logBox.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            logBox.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            logBox.TextAlignment = Avalonia.Media.TextAlignment.Center;
            logBox.Foreground = SolidColorBrush.Parse("#8C96A5");
            logBox.FontWeight = FontWeight.Bold;
            logBox.FontSize = 16;
            userAvatarBgImg.Source = new Avalonia.Media.Imaging.Bitmap(
                Avalonia.Platform.AssetLoader.Open(new Uri("avares://steamHoursLinux/Assets/Icons/app_icon.ico"))
            );
            RenderGameCards(loadedGames);
            this.Closing += (s, e) =>
            {
                StopFarmTimer();
                worker?.Stop();
            };
        }

        
        // Загрузка настроек из файла
        private void LoadConfig()
        {
            try
            {
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null && config.RememberMe)
                    {
                        loginBox.Text = config.Login;
                        passwordBox.Text = config.Password;
                        rememberMeCheckBox.IsChecked = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Не удалось загрузить конфиг: {ex.Message}");
            }
        }

        // Сохранение настроек в файл
        private void SaveConfig()
        {
            try
            {
                var config = new AppConfig
                {
                    RememberMe = rememberMeCheckBox.IsChecked ?? false,
                    Login = (rememberMeCheckBox.IsChecked == true) ? (loginBox.Text?.Trim() ?? "") : "",
                    Password = (rememberMeCheckBox.IsChecked == true) ? (passwordBox.Text?.Trim() ?? "") : ""
                };

                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);
            }
            catch (Exception ex)
            {
                Log($"⚠️ Не удалось сохранить конфиг: {ex.Message}");
            }
        }

        private void LoginBtn_Click(object? sender, RoutedEventArgs e)
        {
            logBox.Text = string.Empty;
            if (string.IsNullOrEmpty(loginBox.Text) || string.IsNullOrEmpty(passwordBox.Text))
            {
                Log("❌ Введите логин и пароль!");
                return;
            }

            // Сохраняем или очищаем данные в зависимости от галочки
            SaveConfig();

            var initialAppIds = ParseAppIdsFromInput();
            uint firstAppId = initialAppIds.FirstOrDefault();

            guardPanel.IsVisible = false;
            loginBtn.IsEnabled = false;

            var login = loginBox.Text.Trim();
            var pass = passwordBox.Text.Trim();
            Log($"🔑 Вход в аккаунт: {login}...");

            worker = new SteamWorker(login, pass, firstAppId);
            worker.OnLogMessage += (msg) => Log(msg);

            worker.OnLoginSuccess += () => Dispatcher.UIThread.Post(() =>
            {
                loginBtn.IsEnabled = true;
                startBtn.IsEnabled = true;
                statusLabel.Text = "● Статус: В сети";
                statusLabel.Foreground = accentGreen;
                guardPanel.IsVisible = false;
            });

            // Обработка получения хэша аватара от воркера
            worker.OnAvatarHashReceived += (hash) => Dispatcher.UIThread.Post(() =>
            {
                System.Diagnostics.Debug.WriteLine($"🔥 Получен хэш аватара в MainWindow: '{hash}'"); // <--- Проверьте, появляется ли это в выводе

                avatarHash = hash;

                if (string.IsNullOrWhiteSpace(avatarHash))
                {
                    avatarHash = "fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb";
                }

                avatarUrl = $"https://avatars.steamstatic.com/{avatarHash}_full.jpg";
                System.Diagnostics.Debug.WriteLine($"🔗 Ссылка на аватар: {avatarUrl}"); // <--- Проверьте ссылку

                _ = LoadImageAsync(avatarUrl, userAvatarBgImg);
            });

            worker.OnLibraryLoaded += (games) => Dispatcher.UIThread.Post(() =>
            {
                loadedGames = games ?? new List<SteamGameInfo>();
                RenderGameCards(loadedGames);
            });

            worker.OnLoginFailed += (error) => Dispatcher.UIThread.Post(() =>
            {
                loginBtn.IsEnabled = true;
                Log($"❌ Ошибка: {error}");
                guardPanel.IsVisible = false;
            });

            worker.OnGuardRequired += () => Dispatcher.UIThread.Post(() =>
            {
                guardPanel.IsVisible = true;
                guardBox.Text = "";
                guardBox.Focus();
                loginBtn.IsEnabled = true;
                Log("🔐 Требуется код Steam Guard!");
                statusLabel.Text = "● Статус: Ждет Guard";
                statusLabel.Foreground = Brushes.Orange;
            });

            worker.OnAutoFarmingResumed += () => Dispatcher.UIThread.Post(() =>
            {
                activeFarmingAppIds.Clear();

                // Берем список игр напрямую из SteamWorker
                if (worker.CurrentFarmingAppIds != null)
                {
                    foreach (var id in worker.CurrentFarmingAppIds)
                    {
                        activeFarmingAppIds.Add(id);
                    }
                }

                // Визуальное оформление (как при ручном запуске)
                startBtn.IsEnabled = false;
                loginBtn.IsEnabled = false;
                stopBtn.IsEnabled = true;

                statusLabel.Text = "● ФАРМИНГ АКТИВЕН";
                statusLabel.Foreground = accentGreen;
                timerText.Foreground = accentGreen;
                timerCardPanel.BorderBrush = accentGreen;

                farmingInfoLabel.Text = $"Запущенные игры ({activeFarmingAppIds.Count}): {string.Join(", ", activeFarmingAppIds)}";

                StartFarmTimer();
                RenderGameCards(loadedGames);
            });

            worker.Start();
        }

        private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (loadedGames == null || loadedGames.Count == 0) return;

            string query = searchBox.Text?.Trim().ToLower() ?? "";

            if (string.IsNullOrEmpty(query))
            {
                RenderGameCards(loadedGames);
                return;
            }

            var filteredGames = loadedGames.Where(g =>
                (g.Name != null && g.Name.ToLower().Contains(query)) ||
                g.AppId.ToString().Contains(query)
            ).ToList();

            RenderGameCards(filteredGames);
        }

        private void RenderGameCards(List<SteamGameInfo> games)
        {
            gamesFlowPanel.Children.Clear();

            if (gamesScrollViewer.Content != gamesFlowPanel)
            {
                gamesScrollViewer.Content = gamesFlowPanel;
            }
            if (games == null || games.Count == 0)
            {
                gamesScrollViewer.Content = new TextBlock
                {
                    Text = "Игры не найдены.",
                    Margin = new Avalonia.Thickness(15),
                    TextAlignment = Avalonia.Media.TextAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    FontWeight = FontWeight.Bold,
                    Foreground = SolidColorBrush.Parse("#8C96A5"),
                    FontFamily = new Avalonia.Media.FontFamily("avares://steamHoursLinux/Assets/Fonts/JetBrains_Mono#JetBrains Mono"),
                    FontSize = 16
                };
                return;
            }

            foreach (var game in games)
            {
                double hours = Math.Round(game.PlaytimeForever / 60.0, 1);
                bool isFarming = activeFarmingAppIds.Contains(game.AppId);
                bool isSelected = selectedAppIds.Contains(game.AppId);

                IBrush cardBg = isFarming ? SolidColorBrush.Parse("#1E382B") : (isSelected ? bgCardSelected : bgCardDefault);
                IBrush borderBrush = isFarming ? accentGreen : (isSelected ? Brushes.White : Brushes.Transparent);

                var card = new Border
                {
                    Width = 165,
                    Height = 75,
                    Margin = new Avalonia.Thickness(5),
                    Background = cardBg,
                    BorderBrush = borderBrush,
                    BorderThickness = new Avalonia.Thickness(isFarming || isSelected ? 2 : 0),
                    CornerRadius = new Avalonia.CornerRadius(4),
                    Tag = game.AppId,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                };

                var grid = new Grid
                {
                    RowDefinitions = RowDefinitions.Parse("*,Auto"),
                    ColumnDefinitions = ColumnDefinitions.Parse("32,*"),
                    Margin = new Avalonia.Thickness(8)
                };

                var img = new Image
                {
                    Width = 28,
                    Height = 28,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
                };

                if (!string.IsNullOrEmpty(game.IconUrl))
                {
                    string iconUrl = $"http://media.steampowered.com/steamcommunity/public/images/apps/{game.AppId}/{game.IconUrl}.jpg";
                    _ = LoadImageAsync(iconUrl, img);
                }

                Grid.SetRow(img, 0);
                Grid.SetColumn(img, 0);

                var titlePanel = new StackPanel();

                if (isFarming)
                {
                    var statusBadge = new TextBlock
                    {
                        Text = "▶ ФАРМИТСЯ",
                        FontSize = 9,
                        FontWeight = FontWeight.Bold,
                        Foreground = accentGreen,
                        Margin = new Avalonia.Thickness(5, 0, 0, 2)
                    };
                    titlePanel.Children.Add(statusBadge);
                }

                var nameText = new TextBlock
                {
                    Text = game.Name,
                    FontWeight = FontWeight.Bold,
                    FontSize = 11,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(5, 0, 0, 0)
                };
                titlePanel.Children.Add(nameText);

                Grid.SetRow(titlePanel, 0);
                Grid.SetColumn(titlePanel, 1);

                var infoText = new TextBlock
                {
                    Text = $"⏱ {hours} ч. | ID: {game.AppId}",
                    FontWeight = FontWeight.Bold,
                    FontSize = 10,
                    Foreground = isFarming ? accentGreen : (isSelected ? Brushes.White : accentBlue),
                    Margin = new Avalonia.Thickness(0, 5, 0, 0)
                };
                Grid.SetRow(infoText, 1);
                Grid.SetColumn(infoText, 0);
                Grid.SetColumnSpan(infoText, 2);

                grid.Children.Add(img);
                grid.Children.Add(titlePanel);
                grid.Children.Add(infoText);
                card.Child = grid;

                card.PointerPressed += (s, e) =>
                {
                    if (selectedAppIds.Contains(game.AppId))
                    {
                        selectedAppIds.Remove(game.AppId);
                    }
                    else
                    {
                        selectedAppIds.Add(game.AppId);
                    }

                    appIdBox.Text = string.Join(", ", selectedAppIds);
                    RenderGameCards(loadedGames);
                };

                gamesFlowPanel.Children.Add(card);
            }
        }

        private async Task LoadImageAsync(string url, Image targetImage)
        {
            try
            {
                byte[] bytes = await httpClient.GetByteArrayAsync(url);
                using var stream = new MemoryStream(bytes);
                var bitmap = new Bitmap(stream);
                Dispatcher.UIThread.Post(() => targetImage.Source = bitmap);
            }
            catch { }
        }

        private void GuardBtn_Click(object? sender, RoutedEventArgs e)
        {
            string code = guardBox.Text?.Trim().ToUpperInvariant() ?? "";
            if (string.IsNullOrEmpty(code))
            {
                Log("❌ Введите код!");
                return;
            }

            guardPanel.IsVisible = false;
            loginBtn.IsEnabled = false;
            Log($"📨 Отправка кода Guard: {code}");
            statusLabel.Text = "● Проверка кода...";
            statusLabel.Foreground = Brushes.Yellow;

            worker?.SubmitGuardCode(code);
        }

        private void GuardApproveBtn_Click(object? sender, RoutedEventArgs e)
        {
            guardPanel.IsVisible = false;
            loginBtn.IsEnabled = false;
            Log("📨 Ожидание подтверждения из мобильного приложения...");
        }

        private void StartBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (worker == null)
            {
                Log("❌ Сначала авторизуйтесь!");
                return;
            }

            List<uint> appIdsToStart = ParseAppIdsFromInput();

            if (appIdsToStart.Count == 0)
            {
                Log("❌ Выберите хотя бы одну игру из списка или введите AppID!");
                return;
            }

            if (appIdsToStart.Count > 32)
            {
                Log("⚠️ Лимит Steam: не более 32 игр одновременно. Запускаются первые 32.");
                appIdsToStart = appIdsToStart.Take(32).ToList();
            }

            Log($"▶️ Запуск фарминга для {appIdsToStart.Count} игр: {string.Join(", ", appIdsToStart)}...");

            worker.StartIdling(appIdsToStart);

            activeFarmingAppIds.Clear();
            foreach (var id in appIdsToStart)
            {
                activeFarmingAppIds.Add(id);
            }

            startBtn.IsEnabled = false;
            loginBtn.IsEnabled = false;
            stopBtn.IsEnabled = true;

            statusLabel.Text = "● ФАРМИНГ АКТИВЕН";
            statusLabel.Foreground = accentGreen;
            timerText.Foreground = accentGreen;
            timerCardPanel.BorderBrush = accentGreen;

            farmingInfoLabel.Text = $"Запущенные игры ({activeFarmingAppIds.Count}): {string.Join(", ", activeFarmingAppIds)}";

            StartFarmTimer();
            RenderGameCards(loadedGames);
        }

        private void StopBtn_Click(object? sender, RoutedEventArgs e)
        {
            Log("⏹ Остановка фарминга...");
            worker?.StopIdling();

            activeFarmingAppIds.Clear();

            StopFarmTimer();

            startBtn.IsEnabled = true;
            stopBtn.IsEnabled = false;

            statusLabel.Text = "● Статус: В сети (Фарм остановлен)";
            statusLabel.Foreground = accentBlue;
            timerText.Foreground = accentBlue;
            timerText.Text = "00:00:00";
            timerCardPanel.BorderBrush = borderDefault;
            farmingInfoLabel.Text = "Фарминг не запущен";

            RenderGameCards(loadedGames);
        }

        private void StartFarmTimer()
        {
            farmStartTime = DateTime.Now;

            farmTimer?.Stop();

            farmTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };

            farmTimer.Tick += (s, e) =>
            {
                DateTime now = DateTime.Now;

                int months = 0;
                DateTime tempDate = farmStartTime;
                while (tempDate.AddMonths(1) <= now)
                {
                    months++;
                    tempDate = tempDate.AddMonths(1);
                }

                TimeSpan remainingSpan = now - tempDate;
                int totalDays = remainingSpan.Days;
                int weeks = totalDays / 7;
                int days = totalDays % 7;

                List<string> parts = new List<string>();

                if (months > 0) parts.Add($"{months} мес.");
                if (weeks > 0) parts.Add($"{weeks} нед.");
                if (days > 0) parts.Add($"{days} д.");

                string timeFormatted = $"{remainingSpan.Hours:D2}:{remainingSpan.Minutes:D2}:{remainingSpan.Seconds:D2}";
                parts.Add(timeFormatted);

                timerText.Text = string.Join(" ", parts);
            };

            farmTimer.Start();
        }

        private void StopFarmTimer()
        {
            farmTimer?.Stop();
            farmTimer = null;
        }

        private List<uint> ParseAppIdsFromInput()
        {
            var result = new List<uint>();
            string inputText = appIdBox.Text?.Trim() ?? "";

            if (!string.IsNullOrEmpty(inputText))
            {
                var parts = inputText.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (uint.TryParse(part, out uint parsedId) && parsedId > 0)
                    {
                        if (!result.Contains(parsedId))
                            result.Add(parsedId);
                    }
                }
            }
            return result;
        }

        private void Log(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                logBox.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
                logBox.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
                logBox.TextAlignment = Avalonia.Media.TextAlignment.Left;
                logBox.Foreground = SolidColorBrush.Parse("#B4C8DC");
                logBox.FontWeight = FontWeight.Normal;
                logBox.FontSize = 12;
                logBox.Text += $"[{timestamp}] {message}\n";
                logScrollViewer.ScrollToEnd();
            });
        }
    }
}