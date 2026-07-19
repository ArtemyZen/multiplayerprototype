using Fusion;
using Fusion.Addons.SimpleKCC;
using Starter.Shooter;
using UnityEngine;

namespace FriendSlop
{
    [RequireComponent(typeof(Player))]
    public sealed class CarryablePlayer : NetworkBehaviour
    {
        public float CarryHeartbeatTimeout = 0.75f;
        public float CarryThrowDuration = 0.65f;
        public float CarryThrowGravity = 18f;
        public float CarryThrowDrag = 4f;

        [Networked] public NetworkBool IsCarried { get; private set; }
        [Networked] public PlayerRef Carrier { get; private set; }
        [Networked] private TickTimer CarryHeartbeatTimer { get; set; }
        [Networked] private TickTimer CarryThrowTimer { get; set; }
        [Networked] private Vector3 CarryThrowVelocity { get; set; }

        private Player _player;

        public bool IsAlive => _player != null && _player.Health != null && _player.Health.IsAlive;
        public bool CanBeCarried => IsCarried == false && IsAlive;
        public bool IsCarryThrown => CarryThrowTimer.IsRunning;

        public override void Spawned()
        {
            _player = GetComponent<Player>();
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority)
                return;

            if (IsCarried)
            {
                if (!IsAlive || CarryHeartbeatTimer.Expired(Runner))
                    EndCarryInternal(transform.position, Vector3.zero);

                return;
            }

            if (CarryThrowTimer.IsRunning)
                MoveThrow();
        }

        public bool BeginCarry(PlayerRef carrier)
        {
            if (!HasStateAuthority)
            {
                RPC_BeginCarry(carrier);
                return true;
            }

            return BeginCarryInternal(carrier);
        }

        public void MoveCarried(Vector3 position, Quaternion rotation)
        {
            if (!HasStateAuthority)
            {
                RPC_MoveCarried(position, rotation);
                return;
            }

            MoveCarriedInternal(position, rotation);
        }

        public void EndCarry(PlayerRef carrier, Vector3 releasePosition)
        {
            EndCarry(carrier, releasePosition, Vector3.zero);
        }

        public void EndCarry(PlayerRef carrier, Vector3 releasePosition, Vector3 releaseVelocity)
        {
            if (!HasStateAuthority)
            {
                RPC_EndCarry(carrier, releasePosition, releaseVelocity);
                return;
            }

            EndCarryInternal(carrier, releasePosition, releaseVelocity);
        }

        private bool BeginCarryInternal(PlayerRef carrier)
        {
            if (carrier == PlayerRef.None || IsCarried)
                return false;

            Carrier = carrier;
            IsCarried = true;
            CarryHeartbeatTimer = TickTimer.CreateFromSeconds(Runner, CarryHeartbeatTimeout);
            CarryThrowTimer = default;
            CarryThrowVelocity = Vector3.zero;
            return true;
        }

        private void MoveCarriedInternal(Vector3 position, Quaternion rotation)
        {
            if (!IsCarried || _player == null || _player.KCC == null)
                return;

            _player.KCC.SetPosition(position);
            _player.KCC.SetLookRotation(0f, rotation.eulerAngles.y);
            CarryHeartbeatTimer = TickTimer.CreateFromSeconds(Runner, CarryHeartbeatTimeout);
        }

        private void EndCarryInternal(PlayerRef carrier, Vector3 releasePosition, Vector3 releaseVelocity)
        {
            EndCarryInternal(releasePosition, releaseVelocity);
        }

        private void EndCarryInternal(Vector3 releasePosition, Vector3 releaseVelocity)
        {
            if (!IsCarried)
                return;

            Carrier = PlayerRef.None;
            IsCarried = false;
            CarryHeartbeatTimer = default;

            if (_player != null && _player.KCC != null)
                _player.KCC.SetPosition(releasePosition);

            CarryThrowVelocity = releaseVelocity;
            CarryThrowTimer = releaseVelocity.sqrMagnitude > 0.01f
                ? TickTimer.CreateFromSeconds(Runner, CarryThrowDuration)
                : default;
        }

        private void MoveThrow()
        {
            if (_player == null || _player.KCC == null || !IsAlive)
            {
                CarryThrowTimer = default;
                CarryThrowVelocity = Vector3.zero;
                return;
            }

            if (CarryThrowTimer.Expired(Runner) || CarryThrowVelocity.sqrMagnitude < 0.01f)
            {
                CarryThrowTimer = default;
                CarryThrowVelocity = Vector3.zero;
                return;
            }

            _player.KCC.Move(CarryThrowVelocity, 0f);
            CarryThrowVelocity += Vector3.down * CarryThrowGravity * Runner.DeltaTime;
            CarryThrowVelocity = Vector3.Lerp(CarryThrowVelocity, Vector3.zero, CarryThrowDrag * Runner.DeltaTime);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RPC_BeginCarry(PlayerRef carrier)
        {
            BeginCarryInternal(carrier);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RPC_MoveCarried(Vector3 position, Quaternion rotation)
        {
            MoveCarriedInternal(position, rotation);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RPC_EndCarry(PlayerRef carrier, Vector3 releasePosition, Vector3 releaseVelocity)
        {
            EndCarryInternal(carrier, releasePosition, releaseVelocity);
        }
    }
}
