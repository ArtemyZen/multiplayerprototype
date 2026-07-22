using Fusion;
using System.Collections.Generic;
using UnityEngine;

namespace FriendSlop
{
    [RequireComponent(typeof(NetworkObject))]
    public sealed class ToggleBoxItem : NetworkBehaviour, IWorldInteractable, IHeldItemAction
    {
        [Header("State")]
        public bool StartOpen;

        [Header("Visuals")]
        public GameObject OpenObject;
        public GameObject CloseObject;
        public string OpenObjectName = "Box_Open";
        public string CloseObjectName = "Box_Close";

        [Header("Cargo")]
        public LayerMask CargoMask = ~0;
        public Vector3 CargoLocalCenter = new Vector3(0f, 0.35f, 0f);
        public Vector3 CargoLocalSize = new Vector3(0.5f, 0.5f, 0.5f);
        public int MaxCargoColliders = 32;

        [Networked] private NetworkBool IsOpen { get; set; }

        private Collider[] _cargoHits;
        private readonly List<GrabbablePhysicsObject> _packedCargo = new List<GrabbablePhysicsObject>();
        private Rigidbody _rigidbody;
        private bool _hasPredictedOpen;
        private bool _predictedOpen;
        private float _predictedOpenUntil;

        public override void Spawned()
        {
            CacheReferences();
            _rigidbody = GetComponent<Rigidbody>();

            if (HasStateAuthority)
            {
                IsOpen = StartOpen;
                if (!IsOpen)
                    PackCargo();
            }

            ApplyState();
        }

        public override void Render()
        {
            ApplyState();
        }

        public void Interact(PlayerRef interactor, NetworkObject instigator)
        {
            Toggle(interactor);
        }

        public void UseHeld(PlayerRef interactor, NetworkObject instigator)
        {
            Toggle(interactor);
        }

        private void Toggle(PlayerRef interactor)
        {
            if (!HasStateAuthority)
            {
                if (_hasPredictedOpen && Time.time < _predictedOpenUntil)
                    return;

                SetPredictedOpen(!GetVisibleOpen());
                RPC_Toggle(interactor);
                return;
            }

            ToggleInternal();
        }

        private void ToggleInternal()
        {
            IsOpen = !IsOpen;

            if (IsOpen)
                UnpackCargo();
            else
                PackCargo();

            ApplyState();
        }

        private void CacheReferences()
        {
            if (OpenObject == null)
                OpenObject = FindChildByName(OpenObjectName);

            if (CloseObject == null)
                CloseObject = FindChildByName(CloseObjectName);
        }

        private GameObject FindChildByName(string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName))
                return null;

            var children = GetComponentsInChildren<Transform>(true);
            foreach (var child in children)
            {
                if (child != transform && child.name == targetName)
                    return child.gameObject;
            }

            return null;
        }

        private void ApplyState()
        {
            CacheReferences();

            var visibleOpen = GetVisibleOpen();
            if (OpenObject != null)
                OpenObject.SetActive(visibleOpen);

            if (CloseObject != null)
                CloseObject.SetActive(!visibleOpen);
        }

        private bool GetVisibleOpen()
        {
            var networkOpen = (bool)IsOpen;
            if (_hasPredictedOpen)
            {
                if (networkOpen == _predictedOpen || Time.time >= _predictedOpenUntil)
                    _hasPredictedOpen = false;
                else
                    return _predictedOpen;
            }

            return networkOpen;
        }

        private void SetPredictedOpen(bool open)
        {
            _hasPredictedOpen = true;
            _predictedOpen = open;
            _predictedOpenUntil = Time.time + 1f;
            ApplyState();
        }

        private void PackCargo()
        {
            if (!HasStateAuthority)
                return;

            EnsureCargoBuffer();

            var count = Physics.OverlapBoxNonAlloc(
                transform.TransformPoint(CargoLocalCenter),
                CargoLocalSize * 0.5f,
                _cargoHits,
                transform.rotation,
                CargoMask,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                var item = _cargoHits[i] != null ? _cargoHits[i].GetComponentInParent<GrabbablePhysicsObject>() : null;
                if (item == null || item.Object == Object || item.IsPackedForCargo || item.IsCurrentlyHeld)
                    continue;

                item.PackInto(Object, transform.InverseTransformPoint(item.Position), Quaternion.Inverse(transform.rotation) * item.transform.rotation);
                _packedCargo.Add(item);
            }
        }

        private void UnpackCargo()
        {
            if (!HasStateAuthority)
                return;

            var releaseVelocity = _rigidbody != null ? _rigidbody.velocity : Vector3.zero;
            for (int i = _packedCargo.Count - 1; i >= 0; i--)
            {
                var item = _packedCargo[i];
                if (item == null)
                    continue;

                item.UnpackFromCargo(releaseVelocity);
            }

            _packedCargo.Clear();
        }

        private void EnsureCargoBuffer()
        {
            var bufferSize = Mathf.Max(1, MaxCargoColliders);
            if (_cargoHits == null || _cargoHits.Length != bufferSize)
                _cargoHits = new Collider[bufferSize];
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = StartOpen ? Color.green : Color.yellow;
            Gizmos.matrix = Matrix4x4.TRS(transform.TransformPoint(CargoLocalCenter), transform.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, CargoLocalSize);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RPC_Toggle(PlayerRef interactor)
        {
            ToggleInternal();
        }
    }
}
