using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MultiplayerRestaurant
{
    // Persistent, scene-independent wrapper around a Unity Multiplayer Services session
    // (Relay-backed, so players on different networks can connect) plus the Netcode
    // for GameObjects connection it brings up automatically.
    public class LobbyManager : MonoBehaviour
    {
        public static LobbyManager Instance { get; private set; }

        public const string MainMenuSceneName = "MainMenu";
        public const string LobbySceneName = "Lobby";
        public const string GameplaySceneName = "Gameplay";

        private const string LastSessionIdKey = "MPR_LastSessionId";
        private const string PlayerNameKey = "MPR_PlayerName";
        private const string ReadyPropertyKey = "ready";

        [SerializeField] private GameObject chatManagerPrefab;

        public ISession CurrentSession { get; private set; }
        public bool IsHost => CurrentSession != null && CurrentSession.IsHost;
        public bool IsSignedIn { get; private set; }
        public string PlayerName { get; private set; } = "Player";
        public IReadOnlyList<ISessionInfo> AvailableLobbies { get; private set; } = new List<ISessionInfo>();
        public string LastErrorMessage { get; private set; }

        public event Action OnSignedIn;
        public event Action OnSessionJoined;
        public event Action OnSessionLeft;
        public event Action OnLobbyStateChanged;
        public event Action<string> OnError;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            PlayerName = PlayerPrefs.GetString(PlayerNameKey, "Chef" + UnityEngine.Random.Range(1000, 9999));
        }

        private async void Start()
        {
            try
            {
                await UnityServices.InitializeAsync();
                AuthenticationService.Instance.SignedIn += HandleSignedIn;

                if (AuthenticationService.Instance.IsSignedIn)
                {
                    HandleSignedIn();
                }
                else
                {
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                RaiseError("Failed to connect to online services: " + e.Message);
            }
        }

        private async void HandleSignedIn()
        {
            if (IsSignedIn) return;
            IsSignedIn = true;

            try
            {
                await AuthenticationService.Instance.UpdatePlayerNameAsync(PlayerName);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            OnSignedIn?.Invoke();
            await TryAutoReconnectAsync();

            // No saved session to rejoin: land on the main menu like a fresh start.
            if (CurrentSession == null)
            {
                SceneManager.LoadScene(MainMenuSceneName);
            }
        }

        public void SetPlayerName(string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return;
            PlayerName = newName.Trim();
            PlayerPrefs.SetString(PlayerNameKey, PlayerName);
            if (IsSignedIn)
            {
                _ = AuthenticationService.Instance.UpdatePlayerNameAsync(PlayerName);
            }
        }

        public async Task<bool> CreateLobbyAsync(string lobbyName, int maxPlayers)
        {
            try
            {
                // Plain UDP relay: this machine's Burst toolchain can't compile the DTLS
                // send-callback function pointer, so encrypted relay (the SDK's default) fails.
                var networkOptions = new NetworkOptions { RelayProtocol = RelayProtocol.UDP };
                var options = new SessionOptions
                {
                    Name = string.IsNullOrWhiteSpace(lobbyName) ? $"{PlayerName}'s Restaurant" : lobbyName.Trim(),
                    MaxPlayers = Mathf.Clamp(maxPlayers, 2, 16)
                }.WithRelayNetwork().WithNetworkOptions(networkOptions).WithPlayerName(VisibilityPropertyOptions.Member);

                var session = await MultiplayerService.Instance.CreateSessionAsync(options);
                AttachSession(session);

                // The host explicitly moves into the Lobby scene; NGO's scene management
                // then auto-syncs any client that joins afterwards to this same scene.
                NetworkManager.Singleton.SceneManager.LoadScene(LobbySceneName, LoadSceneMode.Single);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                RaiseError("Could not create lobby: " + e.Message);
                return false;
            }
        }

        public async Task<bool> JoinByCodeAsync(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            try
            {
                var options = new JoinSessionOptions().WithPlayerName(VisibilityPropertyOptions.Member);
                var session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code.Trim().ToUpperInvariant(), options);
                AttachSession(session);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                RaiseError("Could not join lobby: " + e.Message);
                return false;
            }
        }

        public async Task<bool> JoinByIdAsync(string sessionId)
        {
            try
            {
                var options = new JoinSessionOptions().WithPlayerName(VisibilityPropertyOptions.Member);
                var session = await MultiplayerService.Instance.JoinSessionByIdAsync(sessionId, options);
                AttachSession(session);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                RaiseError("Could not join lobby: " + e.Message);
                return false;
            }
        }

        public async Task RefreshLobbyListAsync()
        {
            try
            {
                var results = await MultiplayerService.Instance.QuerySessionsAsync(new QuerySessionsOptions());
                AvailableLobbies = results.Sessions.ToList();
                OnLobbyStateChanged?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                RaiseError("Could not fetch lobby list: " + e.Message);
            }
        }

        public async Task LeaveLobbyAsync()
        {
            if (CurrentSession == null) return;
            try
            {
                await CurrentSession.LeaveAsync();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                DetachSession();
                await ShutdownNetworkAndWaitAsync();
                SceneManager.LoadScene(MainMenuSceneName);
            }
        }

        public async Task SetReadyAsync(bool ready)
        {
            if (CurrentSession == null) return;
            try
            {
                var properties = new Dictionary<string, PlayerProperty>
                {
                    { ReadyPropertyKey, new PlayerProperty(ready ? "1" : "0", VisibilityPropertyOptions.Member) }
                };
                CurrentSession.CurrentPlayer.SetProperties(properties);
                await CurrentSession.SaveCurrentPlayerDataAsync();
            }
            catch (SessionException e)
            {
                Debug.LogException(e);
            }
        }

        public bool IsPlayerReady(IReadOnlyPlayer player)
        {
            if (player?.Properties != null && player.Properties.TryGetValue(ReadyPropertyKey, out var prop))
                return prop.Value == "1";
            return false;
        }

        public bool AllPlayersReady()
        {
            if (CurrentSession == null || CurrentSession.PlayerCount == 0) return false;
            return CurrentSession.Players.All(IsPlayerReady);
        }

        public void StartGame()
        {
            if (!IsHost || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

            // Scene transition is purely local (Netcode's own scene sync moves every
            // connected client) - no backend round-trip needed, so this can't hang or time out.
            NetworkManager.Singleton.SceneManager.LoadScene(GameplaySceneName, LoadSceneMode.Single);
        }

        private void AttachSession(ISession session)
        {
            CurrentSession = session;
            PlayerPrefs.SetString(LastSessionIdKey, session.Id);

            session.PlayerJoined += HandlePlayerJoined;
            session.PlayerHasLeft += HandlePlayerLeft;
            session.PlayerPropertiesChanged += HandlePlayerPropertiesChanged;
            session.RemovedFromSession += HandleRemovedFromSession;
            session.SessionHostChanged += HandleSessionHostChanged;

            if (session.IsHost && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && chatManagerPrefab != null)
            {
                ChatManager.SpawnPersistent(chatManagerPrefab);
            }

            OnSessionJoined?.Invoke();
        }

        private void DetachSession()
        {
            if (CurrentSession != null)
            {
                CurrentSession.PlayerJoined -= HandlePlayerJoined;
                CurrentSession.PlayerHasLeft -= HandlePlayerLeft;
                CurrentSession.PlayerPropertiesChanged -= HandlePlayerPropertiesChanged;
                CurrentSession.RemovedFromSession -= HandleRemovedFromSession;
                CurrentSession.SessionHostChanged -= HandleSessionHostChanged;
            }

            CurrentSession = null;
            PlayerPrefs.DeleteKey(LastSessionIdKey);
            OnSessionLeft?.Invoke();
        }

        private async Task ShutdownNetworkAndWaitAsync()
        {
            // Never let a transport/shutdown hiccup block the scene navigation that follows this.
            try
            {
                if (NetworkManager.Singleton != null && (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsClient))
                {
                    NetworkManager.Singleton.Shutdown();

                    // Shutdown finishes over subsequent frames (NGO's own update loop), not
                    // synchronously. Loading a new scene before it's done races NGO's deferred
                    // cleanup, which silently clobbers the scene load.
                    while (NetworkManager.Singleton != null && NetworkManager.Singleton.ShutdownInProgress)
                    {
                        await Task.Yield();
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void HandlePlayerJoined(string playerId) => OnLobbyStateChanged?.Invoke();
        private void HandlePlayerLeft(string playerId) => OnLobbyStateChanged?.Invoke();
        private void HandlePlayerPropertiesChanged() => OnLobbyStateChanged?.Invoke();
        private void HandleSessionHostChanged(string newHostId) => OnLobbyStateChanged?.Invoke();

        private async void HandleRemovedFromSession()
        {
            DetachSession();
            RaiseError("You were removed from the lobby.");
            await ShutdownNetworkAndWaitAsync();
            SceneManager.LoadScene(MainMenuSceneName);
        }

        private async Task TryAutoReconnectAsync()
        {
            var lastId = PlayerPrefs.GetString(LastSessionIdKey, string.Empty);
            if (string.IsNullOrEmpty(lastId)) return;

            try
            {
                var joinedIds = await MultiplayerService.Instance.GetJoinedSessionIdsAsync();
                if (!joinedIds.Contains(lastId))
                {
                    PlayerPrefs.DeleteKey(LastSessionIdKey);
                    return;
                }

                var session = await MultiplayerService.Instance.ReconnectToSessionAsync(lastId);
                AttachSession(session);
            }
            catch (SessionException)
            {
                PlayerPrefs.DeleteKey(LastSessionIdKey);
            }
        }

        private void RaiseError(string message)
        {
            LastErrorMessage = message;
            OnError?.Invoke(message);
        }
    }
}
