using Unity.Netcode;
using UnityEngine;

namespace MultiplayerRestaurant
{
    // Host-authoritative kitchen prop: a real Rigidbody that any player can push (see
    // NetworkPlayerController.OnControllerColliderHit) and, while held, gets pulled toward its
    // holder's camera by a spring the host simulates here.
    //
    // CurrentHolderClientId is the single source of truth for "who's holding this" - not
    // duplicated on the player. Grab/release only ever change it through these ServerRpcs, so
    // the host is the only writer and there's no ownership conflict to resolve.
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(NetworkObject))]
    public class KitchenPhysicsObject : NetworkBehaviour
    {
        public const ulong NoHolder = ulong.MaxValue;

        [Header("Hold Spring (host-authoritative)")]
        // Kept per-prop and inspector-tunable rather than a global constant, since the intended
        // feel is that heavier/tuned-down props visibly lag, drag, or can't be lifted at all -
        // needs numeric playtesting per prop, not a single project-wide constant.
        //
        // IMPORTANT when tuning: maxHoldForce must exceed Rigidbody.mass * Physics.gravity.magnitude
        // (~9.81 * mass) or the spring can NEVER win against gravity - the prop just sits on the
        // floor forever (found via host-only testing: maxHoldForce=40 against a mass=5 prop, whose
        // weight is 49.05N, meant the crate couldn't lift off the ground at all - only crept
        // sideways fighting floor friction). Give real headroom above that threshold, not just
        // barely over it, so there's still lift force left over for the spring to actually move
        // the prop rather than merely cancel gravity.
        [SerializeField] private float holdStiffness = 150f;
        [SerializeField] private float holdDamping = 12f;
        [SerializeField] private float maxHoldForce = 90f;

        private readonly NetworkVariable<ulong> _currentHolderClientId = new NetworkVariable<ulong>(
            NoHolder,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public bool IsHeld => _currentHolderClientId.Value != NoHolder;
        public ulong CurrentHolderClientId => _currentHolderClientId.Value;
        public Rigidbody Rigidbody { get; private set; }

        private void Awake()
        {
            Rigidbody = GetComponent<Rigidbody>();
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestGrabServerRpc(ServerRpcParams rpcParams = default)
        {
            if (IsHeld) return;
            _currentHolderClientId.Value = rpcParams.Receive.SenderClientId;
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestReleaseServerRpc(ServerRpcParams rpcParams = default)
        {
            // Only the current holder can release it - a stale/late release call from someone
            // who lost the race to grab it first must not steal control away from the winner.
            if (_currentHolderClientId.Value == rpcParams.Receive.SenderClientId)
                _currentHolderClientId.Value = NoHolder;
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer)
                _currentHolderClientId.Value = NoHolder;
        }

        private void FixedUpdate()
        {
            if (!IsServer || !IsHeld) return;

            if (!NetworkManager.ConnectedClients.TryGetValue(_currentHolderClientId.Value, out var holderClient)
                || holderClient.PlayerObject == null)
            {
                // Holder disconnected or has no player object (e.g. mid-reconnect) - drop it
                // rather than leave the spring pulling toward a stale/missing target forever.
                _currentHolderClientId.Value = NoHolder;
                return;
            }

            var holderController = holderClient.PlayerObject.GetComponent<NetworkPlayerController>();
            if (holderController == null) return;

            var toTarget = holderController.DesiredHoldPoint.Value - Rigidbody.position;
            var force = holdStiffness * toTarget - holdDamping * Rigidbody.linearVelocity;
            force = Vector3.ClampMagnitude(force, maxHoldForce);
            Rigidbody.AddForce(force, ForceMode.Force);

            // Throttled (~twice/sec) so the console stays readable. If "force" is small/zero
            // despite a large "toTargetDist", that's a hold-logic problem (spring not pulling).
            // If force stays pinned at maxHoldForce but "pos" never moves toward "target", the
            // joint/collision is physically resisting it, not the hold logic.
            if (Time.frameCount % 30 == 0)
            {
                Debug.Log($"[HoldDebug] {name} pos={Rigidbody.position} target={holderController.DesiredHoldPoint.Value} " +
                          $"toTargetDist={toTarget.magnitude:F3} force={force} (mag={force.magnitude:F1}) vel={Rigidbody.linearVelocity} (mag={Rigidbody.linearVelocity.magnitude:F3})");
            }
        }
    }
}
