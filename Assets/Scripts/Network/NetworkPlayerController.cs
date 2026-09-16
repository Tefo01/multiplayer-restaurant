using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;

namespace MultiplayerRestaurant
{
    // Networked first-person player: owner-driven CharacterController movement + mouse look,
    // replicated to other clients via the owner-authoritative NetworkTransform on this prefab.
    //
    // This NetworkObject spawns as soon as the owning client connects to the session - which
    // happens well before the Lobby or Gameplay scene loads (LobbyManager attaches the session,
    // then explicitly loads Lobby afterwards). So none of the first-person presentation (camera,
    // cursor lock) or movement/gravity can be tied to OnNetworkSpawn: that fired while the player
    // was still looking at lobby UI, which both locked their cursor prematurely and let gravity
    // free-fall the CharacterController through scenes with no floor before Gameplay ever loaded.
    // Everything owner-specific is instead gated on the object actually sitting in the Gameplay
    // scene (checked every frame, since Netcode migrates this persistent object between scenes
    // rather than respawning it).
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(CharacterController))]
    public class NetworkPlayerController : NetworkBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float moveSpeed = 6f;
        [SerializeField] private float jumpHeight = 1.2f;
        [SerializeField] private float gravity = -20f;

        [Header("Look")]
        [SerializeField] private float mouseSensitivity = 0.15f;
        [SerializeField] private float pitchClamp = 80f;
        [SerializeField] private Transform cameraPivot;
        [SerializeField] private Camera playerCamera;
        [SerializeField] private AudioListener audioListener;

        [Header("Visuals")]
        [SerializeField] private TextMeshPro nameLabel;
        [SerializeField] private Renderer modelRenderer;

        private readonly NetworkVariable<FixedString32Bytes> _displayName = new NetworkVariable<FixedString32Bytes>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        private CharacterController _controller;
        private float _pitch;
        private float _verticalVelocity;
        private bool _cursorLocked;
        private bool _gameplayPresentationApplied;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
        }

        public override void OnNetworkSpawn()
        {
            _displayName.OnValueChanged += HandleNameChanged;
            UpdateLabel(_displayName.Value.ToString());

            if (IsOwner)
            {
                var localName = LobbyManager.Instance != null ? LobbyManager.Instance.PlayerName : "Player";
                _displayName.Value = localName;
            }
            else
            {
                if (playerCamera != null) playerCamera.enabled = false;
                if (audioListener != null) audioListener.enabled = false;
            }
        }

        public override void OnNetworkDespawn()
        {
            _displayName.OnValueChanged -= HandleNameChanged;

            if (IsOwner && _gameplayPresentationApplied)
                SetCursorLocked(false);
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

        private static void DisableSceneCameras()
        {
            foreach (var cam in GameObject.FindGameObjectsWithTag("MainCamera"))
                cam.SetActive(false);
        }

        private void Update()
        {
            if (!IsOwner) return;

            bool inGameplay = gameObject.scene.name == LobbyManager.GameplaySceneName;

            if (inGameplay && !_gameplayPresentationApplied)
                EnterGameplayPresentation();
            else if (!inGameplay && _gameplayPresentationApplied)
                ExitGameplayPresentation();

            if (!inGameplay) return;

            HandleCursorToggle();
            HandleLook();
            HandleMove();
        }

        private void EnterGameplayPresentation()
        {
            _gameplayPresentationApplied = true;

            // Netcode's default player spawn lands exactly on the floor (zero gap), which is
            // an awkward starting contact for ground physics. A small lift lets gravity settle
            // it into a normal resting contact on the first frame instead.
            _controller.enabled = false;
            transform.position += Vector3.up * 0.1f;
            _controller.enabled = true;
            _verticalVelocity = 0f;

            DisableSceneCameras();

            if (playerCamera != null) playerCamera.enabled = true;
            if (audioListener != null) audioListener.enabled = true;
            if (modelRenderer != null) modelRenderer.enabled = false;
            if (nameLabel != null) nameLabel.gameObject.SetActive(false);

            SetCursorLocked(true);
        }

        private void ExitGameplayPresentation()
        {
            _gameplayPresentationApplied = false;

            if (playerCamera != null) playerCamera.enabled = false;
            if (audioListener != null) audioListener.enabled = false;
            if (modelRenderer != null) modelRenderer.enabled = true;
            if (nameLabel != null) nameLabel.gameObject.SetActive(true);

            SetCursorLocked(false);
        }

        private void HandleCursorToggle()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                SetCursorLocked(!_cursorLocked);
        }

        private void SetCursorLocked(bool locked)
        {
            _cursorLocked = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private void HandleLook()
        {
            var mouse = Mouse.current;
            if (mouse == null || !_cursorLocked) return;

            var delta = mouse.delta.ReadValue();
            float yaw = delta.x * mouseSensitivity;
            _pitch = Mathf.Clamp(_pitch - delta.y * mouseSensitivity, -pitchClamp, pitchClamp);

            transform.Rotate(Vector3.up, yaw);
            if (cameraPivot != null)
                cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        private void HandleMove()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            float horizontal = 0f;
            float vertical = 0f;
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) horizontal -= 1f;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) horizontal += 1f;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) vertical -= 1f;
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) vertical += 1f;

            var input = new Vector3(horizontal, 0f, vertical);
            if (input.sqrMagnitude > 1f) input.Normalize();

            var move = (transform.right * input.x + transform.forward * input.z) * moveSpeed;

            if (_controller.isGrounded)
            {
                _verticalVelocity = -2f;
                if (keyboard.spaceKey.wasPressedThisFrame)
                    _verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
            else
            {
                _verticalVelocity += gravity * Time.deltaTime;
            }

            move.y = _verticalVelocity;
            _controller.Move(move * Time.deltaTime);
        }
    }
}
