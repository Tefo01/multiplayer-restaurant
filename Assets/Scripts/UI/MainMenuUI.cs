using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MultiplayerRestaurant
{
    public class MainMenuUI : MonoBehaviour
    {
        [SerializeField] private TMP_InputField playerNameInput;
        [SerializeField] private TMP_InputField lobbyNameInput;
        [SerializeField] private TMP_InputField maxPlayersInput;
        [SerializeField] private TMP_InputField joinCodeInput;
        [SerializeField] private Button createButton;
        [SerializeField] private Button joinCodeButton;
        [SerializeField] private Button refreshButton;
        [SerializeField] private Transform lobbyListContent;
        [SerializeField] private GameObject lobbyListRowPrefab;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private GameObject busyOverlay;

        private void Start()
        {
            if (LobbyManager.Instance == null)
            {
                SetStatus("Lobby system not found in scene.");
                return;
            }

            if (playerNameInput != null)
            {
                playerNameInput.text = LobbyManager.Instance.PlayerName;
                playerNameInput.onEndEdit.AddListener(HandleNameChanged);
            }

            createButton.onClick.AddListener(HandleCreateClicked);
            joinCodeButton.onClick.AddListener(HandleJoinCodeClicked);
            refreshButton.onClick.AddListener(HandleRefreshClicked);

            LobbyManager.Instance.OnError += SetStatus;
            LobbyManager.Instance.OnLobbyStateChanged += RefreshLobbyListDisplay;
            LobbyManager.Instance.OnSignedIn += HandleSignedIn;

            SetBusy(false);
            SetStatus(LobbyManager.Instance.IsSignedIn ? "Ready." : "Connecting to online services...");

            _ = LobbyManager.Instance.RefreshLobbyListAsync();
        }

        private void OnDestroy()
        {
            if (LobbyManager.Instance == null) return;
            LobbyManager.Instance.OnError -= SetStatus;
            LobbyManager.Instance.OnLobbyStateChanged -= RefreshLobbyListDisplay;
            LobbyManager.Instance.OnSignedIn -= HandleSignedIn;
        }

        private void HandleSignedIn()
        {
            SetStatus("Ready.");
        }

        private void HandleNameChanged(string value)
        {
            LobbyManager.Instance.SetPlayerName(value);
        }

        private async void HandleCreateClicked()
        {
            SetBusy(true);
            var maxPlayers = 4;
            if (maxPlayersInput != null) int.TryParse(maxPlayersInput.text, out maxPlayers);
            if (maxPlayers <= 0) maxPlayers = 4;

            var lobbyName = lobbyNameInput != null ? lobbyNameInput.text : string.Empty;
            var success = await LobbyManager.Instance.CreateLobbyAsync(lobbyName, maxPlayers);
            if (!success) SetBusy(false);
        }

        private async void HandleJoinCodeClicked()
        {
            if (joinCodeInput == null || string.IsNullOrWhiteSpace(joinCodeInput.text))
            {
                SetStatus("Enter a join code first.");
                return;
            }

            SetBusy(true);
            var success = await LobbyManager.Instance.JoinByCodeAsync(joinCodeInput.text);
            if (!success) SetBusy(false);
        }

        private async void HandleRefreshClicked()
        {
            refreshButton.interactable = false;
            await LobbyManager.Instance.RefreshLobbyListAsync();
            refreshButton.interactable = true;
        }

        private async void HandleJoinFromList(string sessionId)
        {
            SetBusy(true);
            var success = await LobbyManager.Instance.JoinByIdAsync(sessionId);
            if (!success) SetBusy(false);
        }

        private void RefreshLobbyListDisplay()
        {
            if (lobbyListContent == null || lobbyListRowPrefab == null) return;

            foreach (Transform child in lobbyListContent)
                Destroy(child.gameObject);

            foreach (var info in LobbyManager.Instance.AvailableLobbies)
            {
                var row = Instantiate(lobbyListRowPrefab, lobbyListContent);
                var rowComponent = row.GetComponent<LobbyListRow>();
                var occupied = info.MaxPlayers - info.AvailableSlots;
                rowComponent.SetData(info.Id, info.Name, occupied, info.MaxPlayers, HandleJoinFromList);
            }
        }

        private void SetStatus(string message)
        {
            if (statusText != null) statusText.text = message;
        }

        private void SetBusy(bool busy)
        {
            if (busyOverlay != null) busyOverlay.SetActive(busy);
            createButton.interactable = !busy;
            joinCodeButton.interactable = !busy;
            refreshButton.interactable = !busy;
        }
    }
}
