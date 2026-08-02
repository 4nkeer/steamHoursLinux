using SteamKit2;
using SteamKit2.Internal;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Threading.Timer;

namespace steamHoursLinux
{
    internal class SteamWorker : IDisposable
    {
        public event Action<string>? OnAvatarHashReceived;
        string currentLang = string.Empty;
        public List<uint> CurrentFarmingAppIds => currentFarmingAppIds;
        public event Action OnAutoFarmingResumed;
        private SteamClient steamClient;
        private CallbackManager manager;
        private SteamUser steamUser;
        private SteamFriends steamFriends;

        private readonly SteamAuthenticator authenticator;
        private readonly SteamLibraryLoader libraryLoader;

        private bool isRunning;
        private List<uint> currentFarmingAppIds = new List<uint>();
        private bool isGameRunning = false;
        private Timer keepAliveTimer;
        private bool isLoggedOn = false;
        private bool isReconnecting = false;

        private bool shouldResumeIdling = false;

        public List<SteamGameInfo> UserGames { get; private set; } = new List<SteamGameInfo>();
        public ulong SteamID => steamClient?.SteamID?.ConvertToUInt64() ?? 0;

        public event Action<string> OnLogMessage;
        public event Action OnLoginSuccess;
        public event Action<string> OnLoginFailed;
        public event Action OnGuardRequired;
        public event Action<List<SteamGameInfo>> OnLibraryLoaded;

        private Thread workerThread;
        private ConcurrentQueue<Action> actionQueue = new ConcurrentQueue<Action>();

        public SteamWorker(string login, string pass, uint defaultAppId, string lang)
        {
            currentLang = lang;
            if (defaultAppId > 0)
                currentFarmingAppIds.Add(defaultAppId);

            this.authenticator = new SteamAuthenticator(login, pass, lang);
            this.authenticator.OnLogMessage += message => Log(message);
            this.authenticator.OnGuardRequired += () => OnGuardRequired?.Invoke();

            this.libraryLoader = new SteamLibraryLoader(lang);
            this.libraryLoader.OnLogMessage += message => Log(message);
        }

        public void Start()
        {
            if (workerThread != null && workerThread.IsAlive)
                return;

            isRunning = true;
            isReconnecting = false;

            authenticator.LoadTokens();

            workerThread = new Thread(Run)
            {
                IsBackground = true
            };
            workerThread.Start();
        }

        public void Stop()
        {
            isRunning = false;
            shouldResumeIdling = false;
            StopIdling();

            authenticator.CancelPendingAuth();

            if (steamClient != null && steamClient.IsConnected)
                steamClient.Disconnect();
        }

        public void Dispose()
        {
            try
            {
                Stop();
                keepAliveTimer?.Dispose();
                if (workerThread != null && workerThread.IsAlive)
                {
                    workerThread.Join(500);
                }
            }
            catch { }
        }

        public void SubmitGuardCode(string code)
        {
            actionQueue.Enqueue(() =>
            {
                authenticator.SubmitGuardCode(code);
            });
        }

        private void Run()
        {
            try
            {
                CreateClient();

                while (isRunning)
                {
                    manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));

                    while (actionQueue.TryDequeue(out var act))
                    {
                        try { act(); } catch (Exception ex) { Log(currentLang == "en" ? $"⚠️ Error in action: {ex.Message}" : $"⚠️ Ошибка в action: {ex.Message}"); }
                    }

                    Thread.Sleep(10);
                }

