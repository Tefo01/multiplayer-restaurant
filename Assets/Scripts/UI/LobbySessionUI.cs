using TMPro;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.UI;

namespace MultiplayerRestaurant
{
    public class LobbySessionUI : MonoBehaviour
    {
        [SerializeField] private Transform playerListContent;
        [SerializeField] private GameObject playerRowPrefab;
        [SerializeField] private Toggle readyToggle;
        [SerializeField] private Button startButton;
        [SerializeField] private Button leaveButton;
        [SerializeField] private TMP_Text lobbyNameText;
        [SerializeField] private TMP_Text joinCodeText;
        [SerializeField] private TMP_Text statusText;

        private void Start()
        {
            // Always wire the buttons, regardless of whether the session is attached yet:
            // a joining client's Netcode scene sync can land them here before LobbyManager
            // finishes AttachSession (the host never hits this race since it only loads this
            // scene after AttachSession has already run).
            leaveButton.onClick.AddListener(HandleLeaveClicked);
            readyToggle.onValueChanged.AddListener(HandleReadyToggled);
            startButton.onClick.AddListener(HandleStartClicked);

            if (LobbyManager.Instance == null)
            {
                SetStatus("Lobby system not found.");
                return;
            }

            LobbyManager.Instance.OnLobbyStateChanged += Refresh;
            LobbyManager.Instance.OnError += SetStatus;
            LobbyManager.Instance.OnSessionJoined += Initialize;

            if (LobbyManager.Instance.CurrentSession != null)
                Initialize();
            else
                SetStatus("Connecting to lobby...");
        }

        private void OnDestroy()
        {
            if (LobbyManager.Instance == null) return;
            LobbyManager.Instance.OnLobbyStateChanged -= Refresh;
            LobbyManager.Instance.OnError -= SetStatus;
            LobbyManager.Instance.OnSessionJoined -= Initialize;
        }

        private void Initialize()
        {
            var session = LobbyManager.Instance.CurrentSession;
            if (session == null) return;

            if (lobbyNameText != null) lobbyNameText.text = session.Name;
            SetStatus(string.Empty);

            Refresh();
        }

        private async void HandleLeaveClicked()
        {
            leaveButton.interactable = false;
            await LobbyManager.Instance.LeaveLobbyAsync();
        }

        private async void HandleReadyToggled(bool value)
        {
            await LobbyManager.Instance.SetReadyAsync(value);
        }

        private void HandleStartClicked()
        {
            if (!LobbyManager.Instance.AllPlayersReady())
            {
                SetStatus("Everyone needs to be ready first.");
                return;
            }

            LobbyManager.Instance.StartGame();
        }

        private void Refresh()
        {
            var session = LobbyManager.Instance.CurrentSession;
            if (session == null) return;

            var isHost = LobbyManager.Instance.IsHost;

            // Re-evaluated every refresh (not just once) so a host migration - the session
            // backend electing a new host after the old one leaves - correctly grants or
            // revokes this player's Start Game button and join code as it happens.
            if (joinCodeText != null) joinCodeText.text = isHost ? $"Code: {session.Code}" : string.Empty;
            startButton.gameObject.SetActive(isHost);

            foreach (Transform child in playerListContent)
                Destroy(child.gameObject);

            foreach (var player in session.Players)
            {
                var row = Instantiate(playerRowPrefab, playerListContent);
                var rowComponent = row.GetComponent<LobbyPlayerRow>();
                var displayName = player.GetPlayerName() ?? $"Player {player.Id.Substring(0, 4)}";
                var isRowHost = player.Id == session.Host;
                var isReady = LobbyManager.Instance.IsPlayerReady(player);
                rowComponent.SetData(displayName, isRowHost, isReady);
            }

            if (isHost)
                startButton.interactable = LobbyManager.Instance.AllPlayersReady();
        }

        private void SetStatus(string message)
        {
            if (statusText != null) statusText.text = message;
        }
    }
}
