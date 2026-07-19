using Fusion;
using Starter.Shooter;
using UnityEngine;
using UnityEngine.UI;

namespace FriendSlop
{
    [RequireComponent(typeof(Player))]
    public sealed class ShooterPlayerObjectInteractor : NetworkBehaviour
    {
        [Header("References")]
        public Transform HoldPoint;
        public LayerMask GrabMask = ~0;
        public LayerMask ObstructionMask = ~0;
        public LayerMask InteractMask = ~0;

        [Header("Input")]
        public KeyCode GrabKey = KeyCode.Mouse1;
        public KeyCode ThrowKey = KeyCode.Mouse0;
        public KeyCode InteractKey = KeyCode.F;

        [Header("Crosshair")]
        public string CrosshairObjectName = "Crosshair";
        public Sprite DefaultCrosshair;
        public Sprite GrabCrosshair;
        public bool AutoFindCrosshairImage = true;

        [Header("Interact")]
        public float InteractDistance = 3f;

        [Header("Grab")]
        public float GrabDistance = 3f;
        public float HoldDistance = 1.6f;
        public float HoldMoveSpeed = 24f;
        public float ThrowImpulse = 11f;
        public float DropForwardOffset = 0.6f;
        public float MinimumHoldTime = 0.45f;
        public bool UseSphereObstructionProbe;
        public float ObstructionProbeRadius = 0f;
        public float ObstructionGraceTime = 0.25f;
        public float ObstructionMargin = 0.05f;
        public int ObstructionReleaseTicks = 4;
        public float GrabCooldownTime = 0.25f;
        public float RespawnGrabCooldownTime = 0.35f;
        public float HolderValidationGraceTime = 1f;
        public float HolderValidationRetryTime = 0.15f;
        public float HolderValidationGiveUpTime = 4f;
        public float MaxHoldStretchDistance = 3.5f;
        public int HoldStretchReleaseTicks = 8;

        [Header("Catch Assist")]
        public bool EnableCatchAssist = true;
        public float CatchDistance = 3.25f;
        public float CatchRadius = 0.85f;
        public float CatchMinObjectSpeed = 1.5f;
        [Range(0f, 180f)] public float CatchMaxAngle = 80f;

        [Header("Player Carry")]
        public bool CanCarryPlayers = true;
        public float PlayerCarryDistance = 1.3f;
        public float PlayerCarryMoveSpeed = 18f;
        public float PlayerCarryThrowImpulse = 9f;
        [Range(0.05f, 1f)] public float CarriedPlayerEfficiency = 0.45f;
        public float PlayerCarryReleaseForwardOffset = 0.75f;

        [Header("Weight Effect")]
        [Tooltip("At effective weight 8, player/throw/hold effectiveness is multiplied by this value.")]
        [Range(0.02f, 1f)] public float HeavyWeightEfficiency = 0.02f;
        public bool SlowMovementWhenHolding = true;
        public bool SlowLookWhenHolding;
        public bool WeakenThrowWhenHolding = true;
        public bool WeakenHoldWhenHolding = true;

        private Player _player;
        private CarryablePlayer _selfCarryable;
        private GrabbablePhysicsObject _heldObject;
        private CarryablePlayer _carriedPlayer;
        private IGrabHoldInteractable _activeGrabHoldInteractable;
        private Collider[] _playerColliders;
        private TickTimer _minimumHoldTimer;
        private TickTimer _obstructionGraceTimer;
        private TickTimer _grabCooldownTimer;
        private TickTimer _holderValidationGraceTimer;
        private TickTimer _holderValidationRetryTimer;
        private TickTimer _holderValidationGiveUpTimer;
        private int _obstructedTicks;
        private int _overstretchedTicks;
        private bool _wasAlive;
        private bool _grabHeld;
        private bool _grabRequiresRelease;
        private bool _throwPressed;
        private bool _interactPressed;
        private Vector2 _grabHoldMoveInput;
        private float _baseWalkSpeed;
        private Image _crosshairImage;
        private bool _crosshairSearched;

        [Networked] public NetworkBool StickyHandActive { get; private set; }
        [Networked] public Vector3 StickyHandTargetPosition { get; private set; }
        public bool CanReadStickyHandState => Object != null && Object.IsValid;

