using Starter.Shooter;
using UnityEngine;

namespace FriendSlop
{
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(Player))]
    [RequireComponent(typeof(ShooterPlayerObjectInteractor))]
    public sealed class StickyHandToHeldObject : MonoBehaviour
    {
        [Header("Bones")]
        public Transform UpperArm;
        public Transform LowerArm;
        public Transform Hand;
        public bool AutoFindRightArm = true;

        [Header("Blend")]
        [Range(0f, 1f)] public float Weight = 1f;
        public float BlendInSpeed = 18f;
        public float BlendOutSpeed = 24f;

        private Player _player;
        private ShooterPlayerObjectInteractor _interactor;
        private float _currentWeight;
        private Vector3 _lastTargetPosition;

        private void Awake()
        {
            _player = GetComponent<Player>();
            _interactor = GetComponent<ShooterPlayerObjectInteractor>();

            if (AutoFindRightArm)
                FindRightArmBones();
        }

        private void LateUpdate()
        {
            if (UpperArm == null || LowerArm == null || Hand == null)
                return;

            if (_player == null || _player.Object == null || !_player.Object.IsValid)
                return;

            if (_interactor == null || !_interactor.CanReadStickyHandState)
                return;

            var hasTarget = _player.HoldingItemPoseActive && _interactor.StickyHandActive;
            if (hasTarget)
                _lastTargetPosition = _interactor.StickyHandTargetPosition;

            var targetWeight = hasTarget ? Weight : 0f;
            var blendSpeed = targetWeight > _currentWeight ? BlendInSpeed : BlendOutSpeed;
            _currentWeight = Mathf.MoveTowards(_currentWeight, targetWeight, blendSpeed * Time.deltaTime);

            if (_currentWeight <= 0.001f)
            {
                return;
            }

            var animatedUpperRotation = UpperArm.rotation;
            var animatedLowerRotation = LowerArm.rotation;
            var animatedHandRotation = Hand.rotation;

            SolveArmToTarget(_lastTargetPosition);

            UpperArm.rotation = Quaternion.Slerp(animatedUpperRotation, UpperArm.rotation, _currentWeight);
            LowerArm.rotation = Quaternion.Slerp(animatedLowerRotation, LowerArm.rotation, _currentWeight);
            Hand.rotation = Quaternion.Slerp(animatedHandRotation, Hand.rotation, _currentWeight);
        }

        private void FindRightArmBones()
        {
            var root = _player.Animator != null ? _player.Animator.transform : transform;
            var transforms = root.GetComponentsInChildren<Transform>(true);

            UpperArm = UpperArm != null ? UpperArm : FindBone(transforms, "upperarm.r", "rightarm", "right_arm", "rightupperarm");
            LowerArm = LowerArm != null ? LowerArm : FindBone(transforms, "lowerarm.r", "rightforearm", "right_forearm", "rightlowerarm");
            Hand = Hand != null ? Hand : FindBone(transforms, "hand.r", "righthand", "right_hand");
        }

        private Transform FindBone(Transform[] transforms, params string[] names)
        {
            foreach (var candidate in transforms)
            {
                var normalizedName = candidate.name.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");

                foreach (var name in names)
                {
                    var normalizedTarget = name.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
                    if (normalizedName == normalizedTarget || normalizedName.EndsWith(normalizedTarget))
                        return candidate;
                }
            }

            return null;
        }

        private void SolveArmToTarget(Vector3 target)
        {
            var shoulder = UpperArm.position;
            var elbow = LowerArm.position;
            var wrist = Hand.position;

            var upperLength = Vector3.Distance(shoulder, elbow);
            var lowerLength = Vector3.Distance(elbow, wrist);
            var shoulderToTarget = target - shoulder;
            var distance = Mathf.Clamp(shoulderToTarget.magnitude, 0.01f, upperLength + lowerLength * 0.98f);
            var targetDirection = shoulderToTarget.normalized;

            var poleDirection = Vector3.ProjectOnPlane(elbow - shoulder, targetDirection);
            if (poleDirection.sqrMagnitude < 0.0001f)
                poleDirection = Vector3.ProjectOnPlane(transform.right, targetDirection);
            if (poleDirection.sqrMagnitude < 0.0001f)
                poleDirection = Vector3.up;

            poleDirection.Normalize();

            var elbowAlongTarget = (upperLength * upperLength + distance * distance - lowerLength * lowerLength) / (2f * distance);
            var elbowSideDistance = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - elbowAlongTarget * elbowAlongTarget));
            var solvedElbow = shoulder + targetDirection * elbowAlongTarget + poleDirection * elbowSideDistance;

            RotateBoneTowards(UpperArm, LowerArm.position - UpperArm.position, solvedElbow - shoulder);
            RotateBoneTowards(LowerArm, Hand.position - LowerArm.position, target - LowerArm.position);
            RotateBoneTowards(Hand, Hand.forward, target - Hand.position);
        }

        private void RotateBoneTowards(Transform bone, Vector3 currentDirection, Vector3 desiredDirection)
        {
            if (currentDirection.sqrMagnitude < 0.0001f || desiredDirection.sqrMagnitude < 0.0001f)
                return;

            bone.rotation = Quaternion.FromToRotation(currentDirection.normalized, desiredDirection.normalized) * bone.rotation;
        }
    }
}
