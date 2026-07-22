using Fusion;
using System.Collections.Generic;
using UnityEngine;

namespace FriendSlop
{
    [RequireComponent(typeof(NetworkObject))]
    public sealed class CargoStabilizationZone : NetworkBehaviour
    {
        private struct LockedCargo
        {
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
        }

        [Header("Zone")]
        public bool Active = true;
        public bool OnlyWhenContainerHeld = true;
        public LayerMask CargoMask = ~0;
        public Vector3 LocalCenter = new Vector3(0f, 0.35f, 0f);
        public Vector3 LocalSize = new Vector3(0.5f, 0.5f, 0.5f);
        public int MaxCargoColliders = 32;

        [Header("Visible Cargo Lock")]
        public bool DisableCargoCollidersWhileLocked = true;
        public float ReleaseDelay = 2f;

        private readonly Dictionary<GrabbablePhysicsObject, LockedCargo> _lockedItems = new Dictionary<GrabbablePhysicsObject, LockedCargo>();
        private readonly HashSet<GrabbablePhysicsObject> _scannedItems = new HashSet<GrabbablePhysicsObject>();
        private readonly List<GrabbablePhysicsObject> _itemsToRelease = new List<GrabbablePhysicsObject>();
        private Collider[] _hits;
        private GrabbablePhysicsObject _container;
        private Vector3 _previousPosition;
        private bool _hasPreviousPose;
        private TickTimer _releaseTimer;

        public override void Spawned()
        {
            CacheReferences();
            StorePose();
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority)
                return;

            CacheReferences();

            var releaseVelocity = GetCarrierVelocity();
            var shouldLockCargo = Active && (!OnlyWhenContainerHeld || (_container != null && _container.IsCurrentlyHeld));
            if (!shouldLockCargo)
            {
                if (_lockedItems.Count > 0 && ReleaseDelay > 0f)
                {
                    if (!_releaseTimer.IsRunning)
                        _releaseTimer = TickTimer.CreateFromSeconds(Runner, ReleaseDelay);

                    if (!_releaseTimer.Expired(Runner))
                    {
                        UpdateLockedCargo(releaseVelocity);
                        StorePose();
                        return;
                    }
                }

                ReleaseAll(releaseVelocity);
                _releaseTimer = default;
                StorePose();
                return;
            }

            _releaseTimer = default;
            CaptureNewCargo();
            UpdateLockedCargo(releaseVelocity);
            StorePose();
        }

        public override void Render()
        {
            if (!HasStateAuthority || _lockedItems.Count == 0)
                return;

            foreach (var pair in _lockedItems)
            {
                if (pair.Key != null)
                    pair.Key.PresentLooseCargoLocally(transform, pair.Value.LocalPosition, pair.Value.LocalRotation);
            }
        }

        private void CaptureNewCargo()
        {
            EnsureBuffer();
            _scannedItems.Clear();

            var count = Physics.OverlapBoxNonAlloc(
                transform.TransformPoint(LocalCenter),
                LocalSize * 0.5f,
                _hits,
                transform.rotation,
                CargoMask,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                var item = _hits[i] != null ? _hits[i].GetComponentInParent<GrabbablePhysicsObject>() : null;
                if (item == null || item == _container || !_scannedItems.Add(item))
                    continue;

                if (item.IsPackedForCargo || item.IsCurrentlyHeld || item.IsLooseCargoLocked)
                    continue;

                _lockedItems[item] = new LockedCargo
                {
                    LocalPosition = transform.InverseTransformPoint(item.Position),
                    LocalRotation = Quaternion.Inverse(transform.rotation) * item.transform.rotation
                };
            }
        }

        private void UpdateLockedCargo(Vector3 releaseVelocity)
        {
            _itemsToRelease.Clear();

            foreach (var pair in _lockedItems)
            {
                var item = pair.Key;
                if (item == null || item.IsPackedForCargo || item.IsCurrentlyHeld)
                {
                    _itemsToRelease.Add(item);
                    continue;
                }

                item.LockAsLooseCargo(Object, pair.Value.LocalPosition, pair.Value.LocalRotation, DisableCargoCollidersWhileLocked);
            }

            for (int i = 0; i < _itemsToRelease.Count; i++)
            {
                var item = _itemsToRelease[i];
                if (item != null)
                    item.UnlockLooseCargo(releaseVelocity);

                _lockedItems.Remove(item);
            }
        }

        private void ReleaseAll(Vector3 releaseVelocity)
        {
            if (_lockedItems.Count == 0)
                return;

            _itemsToRelease.Clear();
            foreach (var pair in _lockedItems)
                _itemsToRelease.Add(pair.Key);

            for (int i = 0; i < _itemsToRelease.Count; i++)
            {
                var item = _itemsToRelease[i];
                if (item != null)
                    item.UnlockLooseCargo(releaseVelocity);
            }

            _lockedItems.Clear();
        }

        private Vector3 GetCarrierVelocity()
        {
            var deltaTime = Runner != null ? Runner.DeltaTime : Time.fixedDeltaTime;
            if (!_hasPreviousPose || deltaTime <= 0f)
                return Vector3.zero;

            return (transform.position - _previousPosition) / deltaTime;
        }

        private void CacheReferences()
        {
            if (_container == null)
                _container = GetComponent<GrabbablePhysicsObject>();
        }

        private void EnsureBuffer()
        {
            var size = Mathf.Max(1, MaxCargoColliders);
            if (_hits == null || _hits.Length != size)
                _hits = new Collider[size];
        }

        private void StorePose()
        {
            _previousPosition = transform.position;
            _hasPreviousPose = true;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Active ? Color.cyan : Color.gray;
            Gizmos.matrix = Matrix4x4.TRS(transform.TransformPoint(LocalCenter), transform.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, LocalSize);
        }
    }
}
