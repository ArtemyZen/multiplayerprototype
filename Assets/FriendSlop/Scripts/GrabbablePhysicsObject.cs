using UnityEngine;
using Fusion;

namespace FriendSlop
{
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class GrabbablePhysicsObject : NetworkBehaviour
    {
        private const int MinWeightLevel = 1;
        private const int MaxWeightLevel = 8;

        [Header("Weight")]
        [Range(1, 8)] public int WeightLevel = 1;
        [Range(0.02f, 1f)] public float HeavyWeightEfficiency = 0.02f;
        public int WeightReductionPerHolder = 2;
        public bool ApplyWeightToRigidbodyMass = true;
        public float HeavyWeightMassMultiplier = 5f;

        [Header("Physics")]
        public float HeldCollisionSkin = 0.03f;
        public int HeldCollisionResolveIterations = 3;
        public bool ForceDynamicInterpolation = true;
        public float MaxHoldSpeed = 18f;
        public float ProxyLerpSpeed = 18f;
        public float HoldTargetSmoothing = 18f;
        [Tooltip("Drops a held object if no holder sends HoldAt for this many seconds.")]
        public float HoldHeartbeatTimeout = 0.75f;
        [Tooltip("Ignores late hold RPCs from a player for a short time after that player released/threw this object.")]
        public float ReleaseHoldIgnoreTime = 0.25f;
        public float LocalReleasePredictionTime = 0.2f;

        [Networked, Capacity(16)] private NetworkLinkedList<PlayerRef> Holders => default;
        [Networked] public PlayerRef Holder { get; private set; }
        [Networked] private NetworkBool IsHeld { get; set; }
        [Networked] private Vector3 NetworkPosition { get; set; }
        [Networked] private Quaternion NetworkRotation { get; set; }
        [Networked] private Vector3 NetworkVelocity { get; set; }
        [Networked] private Vector3 NetworkAngularVelocity { get; set; }
        [Networked] private NetworkBool IsPacked { get; set; }
        [Networked] private NetworkObject PackedContainer { get; set; }
        [Networked] private Vector3 PackedLocalPosition { get; set; }
        [Networked] private Quaternion PackedLocalRotation { get; set; }

        private Rigidbody _rigidbody;
        private Collider[] _colliders;
        private bool[] _initialColliderEnabled;
        private Renderer[] _renderers;
        private bool[] _initialRendererEnabled;
        private CollisionDetectionMode _initialCollisionDetectionMode;
        private RigidbodyInterpolation _initialInterpolation;
        private float _initialMass;
        private int _holdTargetTick = -1;
        private int _holdTargetCount;
        private Vector3 _holdTargetPositionSum;
        private Vector3 _smoothedHoldTargetPosition;
        private bool _hasSmoothedHoldTarget;
        private TickTimer _holdHeartbeatTimer;
        private bool _collidersDisabledForPacking;
        private bool _packedPresentationApplied;
        private bool _looseCargoLocked;
        private bool _looseCargoCollidersDisabled;
        private PlayerRef _recentReleaseHolder;
        private TickTimer _recentReleaseIgnoreTimer;
        private float _localReleasePresentationUntil;

        public bool CanBeGrabbed => !IsPacked && !_looseCargoLocked && Holders.Count < Holders.Capacity;
        public bool IsHeldBy(PlayerRef player) => IsHeld && Holders.Contains(player);
        public int HolderCount => Holders.Count;
        public int EffectiveWeightLevel => GetEffectiveWeightLevel(HolderCount);
        public Vector3 Position => _rigidbody != null ? _rigidbody.position : transform.position;
        public Vector3 Center => GetCenter();
        public Vector3 Velocity => _rigidbody != null ? _rigidbody.velocity : NetworkVelocity;
        public bool IsCurrentlyHeld => IsHeld;
        public bool IsPackedForCargo => IsPacked;
        public bool IsLooseCargoLocked => _looseCargoLocked;
        public int CargoWeight => WeightLevel;
        public float WeightEfficiency => GetWeightEfficiencyForHolderCount(HolderCount);
        public float WeightT => GetWeightT(WeightLevel);


        private void OnValidate()
        {
            WeightLevel = Mathf.Clamp(WeightLevel, MinWeightLevel, MaxWeightLevel);
            HeavyWeightEfficiency = Mathf.Clamp(HeavyWeightEfficiency, 0.02f, 1f);
            WeightReductionPerHolder = Mathf.Max(0, WeightReductionPerHolder);
            HeavyWeightMassMultiplier = Mathf.Max(1f, HeavyWeightMassMultiplier);
            HeldCollisionSkin = Mathf.Max(0f, HeldCollisionSkin);
            HeldCollisionResolveIterations = Mathf.Max(1, HeldCollisionResolveIterations);
            HoldTargetSmoothing = Mathf.Max(0f, HoldTargetSmoothing);
            HoldHeartbeatTimeout = Mathf.Max(0.05f, HoldHeartbeatTimeout);
            ReleaseHoldIgnoreTime = Mathf.Max(0f, ReleaseHoldIgnoreTime);
            LocalReleasePredictionTime = Mathf.Max(0f, LocalReleasePredictionTime);
        }

        private void ApplyWeightMass()
        {
            if (!ApplyWeightToRigidbodyMass || _rigidbody == null)
                return;

            _rigidbody.mass = _initialMass * Mathf.Lerp(1f, HeavyWeightMassMultiplier, WeightT);
        }

        public override void Spawned()
        {
            _rigidbody = GetComponent<Rigidbody>();
            _colliders = GetComponentsInChildren<Collider>();
            _initialColliderEnabled = new bool[_colliders.Length];
            for (int i = 0; i < _colliders.Length; i++)
                _initialColliderEnabled[i] = _colliders[i] != null && _colliders[i].enabled;

            _renderers = GetComponentsInChildren<Renderer>();
            _initialRendererEnabled = new bool[_renderers.Length];
            for (int i = 0; i < _renderers.Length; i++)
                _initialRendererEnabled[i] = _renderers[i] != null && _renderers[i].enabled;

            _initialCollisionDetectionMode = _rigidbody.collisionDetectionMode;
            _initialInterpolation = _rigidbody.interpolation;
            _initialMass = _rigidbody.mass;
            ApplyWeightMass();
            ApplyDynamicInterpolation();
            ApplyAuthorityMode();

            if (HasStateAuthority)
                CopyPhysicsToNetworkState();
        }

        public bool BeginGrab(PlayerRef holder)
        {
            if (!HasStateAuthority)
            {
                if (!IsHeld)
                    Object.RequestStateAuthority();

                RPC_BeginGrab(holder);
                return true;
            }

            return BeginGrabInternal(holder);
        }

        private bool BeginGrabInternal(PlayerRef holder)
        {
            if (IsPacked)
                return false;

            if (holder == PlayerRef.None)
                return false;

            if (ShouldIgnoreRecentReleaseHolder(holder))
                return false;

            if (Holders.Contains(holder))
            {
                RefreshHoldHeartbeat();
                return true;
            }

            if (Holders.Count >= Holders.Capacity)
                return false;

            Holders.Add(holder);
            RefreshHoldState();
            RefreshHoldHeartbeat();
            _hasSmoothedHoldTarget = false;

            _rigidbody.velocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;
            _rigidbody.isKinematic = true;
            _rigidbody.useGravity = false;
            _rigidbody.drag = 0f;
            _rigidbody.angularDrag = 0f;
            _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            _rigidbody.interpolation = RigidbodyInterpolation.None;
            _rigidbody.WakeUp();
            CopyPhysicsToNetworkState();
            return true;
        }

        public void HoldAt(Vector3 targetPosition, Quaternion targetRotation, float moveSpeed)
        {
            HoldAt(Holder, targetPosition, targetRotation, moveSpeed);
        }

        public void HoldAt(PlayerRef holder, Vector3 targetPosition, Quaternion targetRotation, float moveSpeed)
        {
            if (!HasStateAuthority)
            {
                RPC_HoldAt(holder, targetPosition, targetRotation, moveSpeed);
                return;
            }

            HoldAtInternal(holder, targetPosition, targetRotation, moveSpeed);
        }

        private void HoldAtInternal(PlayerRef holder, Vector3 targetPosition, Quaternion targetRotation, float moveSpeed)
        {
            if (IsPacked)
                return;

            if (!EnsureHolderActive(holder))
                return;

            if (_holdTargetTick != Runner.Tick)
            {
                _holdTargetTick = Runner.Tick;
                _holdTargetCount = 0;
                _holdTargetPositionSum = Vector3.zero;
            }

            _holdTargetCount++;
            _holdTargetPositionSum += targetPosition;
            RefreshHoldHeartbeat();

            _smoothedHoldTargetPosition = _holdTargetPositionSum / _holdTargetCount;
            _hasSmoothedHoldTarget = true;
            _rigidbody.WakeUp();
            _rigidbody.MovePosition(ResolveHeldTargetPosition(_smoothedHoldTargetPosition));
            _rigidbody.MoveRotation(targetRotation);
            CopyPhysicsToNetworkState();
        }

        public void PresentHeldLocally(PlayerRef holder, Vector3 targetPosition, Quaternion targetRotation, bool forceLocalPrediction = false)
        {
            if (_rigidbody == null || !_rigidbody.isKinematic)
                return;

            if (!forceLocalPrediction && (!HasStateAuthority || !IsHeldBy(holder)))
                return;

            var resolvedPosition = ResolveHeldTargetPosition(targetPosition);
            _rigidbody.position = resolvedPosition;
            _rigidbody.rotation = targetRotation;
            transform.SetPositionAndRotation(resolvedPosition, targetRotation);
        }

        public void PresentLooseCargoLocally(Transform containerTransform, Vector3 localPosition, Quaternion localRotation)
        {
            if (!_looseCargoLocked || _rigidbody == null || containerTransform == null)
                return;

            var targetPosition = containerTransform.TransformPoint(localPosition);
            var targetRotation = containerTransform.rotation * localRotation;
            _rigidbody.position = targetPosition;
            _rigidbody.rotation = targetRotation;
            transform.SetPositionAndRotation(targetPosition, targetRotation);
        }

        public void LockAsLooseCargo(NetworkObject container, Vector3 localPosition, Quaternion localRotation, bool disableColliders)
        {
            if (!HasStateAuthority || IsPacked || IsHeld || container == null || container == Object || _rigidbody == null)
                return;

            _looseCargoLocked = true;

            if (disableColliders && !_looseCargoCollidersDisabled)
            {
                SetCollidersEnabled(false);
                _looseCargoCollidersDisabled = true;
            }

            var containerTransform = container.transform;
            var targetPosition = containerTransform.TransformPoint(localPosition);
            var targetRotation = containerTransform.rotation * localRotation;

            if (!_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }

            _rigidbody.isKinematic = true;
            _rigidbody.useGravity = false;
            _rigidbody.drag = 0f;
            _rigidbody.angularDrag = 0f;
            _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            _rigidbody.interpolation = RigidbodyInterpolation.None;
            _rigidbody.MovePosition(targetPosition);
            _rigidbody.MoveRotation(targetRotation);
            transform.SetPositionAndRotation(targetPosition, targetRotation);
            CopyPhysicsToNetworkState();
        }

        public void UnlockLooseCargo(Vector3 releaseVelocity)
        {
            if (!_looseCargoLocked || _rigidbody == null)
                return;

            _looseCargoLocked = false;

            if (_looseCargoCollidersDisabled)
            {
                RestoreColliderStates();
                _looseCargoCollidersDisabled = false;
            }

            _rigidbody.isKinematic = false;
            _rigidbody.useGravity = true;
            _rigidbody.drag = 0f;
            _rigidbody.angularDrag = 0.05f;
            _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            ApplyDynamicInterpolation();
            _rigidbody.WakeUp();
            _rigidbody.velocity = releaseVelocity;
            _rigidbody.angularVelocity = Vector3.zero;
            CopyPhysicsToNetworkState();
        }

        public void StabilizeLooseCargo(
            Vector3 carrierVelocity,
            Vector3 carrierAngularVelocity,
            Vector3 carrierPivot,
            float linearStrength,
            float relativeDamping,
            float maxAcceleration,
            float angularStrength)
        {
            if (!HasStateAuthority || IsPacked || IsHeld || _rigidbody == null || _rigidbody.isKinematic)
                return;

            var deltaTime = Runner != null ? Runner.DeltaTime : Time.fixedDeltaTime;
            if (deltaTime <= 0f)
                return;

            var pointVelocity = carrierVelocity + Vector3.Cross(carrierAngularVelocity, _rigidbody.worldCenterOfMass - carrierPivot);
            var velocityDelta = (pointVelocity - _rigidbody.velocity) * Mathf.Clamp01(linearStrength);
            var maxDelta = Mathf.Max(0f, maxAcceleration) * deltaTime;
            if (maxDelta > 0f)
                velocityDelta = Vector3.ClampMagnitude(velocityDelta, maxDelta);

            _rigidbody.WakeUp();
            _rigidbody.velocity += velocityDelta;

            if (relativeDamping > 0f)
            {
                var damping = 1f - Mathf.Exp(-relativeDamping * deltaTime);
                var relativeVelocity = _rigidbody.velocity - pointVelocity;
                _rigidbody.velocity = pointVelocity + Vector3.Lerp(relativeVelocity, Vector3.zero, damping);
            }

            if (angularStrength > 0f)
                _rigidbody.angularVelocity = Vector3.Lerp(_rigidbody.angularVelocity, carrierAngularVelocity, Mathf.Clamp01(angularStrength));

            if (_rigidbody.collisionDetectionMode == CollisionDetectionMode.Discrete)
                _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            CopyPhysicsToNetworkState();
        }

        private Vector3 ResolveHeldTargetPosition(Vector3 targetPosition)
        {
            var resolvedPosition = _rigidbody.position;
            var desiredPosition = targetPosition;
            var iterations = Mathf.Max(1, HeldCollisionResolveIterations);

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                var delta = desiredPosition - resolvedPosition;
                var distance = delta.magnitude;

                if (distance <= 0.0001f)
                    return desiredPosition;

                var direction = delta / distance;
                var hits = _rigidbody.SweepTestAll(direction, distance + HeldCollisionSkin, QueryTriggerInteraction.Ignore);
                System.Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));

