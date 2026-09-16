using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MultiplayerRestaurant
{
    public class LobbyListRow : MonoBehaviour
    {
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private TMP_Text playersText;
        [SerializeField] private Button joinButton;

        private string _sessionId;
        private Action<string> _onJoin;

        public void SetData(string sessionId, string sessionName, int occupied, int maxPlayers, Action<string> onJoin)
        {
            _sessionId = sessionId;
            _onJoin = onJoin;

            if (nameText != null) nameText.text = sessionName;
            if (playersText != null) playersText.text = $"{occupied}/{maxPlayers}";

            if (joinButton != null)
            {
                joinButton.onClick.RemoveAllListeners();
                joinButton.onClick.AddListener(() => _onJoin?.Invoke(_sessionId));
            }
        }
    }
}
