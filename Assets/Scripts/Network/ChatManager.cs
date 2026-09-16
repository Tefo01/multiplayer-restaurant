using System;
using Unity.Netcode;
using UnityEngine;

namespace MultiplayerRestaurant
{
    // A single server-spawned NetworkObject that survives scene changes (Lobby -> Gameplay)
    // so lobby chat and in-game chat share the same channel.
    //aaaaaaaaaaaaaaaaaaaaaaaa
    public class ChatManager : NetworkBehaviour
    {
        public static ChatManager Instance { get; private set; }

        public event Action<string, string> OnMessageReceived; // senderName, message

        public static void SpawnPersistent(GameObject prefab)
        {
            if (Instance != null || prefab == null) return;
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

            var instance = Instantiate(prefab);
            var networkObject = instance.GetComponent<NetworkObject>();
            networkObject.DestroyWithScene = false;
            networkObject.Spawn();
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        public void SendChatMessage(string senderName, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            SendChatMessageServerRpc(senderName, message.Trim());
        }

        [ServerRpc(RequireOwnership = false)]
        private void SendChatMessageServerRpc(string senderName, string message, ServerRpcParams rpcParams = default)
        {
            ReceiveChatMessageClientRpc(senderName, message);
        }

        public void BroadcastSystemMessage(string message)
        {
            if (!IsServer || string.IsNullOrWhiteSpace(message)) return;
            ReceiveChatMessageClientRpc("System", message);
        }

        [ClientRpc]
        private void ReceiveChatMessageClientRpc(string senderName, string message)
        {
            OnMessageReceived?.Invoke(senderName, message);
        }
    }
}
