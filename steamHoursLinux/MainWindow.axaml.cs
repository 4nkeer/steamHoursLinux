using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SteamKit2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace steamHoursLinux
{
    public class AppConfig
    {
        public string Login { get; set; } = "";
        public string Password { get; set; } = "";
        public bool RememberMe { get; set; } = false;
    }

    public partial class MainWindow : Window
    {
        bool isDelWebApiKey = false;
        string avatarHash = string.Empty;
        string avatarUrl = string.Empty;
        string currentLang = "ru";
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

        // Общая папка config и путь к config.json в ней
        private static readonly string configDirectory = Path.Join(AppDomain.CurrentDomain.BaseDirectory, "config");
        private static readonly string configPath = Path.Join(configDirectory, "config.json");
        private readonly string langFilePath = Path.Join(AppDomain.CurrentDomain.BaseDirectory, "language.txt");
        private readonly string webApiKeyPath = Path.Join(configDirectory, "webapi.txt");
        private readonly Dictionary<string, Dictionary<string, string>> localizedText = new()
        {
            ["ru"] = new()
            {
                ["Login"] = "Логин",
                ["Password"] = "Пароль",
                ["AppId"] = "AppID (необязательно)",
                ["Remember"] = "Запомнить меня",
                ["SignIn"] = "✅ Войти",
                ["Start"] = "⏩ Запустить фарм",
                ["Stop"] = "⏹ Остановить фарм",
                ["Auth"] = "АВТОРИЗАЦИЯ",
                ["Manage"] = "УПРАВЛЕНИЕ ФАРМОМ",
                ["Timer"] = "ТАЙМЕР ФАРМА",
                ["FarmNotStarted"] = "Фарминг не запущен",
                ["TabLibrary"] = "🎮 Библиотека игр",
                ["TabAch"] = "🏆 Достижения",
                ["Search"] = "🔍 Поиск по названию или AppID...",
                ["LogWait"] = "Событий нет. Жду твоих действий...\n",
                ["LogHeader"] = "Лог событий",
                ["StatusWait"] = "● Статус: Ожидание входа",
                ["SettingsHeader"] = "Настройки приложения",
                ["TextLanguageInterface"] = "ЯЗЫК ИНТЕРФЕЙСА",
                ["TextWebApiKey"] = "WEB API КЛЮЧ",
                ["CloseAndSaveButton"] = "Сохранить и закрыть",
                ["WarningTitle"] = "Предупреждение",
                ["WarningMessage"] = "Для действий с достижениями нужен Web API ключ. Получить его можно на сайте Steam. После чего вставить в настройках приложения.",
                ["CloseInfoButton"] = "Закрыть",
                ["SettingsButton"] = "Настройки",
                ["SearchGameAchievement"] = "🔍 Поиск игры по названию или AppID..."
            },
            ["en"] = new()
            {
                ["Login"] = "Login",
                ["Password"] = "Password",
                ["AppId"] = "AppID (optional)",
                ["Remember"] = "Remember me",
                ["SignIn"] = "✅ Sign In",
                ["Start"] = "⏩ Start Farming",
                ["Stop"] = "⏹ Stop Farming",
                ["Auth"] = "AUTHORIZATION",
                ["Manage"] = "FARM MANAGEMENT",
                ["Timer"] = "FARM TIMER",
                ["FarmNotStarted"] = "Farming not started",
                ["TabLibrary"] = "🎮 Library games",
                ["TabAch"] = "🏆 Achievements",
                ["Search"] = "🔍 Search by name or AppID...",
                ["LogWait"] = "There are no events. I'm waiting for your actions...\n",
                ["LogHeader"] = "Event log",
                ["StatusWait"] = "● Status: Waiting to enter",
                ["SettingsHeader"] = "Application settings",
                ["TextLanguageInterface"] = "INTERFACE LANGUAGE",
                ["TextWebApiKey"] = "WEB API KEY",
                ["CloseAndSaveButton"] = "Save and close",
                ["WarningTitle"] = "Warning",
                ["WarningMessage"] = "To work with achievements, you need a Web API key. You can get it on the Steam website. After that, insert it into the application settings.",
                ["CloseInfoButton"] = "Close",
                ["SettingsButton"] = "Settings",
                ["SearchGameAchievement"] = "🔍 Search for a game by name or AppID..."
            }
        };
        public MainWindow()
        {
            InitializeComponent();
            CheckLanguageFile();
            LoadConfig();

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
                if (Directory.Exists(configDirectory) && File.Exists(configPath))
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
                Log(currentLang == "en" ? $"⚠️ Не удалось загрузить конфиг: {ex.Message}" : $"⚠️ Failed to load config: {ex.Message}");
            }
        }


        private void SaveConfig()
        {
            try
            {
                bool rememberMe = rememberMeCheckBox.IsChecked ?? false;


                if (rememberMe)
                {
                    Directory.CreateDirectory(configDirectory);
                }


                if (Directory.Exists(configDirectory))
                {
                    var config = new AppConfig
                    {
                        RememberMe = rememberMe,
                        Login = rememberMe ? (loginBox.Text?.Trim() ?? "") : "",
                        Password = rememberMe ? (passwordBox.Text?.Trim() ?? "") : ""
                    };

                    string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(configPath, json);
                }
            }
            catch (Exception ex)
            {
                Log(currentLang == "en" ? $"⚠️ Failed to save config: {ex.Message}" : $"⚠️ Не удалось сохранить конфиг: {ex.Message}");
            }
        }
        private void CheckLanguageFile()
        {
            if (File.Exists(langFilePath))
            {
                string lang = File.ReadAllText(langFilePath).Trim();
                ApplyLanguage(lang);
                languageSelectionPanel.IsVisible = false;
            }
            else
            {
                languageSelectionPanel.IsVisible = true;
            }
        }
        private void LangRuBtn_Click(object? sender, RoutedEventArgs e)
        {
            File.WriteAllText(langFilePath, "ru");
            ApplyLanguage("ru");
            languageSelectionPanel.IsVisible = false;
        }

        private void LangEnBtn_Click(object? sender, RoutedEventArgs e)
        {
            File.WriteAllText(langFilePath, "en");
            ApplyLanguage("en");
            languageSelectionPanel.IsVisible = false;
        }

        private void ApplyLanguage(string lang)
        {
            currentLang = lang;
            var t = localizedText[lang]; // Берем нужный словарь по коду языка

            loginBox.PlaceholderText = t["Login"];
            passwordBox.PlaceholderText = t["Password"];
            appIdBox.PlaceholderText = t["AppId"];
            rememberMeCheckBox.Content = t["Remember"];
            loginBtn.Content = t["SignIn"];
            startBtn.Content = t["Start"];
            stopBtn.Content = t["Stop"];
            tbAuth.Text = t["Auth"];
            tbManagementFarm.Text = t["Manage"];
            tbTimerFarm.Text = t["Timer"];
            farmingInfoLabel.Text = t["FarmNotStarted"];
            tiLibrary.SetValue(TabItem.HeaderProperty, t["TabLibrary"]);
            tiAchievements.SetValue(TabItem.HeaderProperty, "🏆 " + t["TabAch"].Replace("🏆 ", ""));
            tbSearchBox.PlaceholderText = t["Search"];
            logBox.Text = t["LogWait"];
            tbLogHeader.Text = t["LogHeader"];
            tbStatusLabel.Text = t["StatusWait"];
            tbSettingsHeader.Text = t["SettingsHeader"];
            tbLanguageInterface.Text = t["TextLanguageInterface"];
            tbWebApiKey.Text = t["TextWebApiKey"];
            closeAndSaveSettingsBtn.SetValue(Button.ContentProperty, t["CloseAndSaveButton"]);
            warningTitle.Text = t["WarningTitle"];
            warningMessage.Text = t["WarningMessage"];
            closeInfoBtn.SetValue(Button.ContentProperty, t["CloseInfoButton"]);
            tbSettings.Text = t["SettingsButton"];
            tbSearchGameAchievementsBox.PlaceholderText = t["SearchGameAchievement"];
        }
        private void LoginBtn_Click(object? sender, RoutedEventArgs e)
        {
            logBox.Text = string.Empty;
            if (string.IsNullOrEmpty(loginBox.Text) || string.IsNullOrEmpty(passwordBox.Text))
            {
                Log(currentLang == "en" ? "❌ Please enter login and password!" : "❌ Введите логин и пароль!");
                return;
            }


            SaveConfig();

            var initialAppIds = ParseAppIdsFromInput();
            uint firstAppId = initialAppIds.FirstOrDefault();

            guardPanel.IsVisible = false;
            loginBtn.IsEnabled = false;

            var login = loginBox.Text.Trim();
            var pass = passwordBox.Text.Trim();
            Log(currentLang == "en" ? $"🔑 Signing in: {login}..." : $"🔑 Вход в аккаунт: {login}...");

            worker = new SteamWorker(login, pass, firstAppId, currentLang);
            worker.OnLogMessage += (msg) => Log(msg);

            worker.OnLoginSuccess += () => Dispatcher.UIThread.Post(() =>
            {
                loginBtn.IsEnabled = true;
                startBtn.IsEnabled = true;
                tbStatusLabel.Text = currentLang == "en" ? "● Status: Online" : "● Статус: В сети";
                tbStatusLabel.Foreground = accentGreen;
                guardPanel.IsVisible = false;
            });

            worker.OnAvatarHashReceived += (hash) => Dispatcher.UIThread.Post(() =>
            {

                avatarHash = hash;

                if (string.IsNullOrWhiteSpace(avatarHash))
                {
                    avatarHash = "fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb";
                }

                avatarUrl = $"https://avatars.steamstatic.com/{avatarHash}_full.jpg";

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
                Log(currentLang == "en" ? $"❌ Error: {error}" : $"❌ Ошибка: {error}");
                guardPanel.IsVisible = false;
            });

            worker.OnGuardRequired += () => Dispatcher.UIThread.Post(() =>
            {
                guardPanel.IsVisible = true;
                guardBox.Text = "";
                guardBox.Focus();
                loginBtn.IsEnabled = true;
                Log(currentLang == "en" ? "🔐 Steam Guard code required!" : "🔐 Требуется код Steam Guard!");
                tbStatusLabel.Text = "● Статус: Ждет Guard";
                tbStatusLabel.Foreground = Brushes.Orange;
            });

            worker.OnAutoFarmingResumed += () => Dispatcher.UIThread.Post(() =>
            {
                activeFarmingAppIds.Clear();

                if (worker.CurrentFarmingAppIds != null)
                {
                    foreach (var id in worker.CurrentFarmingAppIds)
                    {
                        activeFarmingAppIds.Add(id);
                    }
                }

                startBtn.IsEnabled = false;
                loginBtn.IsEnabled = false;
                stopBtn.IsEnabled = true;

                tbStatusLabel.Text = currentLang == "en" ? "● FARMING ACTIVE" : "● ФАРМИНГ АКТИВЕН";
                tbStatusLabel.Foreground = accentGreen;
                timerText.Foreground = accentGreen;
                timerCardPanel.BorderBrush = accentGreen;

                farmingInfoLabel.Text = currentLang == "en" ? $"Running games ({activeFarmingAppIds.Count}): {string.Join(", ", activeFarmingAppIds)}" : $"Запущенные игры ({activeFarmingAppIds.Count}): {string.Join(", ", activeFarmingAppIds)}";

                StartFarmTimer();
                RenderGameCards(loadedGames);
            });

            worker.Start();
        }

        private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (loadedGames == null || loadedGames.Count == 0) return;

            string query = tbSearchBox.Text?.Trim().ToLower() ?? "";

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

        private void RenderGameCards(List<SteamGameInfo> games, bool isAchievements = false)
        {
            var targetPanel = isAchievements ? gamesAchievementsFlowPanel : gamesFlowPanel;

            // Выбираем правильный скроллер для текущего режима
            var currentScrollViewer = isAchievements ? gamesScrollAchievementsViewer : gamesScrollViewer;

            // Защита от вызова до инициализации компонентов в XAML
            if (targetPanel == null || currentScrollViewer == null) return;

            if (games == null || games.Count == 0)
            {
                currentScrollViewer.Content = new TextBlock
                {
                    Text = currentLang == "en" ? "No games found." : "Игры не найдены.",
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

            // Возвращаем нужную панель в её собственный скроллер
            currentScrollViewer.Content = targetPanel;
            targetPanel.Children.Clear();

            foreach (var game in games)
            {
                double hours = Math.Round(game.PlaytimeForever / 60.0, 1);
                bool isFarming = activeFarmingAppIds.Contains(game.AppId);

                // В достижениях карточка больше не подсвечивается как выбранная
                bool isSelected = !isAchievements && selectedAppIds.Contains(game.AppId);

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

                if (isFarming && !isAchievements)
                {
                    var statusBadge = new TextBlock
                    {
                        Text = currentLang == "en" ? "▶ FARMING" : "▶ ФАРМИТСЯ",
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

                string infoTextContent;
                if (isAchievements)
                {
                    int unlockedAch = SteamUnlockAchievements.GetUnlockedCount(game.AppId);
                    int totalAch = SteamUnlockAchievements.GetTotalCount(game.AppId);

                    infoTextContent = currentLang == "en"
                        ? $"🏆 {unlockedAch}/{totalAch} achievements"
                        : $"🏆 {unlockedAch}/{totalAch} достижений";
                }
                else
                {
                    infoTextContent = currentLang == "en" ? $"⏱ {hours} h | ID: {game.AppId}" : $"⏱ {hours} ч. | ID: {game.AppId}";
                }

                var infoText = new TextBlock
                {
                    Text = infoTextContent,
                    FontWeight = FontWeight.Bold,
                    FontSize = 10,
                    Foreground = isFarming && !isAchievements ? accentGreen : (isSelected ? Brushes.White : accentBlue),
                    Margin = new Avalonia.Thickness(0, 5, 0, 0)
                };
                Grid.SetRow(infoText, 1);
                Grid.SetColumn(infoText, 0);
                Grid.SetColumnSpan(infoText, 2);

                grid.Children.Add(img);
                grid.Children.Add(titlePanel);
                grid.Children.Add(infoText);
                card.Child = grid;

                card.PointerPressed += async (s, e) =>
                {
                    if (isAchievements)
                    {
                        await ShowGameAchievementsDetail(game.AppId, game.Name);
                    }
                    else
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

                        // Перерисовываем обе панели, чтобы стили обновились мгновенно
                        RenderGameCards(loadedGames, false);
                        RenderGameCards(loadedGames, true);
                    }
                };

                targetPanel.Children.Add(card);
            }

            targetPanel.InvalidateMeasure();
            targetPanel.InvalidateVisual();
        }
        private async Task ShowGameAchievementsDetail(uint appId, string gameName)
        {
            var stats = SteamUnlockAchievements.GetStatsForGame(appId);

            if (achievementsFlowPanel == null) return;

            achievementsFlowPanel.Children.Clear();

            if (stats.Achievements == null || stats.Achievements.Count == 0)
            {
                achievementsFlowPanel.Children.Add(new TextBlock
                {
                    Text = currentLang == "en" ? "No achievements found for this game." : "У этой игры нет достижений.",
                    Foreground = Brushes.Gray,
                    Margin = new Avalonia.Thickness(5)
                });
            }
            else
            {
                // Создаем карточки достижений с иконками
                foreach (var ach in stats.Achievements)
                {
                    var card = new Border
                    {
                        Width = 200,
                        Height = 80,
                        Margin = new Avalonia.Thickness(5),
                        Background = SolidColorBrush.Parse(ach.IsUnlocked ? "#1E382B" : "#2A313D"),
                        BorderBrush = ach.IsUnlocked ? SolidColorBrush.Parse("#4CAF50") : Brushes.Transparent,
                        BorderThickness = new Avalonia.Thickness(ach.IsUnlocked ? 2 : 0),
                        CornerRadius = new Avalonia.CornerRadius(4)
                    };

                    var grid = new Grid
                    {
                        ColumnDefinitions = ColumnDefinitions.Parse("40,*"),
                        Margin = new Avalonia.Thickness(6)
                    };

                    // Иконка достижения
                    var img = new Image
                    {
                        Width = 32,
                        Height = 32,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                    };

                    if (!string.IsNullOrEmpty(ach.IconUrl))
                    {
                        System.Diagnostics.Debug.WriteLine($"Achievement Icon URL: {ach.IconUrl}");
                        // Запускаем асинхронную загрузку, передавая картинку в UI-поток
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // Если ваш LoadImageAsync возвращает готовый Bitmap или сам присваивает — вызываем его
                                // Либо используем стандартную логику загрузки через LoadImageAsync:
                                await LoadImageAsync(ach.IconUrl, img);
                            }
                            catch
                            {
                                // Игнорируем сетевые ошибки отдельных иконок
                            }
                        });
                    }

                    Grid.SetColumn(img, 0);

                    // Текстовая часть (Название + Статус)
                    var textBlock = new TextBlock
                    {
                        Text = $"{ach.Name}\n{(ach.IsUnlocked ? (currentLang == "en" ? "✅ Unlocked" : "✅ Получено") : (currentLang == "en" ? "🔒 Locked" : "🔒 Заблокировано"))}",
                        Foreground = Brushes.White,
                        FontSize = 10,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Margin = new Avalonia.Thickness(6, 0, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    };

                    Grid.SetColumn(textBlock, 1);

                    grid.Children.Add(img);
                    grid.Children.Add(textBlock);
                    card.Child = grid;

                    achievementsFlowPanel.Children.Add(card);
                }
            }

            if (gamesAchievementsFlowPanel != null)
            {
                gamesAchievementsFlowPanel.IsVisible = true;
                RenderGameCards(loadedGames, true);
            }

            achievementsFlowPanel.IsVisible = true;
            achievementsFlowPanel.InvalidateMeasure();
            achievementsFlowPanel.InvalidateVisual();
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
        private void tc_SelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
        {
            if (e.Source is Avalonia.Controls.TabControl tabControl)
            {
                if (tabControl.SelectedItem is TabItem selectedTab)
                {
                    if (selectedTab.Name == "tiAchievements")
                    {
                        if (!System.IO.File.Exists(webApiKeyPath))
                        {
                            warningOverlay.IsVisible = true;
                            tcMain.SelectedItem = tiLibrary;
                        }
                        if (loadedGames == null || loadedGames.Count == 0 || worker == null) return;

                        // Показываем текст загрузки через ScrollViewer (заменяем содержимое на время загрузки)
                        if (gamesScrollAchievementsViewer != null)
                        {
                            gamesScrollAchievementsViewer.Content = new TextBlock
                            {
                                Text = currentLang == "en" ? "🔄 Loading achievements..." : "🔄 Загрузка достижений...",
                                Margin = new Avalonia.Thickness(15),
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                                Foreground = Avalonia.Media.Brushes.White,
                                FontSize = 14
                            };
                        }

                        ulong steamId = worker.SteamID;
                        Log(currentLang == "en" ? "🔄 Loading achievements statistics..." : "🔄 Загрузка статистики достижений...");

                        tabControl.IsEnabled = false;

                        Task.Run(async () =>
                        {
                            var tasks = new List<Task>();

                            foreach (var game in loadedGames)
                            {
                                var existingStats = SteamUnlockAchievements.GetStatsForGame(game.AppId);
                                if (existingStats.TotalAchievements > 0) continue;

                                tasks.Add(SteamUnlockAchievements.LoadAchievementsAsync(steamId, game.AppId));
                            }

                            if (tasks.Count > 0)
                            {
                                await Task.WhenAll(tasks);
                            }

                            Dispatcher.UIThread.Post(() =>
                            {
                                tabControl.IsEnabled = true;

                                if (tabControl.SelectedItem is TabItem currentTab && currentTab.Name == "tiAchievements")
                                {
                                    // Возвращаем WrapPanel обратно в ScrollViewer перед отрисовкой карточек
                                    if (gamesScrollAchievementsViewer != null && gamesAchievementsFlowPanel != null)
                                    {
                                        gamesScrollAchievementsViewer.Content = gamesAchievementsFlowPanel;
                                    }

                                    RenderGameCards(loadedGames, true);
                                }
                                Log(currentLang == "en" ? "✅ Achievements updated!" : "✅ Достижения обновлены!");

                                var window = Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Window>(gamesAchievementsFlowPanel);
                                window?.InvalidateVisual();

                            }, Avalonia.Threading.DispatcherPriority.Render);
                        });
                    }
                    else if (selectedTab.Name == "tiLibrary")
                    {
                        RenderGameCards(loadedGames, false);
                    }
                }
            }
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
            Log(currentLang == "en" ? $"📨 Submitting Guard code: {code}" : $"📨 Отправка кода Guard: {code}");
            tbStatusLabel.Text = currentLang == "en" ? "● Verifying code..." : "● Проверка кода...";
            tbStatusLabel.Foreground = Brushes.Yellow;

            worker?.SubmitGuardCode(code);
        }

        private void GuardApproveBtn_Click(object? sender, RoutedEventArgs e)
        {
            guardPanel.IsVisible = false;
            loginBtn.IsEnabled = false;
            Log(currentLang == "en" ? "📨 Awaiting confirmation from the mobile app..." : "📨 Ожидание подтверждения из мобильного приложения...");
        }

        private void StartBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (worker == null)
            {
                Log(currentLang == "en" ? "❌ First, please log in!" : "❌ Сначала авторизуйтесь!");
                return;
            }

            List<uint> appIdsToStart = ParseAppIdsFromInput();

            if (appIdsToStart.Count == 0)
            {
                Log(currentLang == "en" ? "❌ Please select at least one game from the list or enter an AppID!" : "❌ Выберите хотя бы одну игру из списка или введите AppID!");
                return;
            }

            if (appIdsToStart.Count > 32)
            {
                Log(currentLang == "en" ? "⚠️ Steam limit: no more than 32 games at a time. Starting the first 32." : "⚠️ Лимит Steam: не более 32 игр одновременно. Запускаются первые 32.");
                appIdsToStart = appIdsToStart.Take(32).ToList();
            }

            Log(currentLang == "en" ? $"▶️ Starting farming for {appIdsToStart.Count} games: {string.Join(", ", appIdsToStart)}" : $"▶️ Запуск фарминга для {appIdsToStart.Count} игр: {string.Join(", ", appIdsToStart)}...");

            worker.StartIdling(appIdsToStart);

            activeFarmingAppIds.Clear();
            foreach (var id in appIdsToStart)
            {
                activeFarmingAppIds.Add(id);
            }

            startBtn.IsEnabled = false;
            loginBtn.IsEnabled = false;
            stopBtn.IsEnabled = true;

            tbStatusLabel.Text = currentLang == "en" ? "● FARMING ACTIVE" : "● ФАРМИНГ АКТИВЕН";
            tbStatusLabel.Foreground = accentGreen;
            timerText.Foreground = accentGreen;
            timerCardPanel.BorderBrush = accentGreen;

            farmingInfoLabel.Text = currentLang == "en" ? $"Farming games ({activeFarmingAppIds.Count}): {string.Join(", ", activeFarmingAppIds)}" : $"Запущенные игры ({activeFarmingAppIds.Count}): {string.Join(", ", activeFarmingAppIds)}";

            StartFarmTimer();
            RenderGameCards(loadedGames);
        }

        private void StopBtn_Click(object? sender, RoutedEventArgs e)
        {
            Log(currentLang == "en" ? "⏹ Stopping farming..." : "⏹ Остановка фарминга...");
            worker?.StopIdling();

            activeFarmingAppIds.Clear();

            StopFarmTimer();

            startBtn.IsEnabled = true;
            stopBtn.IsEnabled = false;

            tbStatusLabel.Text = currentLang == "en" ? "● Status: Online (Farming stopped)" : "● Статус: В сети (Фарм остановлен)";
            tbStatusLabel.Foreground = accentBlue;
            timerText.Foreground = accentBlue;
            timerText.Text = "00:00:00";
            timerCardPanel.BorderBrush = borderDefault;
            farmingInfoLabel.Text = currentLang == "en" ? "Farming not active" : "Фарминг не запущен";

            RenderGameCards(loadedGames);
        }
        private void tbSearchGameAchievementsBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (loadedGames == null || loadedGames.Count == 0) return;

            string query = tbSearchGameAchievementsBox.Text?.Trim().ToLower() ?? "";

            if (string.IsNullOrEmpty(query))
            {
                RenderGameCards(loadedGames, true);
                return;
            }

            var filteredGames = loadedGames.Where(g =>
                (g.Name != null && g.Name.ToLower().Contains(query)) ||
                g.AppId.ToString().Contains(query)
            ).ToList();

            RenderGameCards(filteredGames, true);
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

                if (months > 0) parts.Add(currentLang == "en" ? $"{months} mo." : $"{months} мес.");
                if (weeks > 0) parts.Add(currentLang == "en" ? $"{weeks} w." : $"{weeks} нед.");
                if (days > 0) parts.Add(currentLang == "en" ? $"{days} d." : $"{days} д.");

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

        private void settingsBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (languageComboBox != null)
            {
                foreach (ComboBoxItem item in languageComboBox.Items)
                {
                    if (item.Tag?.ToString() == currentLang)
                    {
                        languageComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
            if (System.IO.File.Exists(webApiKeyPath))
            {
                webApiKeyTextBox.Text = File.ReadAllText(webApiKeyPath).Trim();
            }
            settingsOverlayPanel.IsVisible = true;
        }

        private void closeAndSaveSettingsBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (languageComboBox?.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is string newLang)
            {

                if (newLang != currentLang)
                {
                    File.WriteAllText(langFilePath, newLang);
                    ApplyLanguage(newLang);

                }
            }
            if (webApiKeyTextBox != null)
            {

                if (!Directory.Exists(configDirectory))
                {
                    Directory.CreateDirectory(configDirectory);
                }

                File.WriteAllText(webApiKeyPath, webApiKeyTextBox.Text?.Trim() ?? string.Empty);
            }
            if (isDelWebApiKey) {
                File.Delete(webApiKeyPath);
                isDelWebApiKey = false;
            }
            settingsOverlayPanel.IsVisible = false;
        }

        private void closeInfoBtn_Click(object? sender, RoutedEventArgs e)
        {
            warningOverlay.IsVisible = false;

        }

        private void clearWebApiKeyBtn_Click(object? sender, RoutedEventArgs e)
        {
            isDelWebApiKey = true;
            webApiKeyTextBox.Text = string.Empty;
            
        }
    }
}