                Log("🛑 Работа остановлена");
            }
            catch (Exception ex)
            {
                Log(currentLang == "en" ? $"❌ Error in main thread: {ex.Message}" : $"❌ Ошибка в основном потоке: {ex.Message}");
                OnLoginFailed?.Invoke(ex.Message);
            }
        }

        private void CreateClient()
        {
            try
            {
                steamClient = new SteamClient();
                manager = new CallbackManager(steamClient);
                steamUser = steamClient.GetHandler<SteamUser>();
                steamFriends = steamClient.GetHandler<SteamFriends>();

                manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
                manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
                manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
                manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

                manager.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsList);

                Log(currentLang == "en" ? "🔌 Connecting to Steam..." : "🔌 Подключение к Steam...");
                steamClient.Connect();
            }
            catch (Exception ex)
            {
                Log(currentLang == "en" ? $"❌ Error creating client: {ex.Message}" : $"❌ Ошибка создания клиента: {ex.Message}");
            }
        }

        private async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            Log(currentLang == "en" ? "✅ Connected to Steam" : "✅ Подключено к Steam");

            if (authenticator.IsAuthInProgress || isLoggedOn)
                return;

            if (!string.IsNullOrEmpty(authenticator.RefreshToken))
            {
                authenticator.LoginWithToken(steamUser, steamClient, isLoggedOn);
                return;
            }

            _ = authenticator.AuthenticateWithCredentialsAsync(
                steamClient,
                steamUser,
                act => actionQueue.Enqueue(act),
                err => OnLoginFailed?.Invoke(err)
            );
        }

        private async void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.LoggedInElsewhere)
            {
                Log(currentLang == "en" ? "🎮 Another instance of the application is running. Waiting for session to end..." : "🎮 Обнаружена ваша игра на ПК. Бот ждет завершения сессии...");
                isLoggedOn = false;
                ScheduleReconnectAfterKick();
                return;
            }

            if (callback.Result == EResult.AccessDenied || callback.Result == EResult.InvalidPassword || callback.Result == EResult.Expired)
            {
                Log(currentLang == "en" ? $"⚠️ Failed to login with token ({callback.Result}). Clearing tokens..." : $"⚠️ Не удалось войти по токену ({callback.Result}). Очищаем токены...");
                authenticator.ClearTokens();

                if (steamClient != null && steamClient.IsConnected)
                {
                    steamClient.Disconnect();
                }
                return;
            }

            if (callback.Result == EResult.RateLimitExceeded)
            {
                Log(currentLang == "en" ? "⏳ Rate limit exceeded. Waiting before retry..." : "⏳ Лимит запросов превышен. Ожидание перед повторной попыткой...");
                ScheduleReconnectAfterKick();
                return;
            }

            if (callback.Result != EResult.OK)
            {
                Log(currentLang == "en" ? $"❌ Error logging in: {callback.Result}" : $"❌ Ошибка входа: {callback.Result}");
                OnLoginFailed?.Invoke(currentLang == "en" ? $"Error: {callback.Result}" : $"Ошибка: {callback.Result}");
                return;
            }

            Log(currentLang == "en" ? $"✅ Successful login! SteamID: {steamClient.SteamID}" : $"✅ Успешный вход в аккаунт! SteamID: {steamClient.SteamID}");

            isLoggedOn = true;
            authenticator.CurrentSteamId = steamClient.SteamID;
            authenticator.SaveTokens(authenticator.RefreshToken, authenticator.AccessToken, authenticator.CurrentSteamId);

            if (steamFriends != null)
            {
                try
                {
                    steamFriends.SetPersonaState(EPersonaState.Online);
                    Log(currentLang == "en" ? "🟢 Status: Online" : "🟢 Статус: В сети");
                }
                catch { }
            }
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                FetchAndSendAvatar();
            });
            _ = LoadUserLibraryAsync();
            OnLoginSuccess?.Invoke();

            if (shouldResumeIdling && currentFarmingAppIds.Count > 0)
            {
                Log(currentLang == "en" ? $"🚀 You have logged out of the game! Automatically resuming farming for {currentFarmingAppIds.Count} games..." : $"🚀 Вы вышли из игры! Автоматическое возобновление фарма для {currentFarmingAppIds.Count} игр...");
                StartIdling(currentFarmingAppIds);
                OnAutoFarmingResumed?.Invoke();
            }
        }
        private void FetchAndSendAvatar()
        {
            try
            {
                if (steamClient?.SteamID == null || steamFriends == null) return;

                byte[] avatarHashBytes = steamFriends.GetFriendAvatar(steamClient.SteamID);
                string avatarHash = string.Empty;

                if (avatarHashBytes != null && avatarHashBytes.Length > 0 && !avatarHashBytes.All(b => b == 0))
                {
                    avatarHash = BitConverter.ToString(avatarHashBytes).Replace("-", "").ToLowerInvariant();
                    Log(currentLang == "en" ? $"🖼 Avatar hash successfully received: {avatarHash}" : $"🖼 Хэш аватара успешно получен: {avatarHash}");
                }
                else
                {
                    Log(currentLang == "en" ? "⚠️ Avatar hash is empty, using default." : "⚠️ Хэш аватара пустой, будет использован дефолтный.");
                }

                OnAvatarHashReceived?.Invoke(avatarHash);
            }
            catch (Exception ex)
            {
                Log(currentLang == "en" ? $"⚠️ Error occurred while fetching avatar: {ex.Message}" : $"⚠️ Ошибка при запросе аватара: {ex.Message}");
                OnAvatarHashReceived?.Invoke(string.Empty);
            }
        }
        private void OnFriendsList(SteamFriends.FriendsListCallback callback)
        {
            try
            {
                if (steamClient?.SteamID == null) return;

                byte[] avatarHashBytes = steamFriends.GetFriendAvatar(steamClient.SteamID);
                string avatarHash = string.Empty;

                if (avatarHashBytes != null && avatarHashBytes.Length > 0 && !avatarHashBytes.All(b => b == 0))
                {
                    avatarHash = BitConverter.ToString(avatarHashBytes).Replace("-", "").ToLowerInvariant();
                }

                if (!string.IsNullOrEmpty(avatarHash))
                {
                    OnAvatarHashReceived?.Invoke(avatarHash);
                }
            }
            catch (Exception ex)
            {
                Log(currentLang == "en" ? $"⚠️ Error occurred while fetching avatar: {ex.Message}" : $"⚠️ Ошибка получения аватара: {ex.Message}");
            }
        }

        private async Task LoadUserLibraryAsync()
        {
            if (steamClient?.SteamID == null) return;

            ulong steamId = steamClient.SteamID.ConvertToUInt64();
            string token = authenticator.AccessToken;

            UserGames = await libraryLoader.GetOwnedGamesAsync(steamId, token);
            OnLibraryLoaded?.Invoke(UserGames);
        }

        public void StartIdling(IEnumerable<uint> appIds)
        {
            if (steamClient == null || !steamClient.IsConnected) return;

            currentFarmingAppIds = appIds.ToList();
            shouldResumeIdling = true;

            var gamesPlayed = new SteamKit2.ClientMsgProtobuf<SteamKit2.Internal.CMsgClientGamesPlayed>(SteamKit2.EMsg.ClientGamesPlayed);

            foreach (var id in currentFarmingAppIds)
            {
                gamesPlayed.Body.games_played.Add(new SteamKit2.Internal.CMsgClientGamesPlayed.GamePlayed
                {
                    game_id = id
                });
            }

            steamClient.Send(gamesPlayed);
            isGameRunning = true;
            StartKeepAlive();
            Log(currentLang == "en" ? $"🎮 Idling started for games: {string.Join(", ", currentFarmingAppIds)}" : $"🎮 Накрутка часов запущена для игр: {string.Join(", ", currentFarmingAppIds)}");
        }

        public void StopIdling()
        {
            shouldResumeIdling = false;
            isGameRunning = false;
            keepAliveTimer?.Dispose();

            if (steamClient != null && steamClient.IsConnected)
            {
                try
                {
                    var msg = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
                    LoadUserLibraryAsync();
                    steamClient.Send(msg);
                    Log(currentLang == "en" ? "⏹ Farming stopped." : "⏹ Фарм часов остановлен.");
                }
                catch (Exception ex)
                {
                    Log(currentLang == "en" ? $"⚠️ Error occurred while stopping farming: {ex.Message}" : $"⚠️ Ошибка при остановке фарма: {ex.Message}");
                }
            }
        }

        private void StartKeepAlive()
        {
            keepAliveTimer?.Dispose();
            keepAliveTimer = new Timer(
                (state) => SendKeepAlive(),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(60)
            );
        }

        private void SendKeepAlive()
        {
            if (!isRunning || !isGameRunning || steamClient == null || !steamClient.IsConnected)
                return;

            try
            {
                var gamesPlayed = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);

                foreach (var id in currentFarmingAppIds)
                {
                    gamesPlayed.Body.games_played.Add(new SteamKit2.Internal.CMsgClientGamesPlayed.GamePlayed
                    {
                        game_id = id
                    });
                }

                steamClient.Send(gamesPlayed);
                steamFriends?.SetPersonaState(EPersonaState.Online);
            }
            catch (Exception ex)
            {
                Log(currentLang == "en" ? $"⚠️ Error occurred while sending keepalive: {ex.Message}" : $"⚠️ Ошибка отправки keepalive: {ex.Message}");
            }
        }

        private async void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Log(currentLang == "en" ? "⚠️ Disconnected from Steam" : "⚠️ Отключено от Steam");
            isGameRunning = false;
            isLoggedOn = false;
            keepAliveTimer?.Dispose();

            if (currentFarmingAppIds.Count > 0)
            {
                shouldResumeIdling = true;
            }

            if (isRunning && !isReconnecting && !authenticator.IsAuthInProgress)
            {
                ScheduleReconnectAfterKick();
            }
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Log(currentLang == "en" ? $"👋 Session ended by Steam. Reason: {callback.Result}" : $"👋 Сессия завершена со стороны Steam. Причина: {callback.Result}");
            isGameRunning = false;
            isLoggedOn = false;
            keepAliveTimer?.Dispose();

            if (callback.Result == EResult.LoggedInElsewhere || callback.Result == EResult.LogonSessionReplaced)
            {
                Log(currentLang == "en" ? "🕹 You have started a game on another PC. The bot will wait for your session to end..." : "🕹 Вы запустили игру на ПК. Бот ждет окончания вашей сессии...");
                shouldResumeIdling = true;
                ScheduleReconnectAfterKick();
            }
        }

        private async void ScheduleReconnectAfterKick()
        {
            if (isReconnecting || !isRunning) return;

            isReconnecting = true;
            int delaySeconds = 60;

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

            if (isRunning)
            {
                try { steamClient?.Disconnect(); } catch { }
                CreateClient();
            }

            isReconnecting = false;
        }

        private void Log(string message) => OnLogMessage?.Invoke(message);
    }
}