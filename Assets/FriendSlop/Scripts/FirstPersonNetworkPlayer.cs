using UnityEngine;
using Fusion;

namespace FriendSlop
{
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(FriendSlopInput))]
    public sealed class FirstPersonNetworkPlayer : NetworkBehaviour
    {
        [Header("References")]
        public Transform CameraPivot;
        public Transform CameraHandle;
        public Transform HoldPoint;
        public LayerMask GrabMask = ~0;
        public LayerMask ObstructionMask = ~0;
        public LayerMask InteractMask = ~0;

        [Header("Look")]
        public float LookSensitivity = 0.18f;
        public float MinPitch = -85f;
        public float MaxPitch = 85f;

        [Header("Movement")]
        public float WalkSpeed = 5f;
        public float SprintSpeed = 7f;
        public float JumpSpeed = 5f;
        public float Gravity = 22f;

        [Header("Interaction")]
        public float GrabDistance = 3f;
        public float HoldMoveSpeed = 24f;
        public float ThrowImpulse = 11f;
        public float DropForwardOffset = 0.6f;
        public float InteractDistance = 3f;
        public bool UseSphereObstructionProbe;
        public float ObstructionProbeRadius = 0f;
        public float ObstructionGraceTime = 0.18f;
        public float ObstructionMargin = 0.05f;
        public float GrabCooldownTime = 0.25f;

        [Networked] private float NetworkPitch { get; set; }
        [Networked] private float NetworkYaw { get; set; }

        private CharacterController _controller;
        private FriendSlopInput _input;
        private Collider[] _playerColliders;
        private Vector3 _verticalVelocity;
        private GrabbablePhysicsObject _heldObject;
        private bool _grabRequiresRelease;
        private TickTimer _obstructionGraceTimer;
        private TickTimer _grabCooldownTimer;
        private PlayerRef InteractionPlayer => Runner != null ? Runner.LocalPlayer : PlayerRef.None;

        public override void Spawned()
        {
            _controller = GetComponent<CharacterController>();
            _input = GetComponent<FriendSlopInput>();
            _playerColliders = GetComponentsInChildren<Collider>();

            if (HasStateAuthority)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                NetworkYaw = transform.eulerAngles.y;
            }
            else
            {
                _input.enabled = false;
            }
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority)
                return;

            var input = _input.Current;
            UpdateLook(input.LookDelta);
            Move(input);
            UpdateGrab(input);
            _input.ResetAfterNetworkTick();
        }

        public override void Render()
        {
            ApplyLookToTransforms();

            if (HasStateAuthority && CameraHandle != null && Camera.main != null)
                Camera.main.transform.SetPositionAndRotation(CameraHandle.position, CameraHandle.rotation);
        }

        private void UpdateLook(Vector2 lookDelta)
        {
            NetworkYaw += lookDelta.x * LookSensitivity;
            NetworkPitch = Mathf.Clamp(NetworkPitch - lookDelta.y * LookSensitivity, MinPitch, MaxPitch);
            ApplyLookToTransforms();
        }

        private void ApplyLookToTransforms()
        {
            transform.rotation = Quaternion.Euler(0f, NetworkYaw, 0f);

            if (CameraPivot != null)
                CameraPivot.localRotation = Quaternion.Euler(NetworkPitch, 0f, 0f);
        }

        private void Move(FriendSlopInputState input)
        {
            var wishDirection = transform.right * input.Move.x + transform.forward * input.Move.y;
            var speed = Input.GetKey(KeyCode.LeftShift) ? SprintSpeed : WalkSpeed;

            if (_controller.isGrounded && _verticalVelocity.y < 0f)
                _verticalVelocity.y = -2f;

            if (_controller.isGrounded && input.JumpPressed)
                _verticalVelocity.y = JumpSpeed;

            _verticalVelocity.y -= Gravity * Runner.DeltaTime;
            var velocity = wishDirection * speed + _verticalVelocity;
            _controller.Move(velocity * Runner.DeltaTime);
        }

        private void UpdateGrab(FriendSlopInputState input)
        {
            if (!input.GrabHeld)
                _grabRequiresRelease = false;

            var grabHeld = input.GrabHeld && !_grabRequiresRelease;

            if (input.ThrowPressed && _heldObject != null)
            {
                ThrowHeldObject();
                return;
            }

            if (input.InteractPressed)
            {
                TryInteract();
                return;
            }

            if (!grabHeld)
            {
                DropHeldObject();
                return;
            }

            if (_heldObject == null)
                TryGrabObject();

            if (_heldObject != null && HoldPoint != null)
            {
                if (HasObstacleBetweenCameraAndHeldObject())
                    ReleaseHeldObjectAtCurrentPosition();
                else
                    _heldObject.HoldAt(HoldPoint.position, HoldPoint.rotation, HoldMoveSpeed);
            }
        }

        private void TryGrabObject()
        {
            if (_grabRequiresRelease)
                return;

            if (_grabCooldownTimer.IsRunning && !_grabCooldownTimer.Expired(Runner))
                return;

            if (CameraHandle == null)
                return;

            if (!Physics.Raycast(CameraHandle.position, CameraHandle.forward, out var hit, GrabDistance, GrabMask, QueryTriggerInteraction.Ignore))
                return;

            var grabbable = hit.collider.GetComponentInParent<GrabbablePhysicsObject>();
            if (grabbable == null || !grabbable.CanBeGrabbed)
                return;

            if (grabbable.BeginGrab(InteractionPlayer))
            {
                _heldObject = grabbable;
                _obstructionGraceTimer = TickTimer.CreateFromSeconds(Runner, ObstructionGraceTime);
            }
        }

        private void TryInteract()
        {
            if (_heldObject != null)
            {
                var heldAction = FindInterfaceInChildren<IHeldItemAction>(_heldObject.transform);
                if (heldAction != null)
                    heldAction.UseHeld(InteractionPlayer, Object);

                return;
            }

            if (CameraHandle == null)
                return;

            if (!Physics.Raycast(CameraHandle.position, CameraHandle.forward, out var hit, InteractDistance, InteractMask, QueryTriggerInteraction.Ignore))
                return;

            var interactable = FindInterfaceInParents<IWorldInteractable>(hit.collider.transform);
            if (interactable != null)
                interactable.Interact(InteractionPlayer, Object);
        }

        private bool HasObstacleBetweenCameraAndHeldObject()
        {
            if (CameraHandle == null || _heldObject == null)
                return false;

            if (_obstructionGraceTimer.IsRunning && !_obstructionGraceTimer.Expired(Runner))
                return false;

            var origin = CameraHandle.position;
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

        private void DropHeldObject()
        {
            if (_heldObject == null)
                return;

            var dropPosition = HoldPoint != null ? HoldPoint.position + transform.forward * DropForwardOffset : transform.position + transform.forward;
            _heldObject.EndGrab(InteractionPlayer, dropPosition, Vector3.zero);
            _heldObject = null;
            _grabRequiresRelease = true;
            _grabCooldownTimer = TickTimer.CreateFromSeconds(Runner, GrabCooldownTime);
        }

        private void ReleaseHeldObjectAtCurrentPosition()
        {
            if (_heldObject == null)
                return;

            _heldObject.EndGrab(InteractionPlayer, _heldObject.Position, Vector3.zero);
            _heldObject = null;
            _grabRequiresRelease = true;
            _grabCooldownTimer = TickTimer.CreateFromSeconds(Runner, GrabCooldownTime);
        }

        private void ThrowHeldObject()
        {
            if (_heldObject == null)
                return;

            var throwDirection = CameraHandle != null ? CameraHandle.forward : transform.forward;
            var throwPosition = HoldPoint != null ? HoldPoint.position : transform.position + throwDirection;
            _heldObject.EndGrab(InteractionPlayer, throwPosition, throwDirection * ThrowImpulse);
            _heldObject = null;
            _grabRequiresRelease = true;
            _grabCooldownTimer = TickTimer.CreateFromSeconds(Runner, GrabCooldownTime);
        }

    }
}











