using Fusion;
using Starter.Shooter;
using UnityEngine;

namespace FriendSlop
{
    public sealed class TestObjectCannon : MonoBehaviour
    {
        [Header("Input")]
        public KeyCode FireKey = KeyCode.G;
        public KeyCode NextPrefabKey = KeyCode.RightBracket;
        public KeyCode PreviousPrefabKey = KeyCode.LeftBracket;

        [Header("Auto Fire")]
        public bool AutoFireEnabled;
        public float AutoFireInterval = 5f;
        public bool AutoFireImmediately;

        [Header("Spawn")]
        public NetworkObject[] ObjectPrefabs;
        public int SelectedPrefabIndex;
        public Transform Muzzle;
        public bool FireFromLocalPlayerCamera = true;
        public float SpawnForwardOffset = 1.25f;

        [Header("Impulse")]
        public float LaunchSpeed = 14f;
        public float UpImpulse = 0.5f;
        public float RandomSpin = 6f;
        public bool ForceGravityOnSpawn = true;

        [Header("Network Guard")]
        public bool RequireStateAuthorityIfNetworked = true;

        private float _nextAutoFireTime;

        private void OnEnable()
        {
            ResetAutoFireTimer();
        }

        private void Update()
        {
            if (!CanUseThisCannon())
                return;

            if (Input.GetKeyDown(NextPrefabKey))
                SelectPrefab(SelectedPrefabIndex + 1);

            if (Input.GetKeyDown(PreviousPrefabKey))
                SelectPrefab(SelectedPrefabIndex - 1);

            if (Input.GetKeyDown(FireKey))
                Fire();

            UpdateAutoFire();
        }

        private bool CanUseThisCannon()
        {
            if (!RequireStateAuthorityIfNetworked)
                return true;

            var networkObject = GetComponent<NetworkObject>();
            return networkObject == null || networkObject.HasStateAuthority;
        }

        private void ResetAutoFireTimer()
        {
            _nextAutoFireTime = Time.time + (AutoFireImmediately ? 0f : Mathf.Max(0.01f, AutoFireInterval));
        }

        private void UpdateAutoFire()
        {
            if (!AutoFireEnabled)
                return;

            if (Time.time < _nextAutoFireTime)
                return;

            Fire();
            _nextAutoFireTime = Time.time + Mathf.Max(0.01f, AutoFireInterval);
        }

        private void SelectPrefab(int index)
        {
            if (ObjectPrefabs == null || ObjectPrefabs.Length == 0)
                return;

            SelectedPrefabIndex = (index % ObjectPrefabs.Length + ObjectPrefabs.Length) % ObjectPrefabs.Length;
            var selected = ObjectPrefabs[SelectedPrefabIndex];
            Debug.Log($"Test cannon selected prefab: {(selected != null ? selected.name : "<empty>")}", this);
        }

        public void Fire()
        {
            var runner = NetworkRunner.GetRunnerForScene(gameObject.scene);
            if (runner == null)
            {
                Debug.LogWarning("TestObjectCannon could not find a NetworkRunner in this scene.", this);
                return;
            }

            var prefab = GetSelectedPrefab();
            if (prefab == null)
            {
                Debug.LogWarning("TestObjectCannon has no selected object prefab.", this);
                return;
            }

            GetFirePose(runner, out var origin, out var forward);
            var spawnPosition = origin + forward * SpawnForwardOffset;
            var spawnRotation = Quaternion.LookRotation(forward, Vector3.up);
            var spawned = runner.Spawn(prefab, spawnPosition, spawnRotation);

            if (spawned == null)
            {
                Debug.LogWarning("TestObjectCannon failed to spawn object. Check Fusion Object Table and prefab NetworkObject.", this);
                return;
            }

            ApplyImpulse(spawned, forward);
        }

        private NetworkObject GetSelectedPrefab()
        {
            if (ObjectPrefabs == null || ObjectPrefabs.Length == 0)
                return null;

            SelectedPrefabIndex = Mathf.Clamp(SelectedPrefabIndex, 0, ObjectPrefabs.Length - 1);
            return ObjectPrefabs[SelectedPrefabIndex];
        }

        private void GetFirePose(NetworkRunner runner, out Vector3 origin, out Vector3 forward)
        {
            if (FireFromLocalPlayerCamera && runner.TryGetPlayerObject(runner.LocalPlayer, out var playerObject))
            {
                var player = playerObject.GetComponent<Player>();
                if (player != null && player.CameraHandle != null)
                {
                    origin = player.CameraHandle.position;
                    forward = player.CameraHandle.forward;
                    return;
                }
            }

            var fireTransform = Muzzle != null ? Muzzle : transform;
            origin = fireTransform.position;
            forward = fireTransform.forward;
        }

        private void ApplyImpulse(NetworkObject spawned, Vector3 forward)
        {
            var rigidbody = spawned.GetComponent<Rigidbody>();
            if (rigidbody == null)
                rigidbody = spawned.GetComponentInChildren<Rigidbody>();

            if (rigidbody == null)
            {
                Debug.LogWarning($"Spawned object {spawned.name} has no Rigidbody to launch.", spawned);
                return;
            }

            if (ForceGravityOnSpawn)
                rigidbody.useGravity = true;

            rigidbody.WakeUp();
            rigidbody.velocity = forward * LaunchSpeed + Vector3.up * UpImpulse;
            rigidbody.angularVelocity = Random.insideUnitSphere * RandomSpin;
        }

        private void OnDrawGizmosSelected()
        {
            var fireTransform = Muzzle != null ? Muzzle : transform;
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(fireTransform.position, fireTransform.forward * 2f);
            Gizmos.DrawWireSphere(fireTransform.position + fireTransform.forward * SpawnForwardOffset, 0.15f);
        }
    }
}
