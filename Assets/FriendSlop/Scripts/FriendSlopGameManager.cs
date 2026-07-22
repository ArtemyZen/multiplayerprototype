using UnityEngine;
using Fusion;

namespace FriendSlop
{
    public sealed class FriendSlopGameManager : NetworkBehaviour, IPlayerJoined, IPlayerLeft
    {
        [Header("Player")]
        public FirstPersonNetworkPlayer PlayerPrefab;
        public float SpawnRadius = 4f;
        public Transform[] SpawnPoints;

        public FirstPersonNetworkPlayer LocalPlayer { get; private set; }

        public override void Spawned()
        {
            if (PlayerPrefab == null)
            {
                Debug.LogError("FriendSlopGameManager needs a PlayerPrefab.", this);
            }
        }

        public void RegisterLocalPlayer(FirstPersonNetworkPlayer player)
        {
            LocalPlayer = player;
        }

        public void PlayerJoined(PlayerRef player)
        {
            if (Runner.IsServer == false || PlayerPrefab == null)
                return;

            var playerObject = Runner.Spawn(PlayerPrefab, GetSpawnPosition(), GetSpawnRotation(), player);
            Runner.SetPlayerObject(player, playerObject.Object);

            if (player == Runner.LocalPlayer)
                LocalPlayer = playerObject;
        }

        public void PlayerLeft(PlayerRef player)
        {
            if (Runner.IsServer == false)
                return;

            var playerObject = Runner.GetPlayerObject(player);
            if (playerObject != null)
                Runner.Despawn(playerObject);
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            LocalPlayer = null;
        }

        private Vector3 GetSpawnPosition()
        {
            if (SpawnPoints != null && SpawnPoints.Length > 0)
            {
                var spawn = SpawnPoints[Random.Range(0, SpawnPoints.Length)];
                var offset = Random.insideUnitCircle * SpawnRadius;
                return spawn.position + new Vector3(offset.x, 0f, offset.y);
            }

            var fallbackOffset = Random.insideUnitCircle * SpawnRadius;
            return transform.position + new Vector3(fallbackOffset.x, 0f, fallbackOffset.y);
        }

        private Quaternion GetSpawnRotation()
        {
            if (SpawnPoints != null && SpawnPoints.Length > 0)
                return SpawnPoints[Random.Range(0, SpawnPoints.Length)].rotation;

            return transform.rotation;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;

            if (SpawnPoints != null && SpawnPoints.Length > 0)
            {
                foreach (var spawn in SpawnPoints)
                {
                    if (spawn != null)
                        Gizmos.DrawWireSphere(spawn.position, SpawnRadius);
                }
            }
            else
            {
                Gizmos.DrawWireSphere(transform.position, SpawnRadius);
            }
        }
    }
}