        public override void Spawned()
        {
            _player = GetComponent<Player>();
            _selfCarryable = GetComponent<CarryablePlayer>();
            _playerColliders = GetComponentsInChildren<Collider>();
            _baseWalkSpeed = _player.WalkSpeed;
            _wasAlive = IsPlayerAlive();
            SetHoldingPoseActive(false);
            enabled = HasStateAuthority;
        }

        private void Update()
        {
            if (!HasStateAuthority)
                return;

            if (Cursor.lockState != CursorLockMode.Locked)
            {
                RefreshCrosshair(false);
                return;
            }

            var grabButtonHeld = Input.GetKey(GrabKey);
            if (!grabButtonHeld)
                _grabRequiresRelease = false;

            if (!IsPlayerAlive())
            {
                _grabHeld = false;
                _grabRequiresRelease = true;
                _throwPressed = false;
                _interactPressed = false;
                ApplyPlayerWeightEffect(1f);
                SetFireBlocked(false);
                RefreshCrosshair(false);
                return;
            }

            if (_selfCarryable != null && _selfCarryable.IsCarried)
            {
                _grabHeld = false;
                _grabRequiresRelease = true;
                _throwPressed = false;
                _interactPressed = false;
                ApplyPlayerWeightEffect(1f);
                SetFireBlocked(true);
                RefreshCrosshair(false);
                return;
            }

            _grabHeld = grabButtonHeld && !_grabRequiresRelease;
            _grabHoldMoveInput = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));

