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

        [Header("Kitchen Prop Push")]
        // CharacterController has infinite mass as far as PhysX is concerned - Move() collides
        // with and stops against a dynamic Rigidbody but never imparts force onto it. Pushing
        // kitchen props requires this explicit OnControllerColliderHit handling; there is no
        // passive/free push from CharacterController-vs-Rigidbody overlap alone (confirmed by a
        // host-only test: 200 Move() steps straight into a prop produced zero displacement until
        // this was added). Sets velocity directly rather than AddForce(Impulse) - an impulse
        // fires on every single contacted hit (i.e. every frame of sustained contact), and since
        // impulses accumulate, that blew a test prop from (2,-2.5,2) to velocity ~47 m/s and off
        // into the void within a couple hundred ms of contact. Direct velocity assignment is
        // idempotent per-frame instead, matching Unity's own documented CharacterController push
        // pattern.
        [SerializeField] private float propPushSpeed = 3f;

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

        // Fires synchronously from within _controller.Move() above, so it's already implicitly
        // gated the same way HandleMove is (owner + Gameplay scene) - only the locally-owned
        // player's own Move() calls ever land here.
        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            var body = hit.collider.attachedRigidbody;
            if (body == null) return;

            // Ignore hits from standing/landing on top of a prop, so only sideways contact pushes it.
            if (hit.moveDirection.y < -0.3f) return;

            var pushDirection = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z);
            if (pushDirection.sqrMagnitude < 0.0001f) return;
            pushDirection.Normalize();

            if (IsServer)
            {
                // Host simulates props directly - drive the authoritative Rigidbody's velocity now.
                body.linearVelocity = pushDirection * propPushSpeed;
            }
            else
            {
                // Non-host: our local copy of the prop is a kinematic NetworkRigidbody follower
                // (no authority here), so pushing it locally would do nothing real. Send push
                // intent to the host, which owns the actual simulation.
                var propNetworkObject = body.GetComponent<NetworkObject>();
                if (propNetworkObject != null)
                    RequestPropPushServerRpc(propNetworkObject.NetworkObjectId, pushDirection);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestPropPushServerRpc(ulong propNetworkObjectId, Vector3 direction)
        {
            if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(propNetworkObjectId, out var propNetworkObject))
                return;

            var body = propNetworkObject.GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic)
                body.linearVelocity = direction * propPushSpeed;
        }
    }
}