                var blocked = false;
                foreach (var hit in hits)
                {
                    if (ShouldIgnoreHeldSweepHit(hit.collider))
                        continue;

                    var safeDistance = Mathf.Max(0f, hit.distance - HeldCollisionSkin);
                    var blockedPosition = resolvedPosition + direction * Mathf.Min(safeDistance, distance);
                    var remainingDelta = desiredPosition - blockedPosition;
                    desiredPosition = blockedPosition + Vector3.ProjectOnPlane(remainingDelta, hit.normal);
                    resolvedPosition = blockedPosition;
                    blocked = true;
                    break;
                }

                if (!blocked)
                    return desiredPosition;
            }

            return resolvedPosition;
        }

        private bool ShouldIgnoreHeldSweepHit(Collider hitCollider)
        {
            if (hitCollider == null || ContainsCollider(hitCollider))
                return true;

            var grabbable = hitCollider.GetComponentInParent<GrabbablePhysicsObject>();
            if (grabbable != null)
                return true;

            var attachedRigidbody = hitCollider.attachedRigidbody;
            return attachedRigidbody != null && !attachedRigidbody.isKinematic;
        }

        private bool EnsureHolderActive(PlayerRef holder)
        {
            if (holder == PlayerRef.None)
                return IsHeld;

            if (Holders.Contains(holder))
                return true;

            if (ShouldIgnoreRecentReleaseHolder(holder))
                return false;

            if (Holders.Count >= Holders.Capacity)
                return false;

            return BeginGrabInternal(holder);
        }

        public void EndGrab(Vector3 releasePosition, Vector3 releaseVelocity)
        {
            EndGrab(PlayerRef.None, releasePosition, releaseVelocity);
        }

        public void EndGrab(PlayerRef holder, Vector3 releasePosition, Vector3 releaseVelocity)
        {
            if (!HasStateAuthority)
            {
                PredictReleaseLocally(releasePosition, releaseVelocity);
                RPC_EndGrab(holder, releasePosition, releaseVelocity);
                return;
            }

            if (!IsHeld)
                return;

            EndGrabInternal(holder, releasePosition, releaseVelocity);
        }

        private void EndGrabInternal(PlayerRef holder, Vector3 releasePosition, Vector3 releaseVelocity)
        {
            if (!IsHeld)
                return;

            MarkRecentReleaseHolder(holder);

            if (holder == PlayerRef.None)
                Holders.Clear();
            else
                Holders.Remove(holder);

            RefreshHoldState();

            if (IsHeld)
            {
                RefreshHoldHeartbeat();
                _rigidbody.WakeUp();
                CopyPhysicsToNetworkState();
                return;
            }

            _holdHeartbeatTimer = default;
            _hasSmoothedHoldTarget = false;
            _rigidbody.isKinematic = false;
            _rigidbody.position = releasePosition;
            _rigidbody.useGravity = true;
            _rigidbody.drag = 0f;
            _rigidbody.angularDrag = 0.05f;
            _rigidbody.collisionDetectionMode = _initialCollisionDetectionMode;
            ApplyDynamicInterpolation();
            _rigidbody.WakeUp();
            _rigidbody.velocity = releaseVelocity;
            _rigidbody.angularVelocity = Random.insideUnitSphere * releaseVelocity.magnitude * 0.15f;
            CopyPhysicsToNetworkState();
        }

        private void PredictReleaseLocally(Vector3 releasePosition, Vector3 releaseVelocity)
        {
            if (_rigidbody == null)
                return;

            _localReleasePresentationUntil = Time.time + LocalReleasePredictionTime;
            _rigidbody.isKinematic = false;
            _rigidbody.position = releasePosition;
            _rigidbody.rotation = transform.rotation;
            _rigidbody.useGravity = true;
            _rigidbody.drag = 0f;
            _rigidbody.angularDrag = 0.05f;
            _rigidbody.collisionDetectionMode = _initialCollisionDetectionMode;
            ApplyDynamicInterpolation();
            _rigidbody.WakeUp();
            _rigidbody.velocity = releaseVelocity;
            _rigidbody.angularVelocity = Random.insideUnitSphere * releaseVelocity.magnitude * 0.15f;
            transform.position = releasePosition;
        }

        private bool IsLocalReleasePresentationActive()
        {
            return !HasStateAuthority && Time.time < _localReleasePresentationUntil;
        }

        private void MarkRecentReleaseHolder(PlayerRef holder)
        {
            if (Runner == null || holder == PlayerRef.None || ReleaseHoldIgnoreTime <= 0f)
                return;

            _recentReleaseHolder = holder;
            _recentReleaseIgnoreTimer = TickTimer.CreateFromSeconds(Runner, ReleaseHoldIgnoreTime);
        }

        private bool ShouldIgnoreRecentReleaseHolder(PlayerRef holder)
        {
            if (Runner == null || holder == PlayerRef.None || holder != _recentReleaseHolder)
                return false;

            if (!_recentReleaseIgnoreTimer.IsRunning || _recentReleaseIgnoreTimer.Expired(Runner))
                return false;

            return true;
        }

        public int GetEffectiveWeightLevel(int holderCount)
        {
            var additionalHolders = Mathf.Max(0, holderCount - 1);
            var reduction = additionalHolders * Mathf.Max(0, WeightReductionPerHolder);
            return Mathf.Clamp(WeightLevel - reduction, MinWeightLevel, MaxWeightLevel);
        }

        public float GetWeightEfficiencyForHolderCount(int holderCount)
        {
            return Mathf.Lerp(1f, HeavyWeightEfficiency, GetWeightT(GetEffectiveWeightLevel(holderCount)));
        }

        private float GetWeightT(int weightLevel)
        {
            return (Mathf.Clamp(weightLevel, MinWeightLevel, MaxWeightLevel) - 1f) / (MaxWeightLevel - 1f);
        }

        public bool ContainsCollider(Collider candidate)
        {
            if (candidate == null || _colliders == null)
                return false;

            foreach (var objectCollider in _colliders)
            {
                if (objectCollider == candidate)
                    return true;
            }

            return false;
        }

        public void IgnoreCollisionsWith(Collider[] otherColliders, bool ignore)
        {
            if (_colliders == null || otherColliders == null)
                return;

            foreach (var itemCollider in _colliders)
            {
                if (itemCollider == null)
                    continue;

                foreach (var otherCollider in otherColliders)
                {
                    if (otherCollider == null || otherCollider == itemCollider)
                        continue;

                    Physics.IgnoreCollision(itemCollider, otherCollider, ignore);
                }
            }
        }

        public void PackInto(NetworkObject container, Vector3 localPosition, Quaternion localRotation)
        {
            if (!HasStateAuthority)
            {
                RPC_PackInto(container, localPosition, localRotation);
                return;
            }

            PackIntoInternal(container, localPosition, localRotation);
        }

        public void UnpackFromCargo(Vector3 releaseVelocity)
        {
            if (!HasStateAuthority)
            {
                RPC_UnpackFromCargo(releaseVelocity);
                return;
            }

            UnpackFromCargoInternal(releaseVelocity);
        }

        private void PackIntoInternal(NetworkObject container, Vector3 localPosition, Quaternion localRotation)
        {
            if (container == null || container == Object)
                return;

            if (_looseCargoLocked)
                UnlockLooseCargo(Vector3.zero);

            Holders.Clear();
            RefreshHoldState();
            _holdHeartbeatTimer = default;
            _hasSmoothedHoldTarget = false;

            IsPacked = true;
            PackedContainer = container;
            PackedLocalPosition = localPosition;
            PackedLocalRotation = localRotation;

            SetCollidersEnabled(false);
            SetRenderersEnabled(false);
            _packedPresentationApplied = true;
            FollowPackedContainer();
            CopyPhysicsToNetworkState();
        }

        private void UnpackFromCargoInternal(Vector3 releaseVelocity)
        {
            if (!IsPacked)
                return;

            IsPacked = false;
            PackedContainer = null;
            RestoreColliderStates();
            RestoreRendererStates();
            _packedPresentationApplied = false;
            _hasSmoothedHoldTarget = false;
            _rigidbody.isKinematic = !HasStateAuthority;
            _rigidbody.useGravity = HasStateAuthority;
            _rigidbody.drag = 0f;
            _rigidbody.angularDrag = 0.05f;
            _rigidbody.collisionDetectionMode = _initialCollisionDetectionMode;
            ApplyDynamicInterpolation();
            _rigidbody.velocity = releaseVelocity;
            _rigidbody.angularVelocity = Vector3.zero;
            _rigidbody.WakeUp();
            CopyPhysicsToNetworkState();
        }

        private void SetCollidersEnabled(bool enabled)
        {
            if (_colliders == null)
                return;

            foreach (var itemCollider in _colliders)
            {
                if (itemCollider != null)
                    itemCollider.enabled = enabled;
            }

            _collidersDisabledForPacking = !enabled;
        }

        private void RestoreColliderStates()
        {
            if (_colliders == null)
                return;

            for (int i = 0; i < _colliders.Length; i++)
            {
                if (_colliders[i] != null)
                    _colliders[i].enabled = _initialColliderEnabled == null || i >= _initialColliderEnabled.Length || _initialColliderEnabled[i];
            }

            _collidersDisabledForPacking = false;
        }

        private void SetRenderersEnabled(bool enabled)
        {
            if (_renderers == null)
                return;

            foreach (var itemRenderer in _renderers)
            {
                if (itemRenderer != null)
                    itemRenderer.enabled = enabled;
            }
        }

        private void RestoreRendererStates()
        {
            if (_renderers == null)
                return;

            for (int i = 0; i < _renderers.Length; i++)
            {
                if (_renderers[i] != null)
                    _renderers[i].enabled = _initialRendererEnabled == null || i >= _initialRendererEnabled.Length || _initialRendererEnabled[i];
            }
        }

        private void ApplyPackedPresentationState()
        {
            if (IsPacked)
            {
                if (!_packedPresentationApplied)
                {
                    SetCollidersEnabled(false);
                    SetRenderersEnabled(false);
                    _packedPresentationApplied = true;
                }

                return;
            }

            if (_packedPresentationApplied)
            {
                RestoreColliderStates();
                RestoreRendererStates();
                _packedPresentationApplied = false;
            }
        }

        private void FollowPackedContainer()
        {
            if (!IsPacked || PackedContainer == null)
                return;

            var containerTransform = PackedContainer.transform;
            var targetPosition = containerTransform.TransformPoint(PackedLocalPosition);
            var targetRotation = containerTransform.rotation * PackedLocalRotation;

            _rigidbody.isKinematic = true;
            _rigidbody.useGravity = false;
            _rigidbody.MovePosition(targetPosition);
            _rigidbody.MoveRotation(targetRotation);
        }

        private Vector3 GetCenter()
        {
            if (_colliders == null || _colliders.Length == 0)
                return Position;

            var hasBounds = false;
            var bounds = new Bounds(Position, Vector3.zero);

            foreach (var objectCollider in _colliders)
            {
                if (objectCollider == null || !objectCollider.enabled)
                    continue;

                if (!hasBounds)
                {
                    bounds = objectCollider.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(objectCollider.bounds);
                }
            }

            return hasBounds ? bounds.center : Position;
        }

        public override void FixedUpdateNetwork()
        {
            ApplyAuthorityMode();
            ApplyPackedPresentationState();

            if (HasStateAuthority)
            {
                if (IsPacked)
                {
                    FollowPackedContainer();
                    CopyPhysicsToNetworkState();
                    return;
                }

                DropIfHoldHeartbeatExpired();
                CopyPhysicsToNetworkState();
            }
            else
                ApplyNetworkStateToProxy(false);
        }

        public override void Render()
        {
            ApplyPackedPresentationState();

            if (!HasStateAuthority)
                ApplyNetworkStateToProxy(true);
        }

        private void ApplyAuthorityMode()
        {
            if (_rigidbody == null)
                return;

            if (IsPacked)
            {
                _rigidbody.isKinematic = true;
                _rigidbody.useGravity = false;
                return;
            }

            if (IsLocalReleasePresentationActive())
            {
                _rigidbody.isKinematic = false;
                _rigidbody.useGravity = true;
                return;
            }

            _rigidbody.isKinematic = !HasStateAuthority || IsHeld || _looseCargoLocked;
            if (!HasStateAuthority)
                _rigidbody.useGravity = false;
        }

        private void RefreshHoldState()
        {
            IsHeld = Holders.Count > 0;
            Holder = IsHeld ? Holders[0] : PlayerRef.None;
        }

        private void RefreshHoldHeartbeat()
        {
            if (Runner == null)
                return;

            _holdHeartbeatTimer = TickTimer.CreateFromSeconds(Runner, Mathf.Max(0.05f, HoldHeartbeatTimeout));
        }

        private void DropIfHoldHeartbeatExpired()
        {
            if (!IsHeld)
                return;

            if (_holdHeartbeatTimer.IsRunning && !_holdHeartbeatTimer.Expired(Runner))
                return;

            EndGrabInternal(PlayerRef.None, Position, Velocity);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RPC_BeginGrab(PlayerRef holder)
        {
            BeginGrabInternal(holder);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RPC_HoldAt(PlayerRef holder, Vector3 targetPosition, Quaternion targetRotation, float moveSpeed)
        {
            HoldAtInternal(holder, targetPosition, targetRotation, moveSpeed);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RPC_EndGrab(PlayerRef holder, Vector3 releasePosition, Vector3 releaseVelocity)
        {
            EndGrabInternal(holder, releasePosition, releaseVelocity);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RPC_PackInto(NetworkObject container, Vector3 localPosition, Quaternion localRotation)
        {
            PackIntoInternal(container, localPosition, localRotation);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RPC_UnpackFromCargo(Vector3 releaseVelocity)
        {
            UnpackFromCargoInternal(releaseVelocity);
        }

        private void CopyPhysicsToNetworkState()
        {
            NetworkPosition = _rigidbody.position;
            NetworkRotation = _rigidbody.rotation;

            if (IsPacked || _looseCargoLocked)
            {
                NetworkVelocity = Vector3.zero;
                NetworkAngularVelocity = Vector3.zero;
                return;
            }

            NetworkVelocity = _rigidbody.velocity;
            NetworkAngularVelocity = _rigidbody.angularVelocity;
        }

        private void ApplyDynamicInterpolation()
        {
            if (_rigidbody == null)
                return;

            _rigidbody.interpolation = ForceDynamicInterpolation ? RigidbodyInterpolation.Interpolate : _initialInterpolation;
        }

        private void ApplyNetworkStateToProxy(bool interpolate)
        {
            if (IsLocalReleasePresentationActive())
                return;

            _rigidbody.useGravity = false;

            if (!_rigidbody.isKinematic)
            {
                _rigidbody.velocity = NetworkVelocity;
                _rigidbody.angularVelocity = NetworkAngularVelocity;
            }

            if (interpolate)
            {
                var t = 1f - Mathf.Exp(-ProxyLerpSpeed * Time.deltaTime);
                _rigidbody.position = Vector3.Lerp(_rigidbody.position, NetworkPosition, t);
                _rigidbody.rotation = Quaternion.Slerp(_rigidbody.rotation, NetworkRotation, t);
            }
            else
            {
                _rigidbody.position = NetworkPosition;
                _rigidbody.rotation = NetworkRotation;
            }
        }

    }
}


