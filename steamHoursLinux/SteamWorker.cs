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

        // Флаг: должен ли бот возобновить фарм после того, как освободится аккаунт
        private bool shouldResumeIdling = false;

        public List<SteamGameInfo> UserGames { get; private set; } = new List<SteamGameInfo>();

        public event Action<string> OnLogMessage;
        public event Action OnLoginSuccess;
        public event Action<string> OnLoginFailed;
        public event Action OnGuardRequired;
        public event Action<List<SteamGameInfo>> OnLibraryLoaded;

        private Thread workerThread;
        private ConcurrentQueue<Action> actionQueue = new ConcurrentQueue<Action>();

        public SteamWorker(string login, string pass, uint defaultAppId)
        {
            if (defaultAppId > 0)
                currentFarmingAppIds.Add(defaultAppId);

            this.authenticator = new SteamAuthenticator(login, pass);
            this.authenticator.OnLogMessage += message => Log(message);
            this.authenticator.OnGuardRequired += () => OnGuardRequired?.Invoke();

            this.libraryLoader = new SteamLibraryLoader();
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
                        try { act(); } catch (Exception ex) { Log($"⚠️ Ошибка в action: {ex.Message}"); }
                    }

                    Thread.Sleep(10);
                }

                Log("🛑 Работа остановлена");
            }
            catch (Exception ex)
            {
                Log($"❌ Ошибка в основном потоке: {ex.Message}");
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

                // Подписываемся на обновление списка друзей/профиля, чтобы гарантированно поймать аватар
                manager.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsList);

                Log("🔌 Подключение к Steam...");
                steamClient.Connect();
            }
            catch (Exception ex)
            {
                Log($"❌ Ошибка создания клиента: {ex.Message}");
            }
        }

        private async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            Log("✅ Подключено к Steam");

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
            // Если мы зашли, а Steam говорит, что сессия занята другим ПК (вы играете)
            if (callback.Result == EResult.LoggedInElsewhere)
            {
                Log("🎮 Обнаружена ваша игра на ПК. Бот ждет завершения сессии...");
                isLoggedOn = false;
                ScheduleReconnectAfterKick();
                return;
            }

            if (callback.Result == EResult.AccessDenied || callback.Result == EResult.InvalidPassword || callback.Result == EResult.Expired)
            {
                Log($"⚠️ Не удалось войти по токену ({callback.Result}). Очищаем токены...");
                authenticator.ClearTokens();

                if (steamClient != null && steamClient.IsConnected)
                {
                    steamClient.Disconnect();
                }
                return;
            }

            if (callback.Result == EResult.RateLimitExceeded)
            {
                Log("⏳ Лимит запросов превышен. Ожидание перед повторной попыткой...");
                ScheduleReconnectAfterKick();
                return;
            }

            if (callback.Result != EResult.OK)
            {
                Log($"❌ Ошибка входа: {callback.Result}");
                OnLoginFailed?.Invoke($"Ошибка: {callback.Result}");
                return;
            }

            Log($"✅ Успешный вход в аккаунт! SteamID: {steamClient.SteamID}");

            isLoggedOn = true;
            authenticator.CurrentSteamId = steamClient.SteamID;
            authenticator.SaveTokens(authenticator.RefreshToken, authenticator.AccessToken, authenticator.CurrentSteamId);

            if (steamFriends != null)
            {
                try
                {
                    steamFriends.SetPersonaState(EPersonaState.Online);
                    Log("🟢 Статус: В сети");
                }
                catch { }
            }
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500); // Даем секунду-полторы на инициализацию сеанса связи
                FetchAndSendAvatar();
            });
            _ = LoadUserLibraryAsync();
            OnLoginSuccess?.Invoke();

            // Если бот должен фармить (была активна задача фарминга до этого)
            if (shouldResumeIdling && currentFarmingAppIds.Count > 0)
            {
                Log($"🚀 Вы вышли из игры! Автоматическое возобновление фарма для {currentFarmingAppIds.Count} игр...");
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
                    Log($"🖼 Хэш аватара успешно получен: {avatarHash}");
                }
                else
                {
                    Log("⚠️ Хэш аватара пустой, будет использован дефолтный.");
                }

                // Вызываем событие, которое поймает MainWindow
                OnAvatarHashReceived?.Invoke(avatarHash);
            }
            catch (Exception ex)
            {
                Log($"⚠️ Ошибка при запросе аватара: {ex.Message}");
                OnAvatarHashReceived?.Invoke(string.Empty);
            }
        }
        private void OnFriendsList(SteamFriends.FriendsListCallback callback)
        {
            try
            {
                if (steamClient?.SteamID == null) return;

                // Запрашиваем аватар для своего SteamID, когда данные от сети получены
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
                Log($"⚠️ Ошибка получения аватара: {ex.Message}");
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
            Log($"🎮 Накрутка часов запущена для игр: {string.Join(", ", currentFarmingAppIds)}");
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
                    Log("⏹ Фарм часов остановлен.");
                }
                catch (Exception ex)
                {
                    Log($"⚠️ Ошибка при остановке фарма: {ex.Message}");
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
                Log($"⚠️ Ошибка отправки keepalive: {ex.Message}");
            }
        }

        private async void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Log("⚠️ Отключено от Steam");
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
            Log($"👋 Сессия завершена со стороны Steam. Причина: {callback.Result}");
            isGameRunning = false;
            isLoggedOn = false;
            keepAliveTimer?.Dispose();

            if (callback.Result == EResult.LoggedInElsewhere || callback.Result == EResult.LogonSessionReplaced)
            {
                Log("🕹 Вы запустили игру на ПК. Бот ждет окончания вашей сессии...");
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