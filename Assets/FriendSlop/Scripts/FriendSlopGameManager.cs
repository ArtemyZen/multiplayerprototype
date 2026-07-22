using UnityEngine;
using Fusion;

namespace FriendSlop
{
    public sealed class FriendSlopGameManager : NetworkBehaviour
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
                return;
            }

            LocalPlayer = Runner.Spawn(
                PlayerPrefab,
                GetSpawnPosition(),
                GetSpawnRotation(),
                Runner.LocalPlayer,
                flags: NetworkSpawnFlags.SharedModeStateAuthLocalPlayer);
            Runner.SetPlayerObject(Runner.LocalPlayer, LocalPlayer.Object);
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
