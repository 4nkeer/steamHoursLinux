using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using System;
using System.IO;
using System.Threading.Tasks;

namespace steamHoursLinux
{
    internal class SteamAuthenticator
    {
        private readonly string username;
        private readonly string password;
        private readonly string tokenFilePath;

        public string AccessToken { get; private set; }
        public string RefreshToken { get; private set; }
        public SteamID CurrentSteamId { get; set; }
        public bool IsAuthInProgress { get; private set; }

        private TaskCompletionSource<string> tfaTaskCompletionSource;

        public event Action<string> OnLogMessage;
        public event Action OnGuardRequired;

        public SteamAuthenticator(string username, string password)
        {
            this.username = username;
            this.password = password;

           
            string tokensDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config");
            Directory.CreateDirectory(tokensDirectory);

         
            this.tokenFilePath = Path.Combine(tokensDirectory, $"tokens_{username}.txt");
        }

        public void LoginWithToken(SteamUser steamUser, SteamClient steamClient, bool isLoggedOn)
        {
            if ((string.IsNullOrEmpty(RefreshToken) && string.IsNullOrEmpty(AccessToken)) ||
                steamClient == null || !steamClient.IsConnected || isLoggedOn)
                return;

            Log("🔑 Выполняем вход по токену авторизации...");

            var details = new SteamUser.LogOnDetails
            {
                Username = username,
                AccessToken = !string.IsNullOrEmpty(RefreshToken) ? RefreshToken : AccessToken,
                ShouldRememberPassword = true,
                ClientOSType = EOSType.Windows10
            };

            steamUser.LogOn(details);
        }

        public async Task AuthenticateWithCredentialsAsync(SteamClient steamClient, SteamUser steamUser, Action<Action> enqueueAction, Action<string> onLoginFailed)
        {
            if (IsAuthInProgress) return;

            if (steamClient == null || !steamClient.IsConnected)
            {
                Log("⚠️ Клиент не подключен. Авторизация будет выполнена после подключения.");
                return;
            }

            IsAuthInProgress = true;

            try
            {
                Log($"🔐 Начало авторизации для {username}...");

                var authDetails = new AuthSessionDetails
                {
                    Username = username,
                    Password = password,
                    IsPersistentSession = true,
                    PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient,
                    Authenticator = new CustomUserAuthenticator(GetTwoFactorCodeFromUserAsync)
                };

                var authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(authDetails).ConfigureAwait(false);

                Log("⏳ Ожидание завершения авторизации (проверка 2FA / мобильного подтверждения)...");
                var pollResult = await authSession.PollingWaitForResultAsync().ConfigureAwait(false);

                if (!string.IsNullOrEmpty(pollResult.RefreshToken))
                {
                    RefreshToken = pollResult.RefreshToken;
                    AccessToken = pollResult.AccessToken;

                    Log("✅ Токены успешно получены!");

                    enqueueAction(() =>
                    {
                        steamUser.LogOn(new SteamUser.LogOnDetails
                        {
                            Username = username,
                            AccessToken = RefreshToken,
                            ShouldRememberPassword = true,
                            ClientOSType = EOSType.Windows10
                        });
                    });
                }
                else
                {
                    Log("❌ Не удалось получить токены.");
                    onLoginFailed?.Invoke("Ошибка получения токенов");
                }
            }
            catch (TaskCanceledException)
            {
                Log("⚠️ Авторизация отменена из-за разрыва соединения.");
            }
            catch (AuthenticationException ex)
            {
                Log($"❌ Ошибка авторизации от Steam: {ex.Result}");
                onLoginFailed?.Invoke($"Steam Error: {ex.Result}");
            }
            catch (Exception ex)
            {
                Log($"❌ Ошибка авторизации: {ex.Message}");
                onLoginFailed?.Invoke(ex.Message);
            }
            finally
            {
                IsAuthInProgress = false;
            }
        }

        public void SubmitGuardCode(string code)
        {
            code = code?.Trim().ToUpperInvariant();
            Log($"📨 Введен код Steam Guard: {code}");

            if (!string.IsNullOrEmpty(code) && tfaTaskCompletionSource != null && !tfaTaskCompletionSource.Task.IsCompleted)
            {
                tfaTaskCompletionSource.TrySetResult(code);
            }
        }

        private async Task<string> GetTwoFactorCodeFromUserAsync()
        {
            Log("🔐 Требуется код Steam Guard (2FA)!");
            OnGuardRequired?.Invoke();

            tfaTaskCompletionSource = new TaskCompletionSource<string>();
            return await tfaTaskCompletionSource.Task.ConfigureAwait(false);
        }

        public void CancelPendingAuth()
        {
            tfaTaskCompletionSource?.TrySetCanceled();
        }

        #region Работа с файлом токенов

        public void LoadTokens()
        {
            try
            {
                if (File.Exists(tokenFilePath))
                {
                    string[] lines = File.ReadAllLines(tokenFilePath);
                    if (lines.Length > 0) RefreshToken = lines[0];
                    if (lines.Length > 1) AccessToken = lines[1];
                    if (lines.Length > 2 && ulong.TryParse(lines[2], out ulong steamId64))
                    {
                        CurrentSteamId = new SteamID(steamId64);
                    }

                    if (!string.IsNullOrEmpty(RefreshToken)) Log("🔑 Загружен RefreshToken");
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Ошибка загрузки токенов: {ex.Message}");
            }
        }

        public void SaveTokens(string refresh, string access, SteamID steamId)
        {
            try
            {
                CurrentSteamId = steamId ?? CurrentSteamId;
                string content = $"{refresh}\n{access}\n{(CurrentSteamId != null ? CurrentSteamId.ConvertToUInt64().ToString() : "")}";

                // На всякий случай проверяем директорию перед сохранением
                Directory.CreateDirectory(Path.GetDirectoryName(tokenFilePath));

                File.WriteAllText(tokenFilePath, content);
                Log("💾 Токены сохранены");
            }
            catch (Exception ex)
            {
                Log($"⚠️ Ошибка сохранения токенов: {ex.Message}");
            }
        }

        public void ClearTokens()
        {
            try
            {
                if (File.Exists(tokenFilePath))
                {
                    File.Delete(tokenFilePath);
                    Log("🗑️ Файл токенов удален");
                }
                AccessToken = null;
                RefreshToken = null;
                CurrentSteamId = null;
            }
            catch (Exception ex)
            {
                Log($"⚠️ Ошибка удаления токенов: {ex.Message}");
            }
        }

        #endregion

        private void Log(string message) => OnLogMessage?.Invoke(message);
    }

    internal class CustomUserAuthenticator : IAuthenticator
    {
        private readonly Func<Task<string>> _getTwoFactorCodeFunc;

        public CustomUserAuthenticator(Func<Task<string>> getTwoFactorCodeFunc)
        {
            _getTwoFactorCodeFunc = getTwoFactorCodeFunc;
        }

        public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            return await _getTwoFactorCodeFunc();
        }

        public async Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            return await _getTwoFactorCodeFunc();
        }

        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            return Task.FromResult(true);
        }
    }
}