using UnityEngine;
using UnityEngine.UI;

namespace MultiplayerRestaurant
{
    public class GameplayUI : MonoBehaviour
    {
        [SerializeField] private Button leaveButton;

        private void Start()
        {
            leaveButton.onClick.AddListener(HandleLeaveClicked);
        }

        private async void HandleLeaveClicked()
        {
            leaveButton.interactable = false;
            if (LobbyManager.Instance != null)
                await LobbyManager.Instance.LeaveLobbyAsync();
        }
    }
}
