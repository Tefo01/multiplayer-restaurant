using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MultiplayerRestaurant
{
    // Works in both the Lobby and Gameplay scenes since ChatManager persists across the load.
    public class ChatUI : MonoBehaviour
    {
        [SerializeField] private TMP_InputField inputField;
        [SerializeField] private Button sendButton;
        [SerializeField] private TMP_Text logText;
        [SerializeField] private ScrollRect scrollRect;

        private bool _bound;

        private void Start()
        {
            sendButton.onClick.AddListener(Send);
            inputField.onSubmit.AddListener(_ => Send());
            TryBind();
        }

        private void Update()
        {
            // ChatManager is spawned by the host slightly after the session is created,
            // and only replicates to a client once its connection finishes, so poll briefly.
            if (!_bound) TryBind();
        }

        private void OnDestroy()
        {
            if (ChatManager.Instance != null)
                ChatManager.Instance.OnMessageReceived -= HandleMessage;
        }

        private void TryBind()
        {
            if (_bound || ChatManager.Instance == null) return;
            ChatManager.Instance.OnMessageReceived += HandleMessage;
            _bound = true;
        }

        private void Send()
        {
            if (ChatManager.Instance == null) return;
            var text = inputField.text;
            if (string.IsNullOrWhiteSpace(text)) return;

            var senderName = LobbyManager.Instance != null ? LobbyManager.Instance.PlayerName : "Player";
            ChatManager.Instance.SendChatMessage(senderName, text);
            inputField.text = string.Empty;
            inputField.ActivateInputField();
        }

        private void HandleMessage(string senderName, string message)
        {
            logText.text += $"\n<b>{senderName}:</b> {message}";
            Canvas.ForceUpdateCanvases();
            if (scrollRect != null) scrollRect.verticalNormalizedPosition = 0f;
        }
    }
}
