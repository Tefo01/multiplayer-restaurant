using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;

namespace MultiplayerRestaurant
{
    // Minimal networked player movement used as the gameplay placeholder once the
    // lobby transitions into the game scene. Replace with restaurant-specific logic later.
    [RequireComponent(typeof(NetworkObject))]
    public class NetworkPlayerController : NetworkBehaviour
    {
        [SerializeField] private float moveSpeed = 6f;
        [SerializeField] private TextMeshPro nameLabel;

        private readonly NetworkVariable<FixedString32Bytes> _displayName = new NetworkVariable<FixedString32Bytes>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public override void OnNetworkSpawn()
        {
            _displayName.OnValueChanged += HandleNameChanged;
            UpdateLabel(_displayName.Value.ToString());

            if (IsOwner)
            {
                var localName = LobbyManager.Instance != null ? LobbyManager.Instance.PlayerName : "Player";
                _displayName.Value = localName;
            }
        }

        public override void OnNetworkDespawn()
        {
            _displayName.OnValueChanged -= HandleNameChanged;
        }

        private void HandleNameChanged(FixedString32Bytes previous, FixedString32Bytes current)
        {
            UpdateLabel(current.ToString());
        }

        private void UpdateLabel(string value)
        {
            if (nameLabel != null)
                nameLabel.text = string.IsNullOrEmpty(value) ? "Player" : value;
        }

        private void Update()
        {
            if (!IsOwner) return;

            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            float horizontal = 0f;
            float vertical = 0f;
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) horizontal -= 1f;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) horizontal += 1f;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) vertical -= 1f;
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) vertical += 1f;

            var movement = new Vector3(horizontal, 0f, vertical);
            if (movement.sqrMagnitude > 1f) movement.Normalize();

            if (movement.sqrMagnitude > 0.0001f)
            {
                transform.position += movement * moveSpeed * Time.deltaTime;
                transform.rotation = Quaternion.LookRotation(movement, Vector3.up);
            }
        }
    }
}