            _throwPressed |= Input.GetKeyDown(ThrowKey);
            _interactPressed |= Input.GetKeyDown(InteractKey);
            SetFireBlocked(_heldObject != null || _carriedPlayer != null || _grabHeld);
            RefreshCrosshair(HasGrabTargetUnderCrosshair());
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority)
                return;

            var isAlive = IsPlayerAlive();
            if (!isAlive)
            {
                ReleaseGrabHoldInteractable();
                ReleaseHeldObjectAtCurrentPosition(true);
                ReleaseCarriedPlayer(true);
                _grabHeld = false;
                _throwPressed = false;
                _interactPressed = false;
                _wasAlive = false;
                SetHoldingPoseActive(false);
                ApplyPlayerWeightEffect(1f);
                SetFireBlocked(false);
                RefreshCrosshair(false);
                return;
            }

            if (_selfCarryable != null && _selfCarryable.IsCarried)
            {
                ReleaseGrabHoldInteractable();
                ReleaseHeldObjectAtCurrentPosition(true);
                ReleaseCarriedPlayer(true);
                _grabHeld = false;
                _throwPressed = false;
                _interactPressed = false;
                _grabRequiresRelease = true;
                SetHoldingPoseActive(false);
                ApplyPlayerWeightEffect(1f);
                SetFireBlocked(true);
                RefreshCrosshair(false);
                return;
            }

            if (!_wasAlive && isAlive)
            {
                _grabCooldownTimer = TickTimer.CreateFromSeconds(Runner, RespawnGrabCooldownTime);
                _heldObject = null;
                _carriedPlayer = null;
                _grabRequiresRelease = true;
                _grabHeld = false;
                _throwPressed = false;
                _interactPressed = false;
                _obstructedTicks = 0;
                _overstretchedTicks = 0;
                _holderValidationRetryTimer = default;
                _holderValidationGiveUpTimer = default;
                SetHoldingPoseActive(false);
                ApplyPlayerWeightEffect(1f);
            }

            _wasAlive = true;
            ValidateHeldObject();
            ValidateCarriedPlayer();
            UpdateStickyHandTarget();
            ApplyPlayerWeightEffect(GetActiveCarryEfficiency());

            if (_throwPressed && _heldObject != null)
            {
                ThrowHeldObject();
            }
            else if (_throwPressed && _carriedPlayer != null)
            {
                ThrowCarriedPlayer();
            }
            else if (_interactPressed)
            {
                TryInteract();
            }
            else if (!_grabHeld)
            {
                ReleaseGrabHoldInteractable();
                DropHeldObject();
                DropCarriedPlayer();
            }
            else
            {
                if (_activeGrabHoldInteractable != null)
                    _activeGrabHoldInteractable.UpdateHold(Object.StateAuthority, Object, _grabHoldMoveInput);

                if (_activeGrabHoldInteractable == null && _heldObject == null && _carriedPlayer == null)
                    TryGrabTarget();

                if (_heldObject != null)
                    HoldOrReleaseForObstruction();

                if (_carriedPlayer != null)
                    HoldOrReleaseCarriedPlayer();
            }

            _throwPressed = false;
            _interactPressed = false;
            UpdateStickyHandTarget();
            ApplyPlayerWeightEffect(GetActiveCarryEfficiency());
            SetHoldingPoseActive(ShouldShowHoldPose());
            SetFireBlocked(_heldObject != null || _carriedPlayer != null || _grabHeld);
        }

        private void OnDisable()
        {
            ReleaseGrabHoldInteractable();
            ReleaseCarriedPlayer(true);
            SetHoldingPoseActive(false);
            ApplyPlayerWeightEffect(1f);
            SetFireBlocked(false);
        }

        private bool IsPlayerAlive()
        {
            return _player == null || _player.Health == null || _player.Health.IsAlive;
        }

        private void SetFireBlocked(bool blocked)
        {
            if (_player != null && _player.PlayerInput != null)
                _player.PlayerInput.FireBlocked = blocked;
        }

        private void SetHoldingPoseActive(bool active)
        {
            if (_player != null && HasStateAuthority)
                _player.HoldingItemPoseActive = active;
        }

        private bool ShouldShowHoldPose()
        {
            return _heldObject != null || _carriedPlayer != null || (_grabHeld && _activeGrabHoldInteractable == null);
        }


        private void RefreshCrosshair(bool canGrab)
        {
            var crosshairImage = GetCrosshairImage();
            if (crosshairImage == null)
                return;

            var targetSprite = canGrab && GrabCrosshair != null ? GrabCrosshair : DefaultCrosshair;
            if (crosshairImage.sprite != targetSprite)
                crosshairImage.sprite = targetSprite;
        }

        private Image GetCrosshairImage()
        {
            if (_crosshairImage != null || !AutoFindCrosshairImage || _crosshairSearched)
                return _crosshairImage;

            if (!string.IsNullOrWhiteSpace(CrosshairObjectName))
            {
                var crosshairObject = GameObject.Find(CrosshairObjectName);
                if (crosshairObject != null)
                    _crosshairImage = crosshairObject.GetComponent<Image>();
            }

            _crosshairSearched = true;
            return _crosshairImage;
        }

        private bool HasGrabTargetUnderCrosshair()
        {
            if (_player == null || _player.CameraHandle == null)
                return false;

            if (_grabRequiresRelease || _heldObject != null || _carriedPlayer != null || _activeGrabHoldInteractable != null)
                return false;

            if (_grabCooldownTimer.IsRunning && !_grabCooldownTimer.Expired(Runner))
                return false;

            var cameraHandle = _player.CameraHandle;
            if (!Physics.Raycast(cameraHandle.position, cameraHandle.forward, out var hit, GrabDistance, GrabMask, QueryTriggerInteraction.Ignore))
                return false;

            if (FindInterfaceInParents<IGrabHoldInteractable>(hit.collider.transform) != null)
                return true;

            var grabbable = hit.collider.GetComponentInParent<GrabbablePhysicsObject>();
            if (grabbable != null && grabbable.CanBeGrabbed)
                return true;

            if (!CanCarryPlayers)
                return false;

            var carryable = hit.collider.GetComponentInParent<CarryablePlayer>();
            return carryable != null && carryable != _selfCarryable && carryable.CanBeCarried;
        }
        private void ApplyPlayerWeightEffect(float efficiency)
        {
            var movementMultiplier = SlowMovementWhenHolding ? efficiency : 1f;
            var lookMultiplier = SlowLookWhenHolding ? efficiency : 1f;

            if (_player != null)
                _player.WalkSpeed = _baseWalkSpeed * movementMultiplier;

            if (_player != null && _player.PlayerInput != null)
                _player.PlayerInput.LookSensitivityMultiplier = lookMultiplier;
        }

        private void UpdateStickyHandTarget()
        {
            if (_heldObject != null)
            {
                StickyHandActive = true;
                StickyHandTargetPosition = _heldObject.Center;
                return;
            }

            StickyHandActive = false;
        }

        private float GetHeldWeightEfficiency()
        {
            if (_heldObject == null)
                return 1f;

            var objectEfficiency = _heldObject.WeightEfficiency;
            var interactorEfficiency = Mathf.Lerp(1f, HeavyWeightEfficiency, (_heldObject.EffectiveWeightLevel - 1f) / 7f);
            return Mathf.Min(objectEfficiency, interactorEfficiency);
        }

        private float GetActiveCarryEfficiency()
        {
            if (_carriedPlayer != null)
                return CarriedPlayerEfficiency;

            return GetHeldWeightEfficiency();
        }

        private void ValidateHeldObject()
        {
            if (_heldObject == null)
                return;

            if (_heldObject.IsHeldBy(Object.StateAuthority))
            {
                _holderValidationRetryTimer = default;
                _holderValidationGiveUpTimer = default;
                return;
            }

            if (_holderValidationGraceTimer.IsRunning && !_holderValidationGraceTimer.Expired(Runner))
                return;

            if (!_holderValidationGiveUpTimer.IsRunning)
                _holderValidationGiveUpTimer = TickTimer.CreateFromSeconds(Runner, HolderValidationGiveUpTime);

            RetryUnconfirmedHeldObject();

            if (!_holderValidationGiveUpTimer.Expired(Runner))
                return;

            ReleaseUnconfirmedHeldObject();
        }

        private void RetryUnconfirmedHeldObject()
        {
            if (_heldObject == null)
                return;

            if (_holderValidationRetryTimer.IsRunning && !_holderValidationRetryTimer.Expired(Runner))
                return;

            _heldObject.BeginGrab(Object.StateAuthority);

            var holdEfficiency = WeakenHoldWhenHolding ? GetHeldWeightEfficiency() : 1f;
            _heldObject.HoldAt(Object.StateAuthority, GetHoldPosition(), GetAimRotation(), HoldMoveSpeed * holdEfficiency);
            _holderValidationRetryTimer = TickTimer.CreateFromSeconds(Runner, HolderValidationRetryTime);
        }

        private bool IsHeldObjectConfirmed()
        {
            return _heldObject != null && _heldObject.IsHeldBy(Object.StateAuthority);
        }

        private bool IsHeldObjectAwaitingConfirmation()
        {
            return _heldObject != null && !IsHeldObjectConfirmed();
        }

        private void ReleaseUnconfirmedHeldObject()
        {
            if (_heldObject == null)
                return;

            SetHeldObjectPlayerCollisionIgnored(false);
            _heldObject.EndGrab(Object.StateAuthority, _heldObject.Position, _heldObject.Velocity);
            ClearHeldObjectWithCooldown(GrabCooldownTime);
        }

        private void ValidateCarriedPlayer()
        {
            if (_carriedPlayer == null)
                return;

            if (!_carriedPlayer.IsAlive || !_carriedPlayer.IsCarried || _carriedPlayer.Carrier != Object.StateAuthority)
            {
                if (_holderValidationGraceTimer.IsRunning && !_holderValidationGraceTimer.Expired(Runner))
                    return;

                ClearCarriedPlayerWithCooldown(0f);
            }
        }

        private void TryInteract()
        {
            if (_activeGrabHoldInteractable != null)
                return;

            if (_heldObject != null)
            {
                TryUseHeldObject();
                return;
            }

            if (_carriedPlayer != null)
                return;

            TryInteractWithTarget();
        }

        private void TryUseHeldObject()
        {
            var action = FindInterfaceInChildren<IHeldItemAction>(_heldObject.transform);
            if (action == null)
                return;

            action.UseHeld(Object.StateAuthority, Object);
        }

        private void TryInteractWithTarget()
        {
            var cameraHandle = _player.CameraHandle;
            if (cameraHandle == null)
                return;

            if (!Physics.Raycast(cameraHandle.position, cameraHandle.forward, out var hit, InteractDistance, InteractMask, QueryTriggerInteraction.Ignore))
                return;

            var interactable = FindInterfaceInParents<IWorldInteractable>(hit.collider.transform);
            if (interactable == null)
                return;

            interactable.Interact(Object.StateAuthority, Object);
        }

        private void TryGrabTarget()
        {
            if (_grabRequiresRelease)
                return;

            if (_grabCooldownTimer.IsRunning && !_grabCooldownTimer.Expired(Runner))
                return;

            var cameraHandle = _player.CameraHandle;
            if (cameraHandle == null)
                return;

            if (Physics.Raycast(cameraHandle.position, cameraHandle.forward, out var hit, GrabDistance, GrabMask, QueryTriggerInteraction.Ignore))
            {
                var grabHoldInteractable = FindInterfaceInParents<IGrabHoldInteractable>(hit.collider.transform);
                if (grabHoldInteractable != null && grabHoldInteractable.BeginHold(Object.StateAuthority, Object))
                {
                    _activeGrabHoldInteractable = grabHoldInteractable;
                    _obstructedTicks = 0;
                    _overstretchedTicks = 0;
                    SetHoldingPoseActive(false);
                    return;
                }

                var grabbable = hit.collider.GetComponentInParent<GrabbablePhysicsObject>();
                if (grabbable != null && grabbable.CanBeGrabbed)
                {
                    TryGrabObject(grabbable);
                    return;
                }

                if (CanCarryPlayers)
                {
                    TryCarryPlayer(hit.collider.GetComponentInParent<CarryablePlayer>());
                    if (_carriedPlayer != null)
                        return;
                }
            }

            TryCatchFlyingObject(cameraHandle);
        }

        private void TryCatchFlyingObject(Transform cameraHandle)
        {
            if (!EnableCatchAssist)
                return;

            var candidate = FindCatchCandidate(cameraHandle);
            if (candidate != null)
                TryGrabObject(candidate);
        }

        private GrabbablePhysicsObject FindCatchCandidate(Transform cameraHandle)
        {
            var bestScore = float.PositiveInfinity;
            GrabbablePhysicsObject bestCandidate = null;
            var hits = Physics.SphereCastAll(cameraHandle.position, CatchRadius, cameraHandle.forward, CatchDistance, GrabMask, QueryTriggerInteraction.Ignore);

            foreach (var hit in hits)
            {
                var candidate = hit.collider != null ? hit.collider.GetComponentInParent<GrabbablePhysicsObject>() : null;
                TryScoreCatchCandidate(cameraHandle, candidate, hit.distance, ref bestCandidate, ref bestScore);
            }

            var holdPosition = GetHoldPosition();
            var nearbyColliders = Physics.OverlapSphere(holdPosition, CatchRadius, GrabMask, QueryTriggerInteraction.Ignore);
            foreach (var nearbyCollider in nearbyColliders)
            {
                var candidate = nearbyCollider != null ? nearbyCollider.GetComponentInParent<GrabbablePhysicsObject>() : null;
                var distance = Vector3.Distance(cameraHandle.position, candidate != null ? candidate.Center : holdPosition);
                TryScoreCatchCandidate(cameraHandle, candidate, distance, ref bestCandidate, ref bestScore);
            }

            return bestCandidate;
        }

        private void TryScoreCatchCandidate(Transform cameraHandle, GrabbablePhysicsObject candidate, float hitDistance, ref GrabbablePhysicsObject bestCandidate, ref float bestScore)
        {
            if (candidate == null || !candidate.CanBeGrabbed || candidate.IsHeldBy(Object.StateAuthority))
                return;

            if (candidate.Velocity.sqrMagnitude < CatchMinObjectSpeed * CatchMinObjectSpeed)
                return;

            var toCandidate = candidate.Center - cameraHandle.position;
            if (toCandidate.sqrMagnitude < 0.0001f)
                return;

            var aimDot = Vector3.Dot(cameraHandle.forward, toCandidate.normalized);
            if (aimDot < Mathf.Cos(CatchMaxAngle * Mathf.Deg2Rad))
                return;

            var score = hitDistance + (1f - aimDot) * CatchDistance;
            if (score >= bestScore)
                return;

            bestScore = score;
            bestCandidate = candidate;
        }

        private void TryGrabObject(GrabbablePhysicsObject grabbable)
        {
            if (grabbable.BeginGrab(Object.StateAuthority))
            {
                _heldObject = grabbable;
                SetHeldObjectPlayerCollisionIgnored(true);
                _obstructedTicks = 0;
                _overstretchedTicks = 0;
                _minimumHoldTimer = TickTimer.CreateFromSeconds(Runner, MinimumHoldTime);
                _obstructionGraceTimer = TickTimer.CreateFromSeconds(Runner, ObstructionGraceTime);
                _holderValidationGraceTimer = TickTimer.CreateFromSeconds(Runner, HolderValidationGraceTime);
                _holderValidationRetryTimer = default;
                _holderValidationGiveUpTimer = TickTimer.CreateFromSeconds(Runner, HolderValidationGiveUpTime);
                SetHoldingPoseActive(true);
                ApplyPlayerWeightEffect(GetHeldWeightEfficiency());
            }
        }

        private void TryCarryPlayer(CarryablePlayer target)
        {
            if (target == null || target == _selfCarryable || !target.CanBeCarried)
                return;

            if (target.BeginCarry(Object.StateAuthority))
            {
                _carriedPlayer = target;
                _obstructedTicks = 0;
                _minimumHoldTimer = TickTimer.CreateFromSeconds(Runner, MinimumHoldTime);
                _obstructionGraceTimer = TickTimer.CreateFromSeconds(Runner, ObstructionGraceTime);
                _holderValidationGraceTimer = TickTimer.CreateFromSeconds(Runner, HolderValidationGraceTime);
                SetHoldingPoseActive(true);
                ApplyPlayerWeightEffect(GetActiveCarryEfficiency());
            }
        }

        private void HoldOrReleaseForObstruction()
        {
            var awaitingConfirmation = IsHeldObjectAwaitingConfirmation();

            if (!awaitingConfirmation && IsHeldObjectTooFar())
                _overstretchedTicks++;
            else
                _overstretchedTicks = 0;

            if (_overstretchedTicks >= Mathf.Max(1, HoldStretchReleaseTicks))
            {
                ReleaseHeldObjectAtCurrentPosition(false);
                return;
            }

            if (!awaitingConfirmation && HasObstacleBetweenCameraAndHeldObject())
                _obstructedTicks++;
            else
                _obstructedTicks = 0;

            if (_obstructedTicks >= Mathf.Max(1, ObstructionReleaseTicks))
            {
                ReleaseHeldObjectAtCurrentPosition(false);
                return;
            }

            var holdEfficiency = WeakenHoldWhenHolding ? GetHeldWeightEfficiency() : 1f;
            _heldObject.HoldAt(Object.StateAuthority, GetHoldPosition(), GetAimRotation(), HoldMoveSpeed * holdEfficiency);
        }

        private bool IsHeldObjectTooFar()
        {
            if (_heldObject == null)
                return false;

            if (_minimumHoldTimer.IsRunning && !_minimumHoldTimer.Expired(Runner))
                return false;

            return Vector3.Distance(GetHoldPosition(), _heldObject.Position) > MaxHoldStretchDistance;
        }

        private void HoldOrReleaseCarriedPlayer()
        {
            if (HasObstacleBetweenCameraAndCarriedPlayer())
                _obstructedTicks++;
            else
                _obstructedTicks = 0;

            if (_obstructedTicks >= Mathf.Max(1, ObstructionReleaseTicks))
            {
                ReleaseCarriedPlayer(false);
                return;
            }

            var targetPosition = GetCarriedPlayerPosition();
            _carriedPlayer.MoveCarried(
                Vector3.Lerp(_carriedPlayer.transform.position, targetPosition, Runner.DeltaTime * PlayerCarryMoveSpeed),
                GetCarriedPlayerRotation());
        }

        private bool HasObstacleBetweenCameraAndHeldObject()
        {
            var cameraHandle = _player.CameraHandle;
            if (cameraHandle == null || _heldObject == null)
                return false;

            if (_minimumHoldTimer.IsRunning && !_minimumHoldTimer.Expired(Runner))
                return false;

            if (_obstructionGraceTimer.IsRunning && !_obstructionGraceTimer.Expired(Runner))
                return false;

            var origin = cameraHandle.position;
            var target = _heldObject.Position;
            var toTarget = target - origin;
            var distance = toTarget.magnitude;

            if (distance <= 0.01f)
                return false;

            var direction = toTarget / distance;
            var castDistance = Mathf.Max(0f, distance - ObstructionMargin);
            var hits = UseSphereObstructionProbe && ObstructionProbeRadius > 0f
                ? Physics.SphereCastAll(origin, ObstructionProbeRadius, direction, castDistance, ObstructionMask, QueryTriggerInteraction.Ignore)
                : Physics.RaycastAll(origin, direction, castDistance, ObstructionMask, QueryTriggerInteraction.Ignore);
            var heldHitDistance = distance;
            var obstacleHitDistance = float.PositiveInfinity;

            foreach (var hit in hits)
            {
                if (hit.collider == null)
                    continue;

                if (_heldObject.ContainsCollider(hit.collider))
                {
                    heldHitDistance = Mathf.Min(heldHitDistance, hit.distance);
                    continue;
                }

                if (IsPlayerCollider(hit.collider))
                    continue;

                obstacleHitDistance = Mathf.Min(obstacleHitDistance, hit.distance);
            }

            return obstacleHitDistance + ObstructionMargin < heldHitDistance;
        }

        private bool HasObstacleBetweenCameraAndCarriedPlayer()
        {
            var cameraHandle = _player.CameraHandle;
            if (cameraHandle == null || _carriedPlayer == null)
                return false;

            if (_minimumHoldTimer.IsRunning && !_minimumHoldTimer.Expired(Runner))
                return false;

            if (_obstructionGraceTimer.IsRunning && !_obstructionGraceTimer.Expired(Runner))
                return false;

            var origin = cameraHandle.position;
            var target = _carriedPlayer.transform.position;
            var toTarget = target - origin;
            var distance = toTarget.magnitude;

            if (distance <= 0.01f)
                return false;

            var direction = toTarget / distance;
            var castDistance = Mathf.Max(0f, distance - ObstructionMargin);
            var hits = UseSphereObstructionProbe && ObstructionProbeRadius > 0f
                ? Physics.SphereCastAll(origin, ObstructionProbeRadius, direction, castDistance, ObstructionMask, QueryTriggerInteraction.Ignore)
                : Physics.RaycastAll(origin, direction, castDistance, ObstructionMask, QueryTriggerInteraction.Ignore);
            var carriedHitDistance = distance;
            var obstacleHitDistance = float.PositiveInfinity;
            var carriedColliders = _carriedPlayer.GetComponentsInChildren<Collider>();

            foreach (var hit in hits)
            {
                if (hit.collider == null)
                    continue;

                if (ContainsCollider(carriedColliders, hit.collider))
                {
                    carriedHitDistance = Mathf.Min(carriedHitDistance, hit.distance);
                    continue;
                }

                if (IsPlayerCollider(hit.collider))
                    continue;

                obstacleHitDistance = Mathf.Min(obstacleHitDistance, hit.distance);
            }

            return obstacleHitDistance + ObstructionMargin < carriedHitDistance;
        }

        private bool IsPlayerCollider(Collider candidate)
        {
            if (candidate == null || _playerColliders == null)
                return false;

            foreach (var playerCollider in _playerColliders)
            {
                if (playerCollider == candidate)
                    return true;
            }

            return false;
        }

        private bool ContainsCollider(Collider[] colliders, Collider candidate)
        {
            if (candidate == null || colliders == null)
                return false;

            foreach (var collider in colliders)
            {
                if (collider == candidate)
                    return true;
            }

            return false;
        }

        private void SetHeldObjectPlayerCollisionIgnored(bool ignored)
        {
            if (_heldObject == null)
                return;

            _heldObject.IgnoreCollisionsWith(_playerColliders, ignored);
        }

        private void DropHeldObject()
        {
            if (_heldObject == null)
                return;

            var forward = GetAimForward();
            SetHeldObjectPlayerCollisionIgnored(false);
            _heldObject.EndGrab(Object.StateAuthority, GetHoldPosition() + forward * DropForwardOffset, Vector3.zero);
            ClearHeldObjectWithCooldown(GrabCooldownTime);
        }

        private void DropCarriedPlayer()
        {
            if (_carriedPlayer == null)
                return;

            _carriedPlayer.EndCarry(Object.StateAuthority, GetCarriedPlayerReleasePosition());
            ClearCarriedPlayerWithCooldown(GrabCooldownTime);
        }

        private void ReleaseHeldObjectAtCurrentPosition(bool longCooldown)
        {
            if (_heldObject == null)
                return;

            SetHeldObjectPlayerCollisionIgnored(false);
            _heldObject.EndGrab(Object.StateAuthority, _heldObject.Position, Vector3.zero);
            ClearHeldObjectWithCooldown(longCooldown ? RespawnGrabCooldownTime : GrabCooldownTime);
        }

        private void ReleaseCarriedPlayer(bool longCooldown)
        {
            if (_carriedPlayer == null)
                return;

            _carriedPlayer.EndCarry(Object.StateAuthority, _carriedPlayer.transform.position);
            ClearCarriedPlayerWithCooldown(longCooldown ? RespawnGrabCooldownTime : GrabCooldownTime);
        }

        private void ThrowCarriedPlayer()
        {
            if (_carriedPlayer == null)
                return;

            var forward = GetAimForward();
            _carriedPlayer.EndCarry(
                Object.StateAuthority,
                GetCarriedPlayerPosition(),
                forward * PlayerCarryThrowImpulse * CarriedPlayerEfficiency);
            ClearCarriedPlayerWithCooldown(GrabCooldownTime);
        }

        private void ThrowHeldObject()
        {
            if (_heldObject == null)
                return;

            var forward = GetAimForward();
            var throwEfficiency = WeakenThrowWhenHolding ? GetHeldWeightEfficiency() : 1f;
            SetHeldObjectPlayerCollisionIgnored(false);
            _heldObject.EndGrab(Object.StateAuthority, GetHoldPosition(), forward * ThrowImpulse * throwEfficiency);
            ClearHeldObjectWithCooldown(GrabCooldownTime);
        }

        private void ClearHeldObjectWithCooldown(float cooldown)
        {
            SetHeldObjectPlayerCollisionIgnored(false);
            _heldObject = null;
            StickyHandActive = false;
            _grabRequiresRelease = true;
            _grabHeld = false;
            _obstructedTicks = 0;
            _overstretchedTicks = 0;
            _holderValidationRetryTimer = default;
            _holderValidationGiveUpTimer = default;
            _grabCooldownTimer = cooldown > 0f ? TickTimer.CreateFromSeconds(Runner, cooldown) : default;
            SetHoldingPoseActive(false);
            ApplyPlayerWeightEffect(1f);
        }

        private void ClearCarriedPlayerWithCooldown(float cooldown)
        {
            _carriedPlayer = null;
            StickyHandActive = false;
            _grabRequiresRelease = true;
            _grabHeld = false;
            _obstructedTicks = 0;
            _overstretchedTicks = 0;
            _grabCooldownTimer = cooldown > 0f ? TickTimer.CreateFromSeconds(Runner, cooldown) : default;
            SetHoldingPoseActive(false);
            ApplyPlayerWeightEffect(1f);
        }

        private void ReleaseGrabHoldInteractable()
        {
            if (_activeGrabHoldInteractable == null)
                return;

            _activeGrabHoldInteractable.EndHold(Object.StateAuthority, Object);
            _activeGrabHoldInteractable = null;
            _obstructedTicks = 0;
            _overstretchedTicks = 0;
            SetHoldingPoseActive(false);
            ApplyPlayerWeightEffect(1f);
        }

        private T FindInterfaceInChildren<T>(Transform root) where T : class
        {
            if (root == null)
                return null;

            var behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var behaviour in behaviours)
            {
                if (behaviour is T target)
                    return target;
            }

            return null;
        }

        private T FindInterfaceInParents<T>(Transform root) where T : class
        {
            if (root == null)
                return null;

            var behaviours = root.GetComponentsInParent<MonoBehaviour>(true);
            foreach (var behaviour in behaviours)
            {
                if (behaviour is T target)
                    return target;
            }

            return null;
        }

        private Vector3 GetHoldPosition()
        {
            if (HoldPoint != null)
                return HoldPoint.position;

            var cameraHandle = _player.CameraHandle;
            return cameraHandle != null ? cameraHandle.position + cameraHandle.forward * HoldDistance : transform.position + transform.forward * HoldDistance;
        }

        private Vector3 GetCarriedPlayerPosition()
        {
            var cameraHandle = _player.CameraHandle;
            return cameraHandle != null ? cameraHandle.position + cameraHandle.forward * PlayerCarryDistance : transform.position + transform.forward * PlayerCarryDistance;
        }

        private Vector3 GetCarriedPlayerReleasePosition()
        {
            var forward = GetAimForward();
            return GetCarriedPlayerPosition() + forward * PlayerCarryReleaseForwardOffset;
        }

        private Quaternion GetCarriedPlayerRotation()
        {
            var forward = -GetAimForward();
            forward.y = 0f;

            if (forward.sqrMagnitude < 0.0001f)
                forward = -transform.forward;

            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        private Quaternion GetAimRotation()
        {
            var cameraHandle = _player.CameraHandle;
            return cameraHandle != null ? cameraHandle.rotation : transform.rotation;
        }

        private Vector3 GetAimForward()
        {
            var cameraHandle = _player.CameraHandle;
            return cameraHandle != null ? cameraHandle.forward : transform.forward;
        }
    }
}














