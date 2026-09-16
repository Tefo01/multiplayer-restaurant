using TMPro;
using UnityEngine;

namespace MultiplayerRestaurant
{
    public class LobbyPlayerRow : MonoBehaviour
    {
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private TMP_Text readyText;

        public void SetData(string playerName, bool isHost, bool isReady)
        {
            if (nameText != null)
                nameText.text = isHost ? $"{playerName} (Host)" : playerName;

            if (readyText != null)
            {
                readyText.text = isReady ? "Ready" : "Not Ready";
                readyText.color = isReady ? new Color(0.35f, 0.85f, 0.35f) : new Color(0.85f, 0.4f, 0.4f);
            }
        }
    }
}
