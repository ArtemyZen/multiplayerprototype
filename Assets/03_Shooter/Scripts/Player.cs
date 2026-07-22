using UnityEngine;
using Fusion;
using Fusion.Addons.SimpleKCC;
using UnityEngine.Rendering;
using FriendSlop;

namespace Starter.Shooter
{
	/// <summary>
	/// Main player scrip - controls player movement and animations.
	/// </summary>
	public sealed class Player : NetworkBehaviour
	{
		[Header("References")]
		public Health Health;
		public SimpleKCC KCC;
		public PlayerInput PlayerInput;
		public Animator Animator;
		public Transform CameraPivot;
		public Transform CameraHandle;
		public Transform ScalingRoot;
		public UINameplate Nameplate;
		public Collider Hitbox;
		public Renderer[] HeadRenderers;
		public GameObject[] FirstPersonOverlayObjects;

		[Header("Movement Setup")]
		public float WalkSpeed = 2f;
		public float JumpImpulse = 10f;
		public float JumpInputCooldown = 0.15f;
		public float UpGravity = 25f;
		public float DownGravity = 40f;

		[Header("Movement Accelerations")]
		public float GroundAcceleration = 55f;
		public float GroundDeceleration = 25f;
		public float AirAcceleration = 25f;
		public float AirDeceleration = 1.3f;

		[Header("Fire Setup")]
		public LayerMask HitMask;
		public GameObject ImpactPrefab;
		public ParticleSystem MuzzleParticle;

		[Header("Animation Setup")]
		public Transform ChestTargetPosition;
		public Transform ChestBone;
		public bool AimBodyWithCameraAlways = true;
		[Range(0f, 1f)] public float EmptyHandsBodyAimBlend = 0.2f;

		[Header("Sounds")]
		public AudioSource FireSound;
		public AudioSource FootstepSound;
		public AudioClip JumpAudioClip;
		public AudioClip LandAudioClip;

		[Header("VFX")]
		public ParticleSystem DustParticles;

		[Networked, HideInInspector, Capacity(24), OnChangedRender(nameof(OnNicknameChanged))]
		public string Nickname { get; set; }
		[Networked, HideInInspector]
		public int ChickenKills { get; set; }
		[Networked, HideInInspector]
		public NetworkBool HoldingItemPoseActive { get; set; }

		[Networked, OnChangedRender(nameof(OnJumpingChanged))]
		private NetworkBool _isJumping { get; set; }
		[Networked]
		private Vector3 _hitPosition { get; set; }
		[Networked]
		private Vector3 _hitNormal { get; set; }
		[Networked]
		private int _fireCount { get; set; }

		// Animation IDs
		private int _animIDSpeedX;
		private int _animIDSpeedZ;
		private int _animIDMoveSpeedZ;
		private int _animIDGrounded;
		private int _animIDPitch;
		private int _animIDShoot;
		private int _animLayerAim;
		private int _animLayerWeapon;

		private Vector3 _moveVelocity;
		private int _visibleFireCount;
		private CarryablePlayer _carryablePlayer;
		private ShadowCastingMode[] _headRendererInitialShadowModes;
		private bool _localHeadVisible;
		private bool _localHeadVisibilityApplied;
		private GameplayInput _inputAuthorityFallback;
		private int _lastProcessedJumpSequence;

		private GameManager _gameManager;
		private bool CanReadNetworkedState => Object != null && Object.IsValid;

		public override void Spawned()
		{
			if (HasStateAuthority)
			{
				_gameManager = FindObjectOfType<GameManager>();
			}

			if (HasInputAuthority)
			{
				Object.EnableInterpolation = false;
				FindObjectOfType<GameManager>()?.RegisterLocalPlayer(this);

				// Set player nickname that is saved in UIGameMenu
				SetNickname(PlayerPrefs.GetString("PlayerName"));
			}

			// In case the nickname is already changed,
			// we need to trigger the change manually
			OnNicknameChanged();

			// Reset visible fire count
			_visibleFireCount = _fireCount;

			if (HasInputAuthority)
			{
				CacheHeadRendererModes();

				// For input authority deactivate head renderers so they are not obstructing the view
				SetLocalHeadVisible(false);

				// Some objects (e.g. weapon) are renderer with secondary Overlay camera.
				// This prevents weapon clipping into the wall when close to the wall.
				int overlayLayer = LayerMask.NameToLayer("FirstPersonOverlay");
				if (overlayLayer >= 0)
				{
					for (int i = 0; i < FirstPersonOverlayObjects.Length; i++)
					{
						if (FirstPersonOverlayObjects[i] != null)
							FirstPersonOverlayObjects[i].layer = overlayLayer;
					}
				}

				// Look rotation interpolation is skipped for local player.
				// Look rotation is set manually in Render.
				KCC.Settings.ForcePredictedLookRotation = true;
			}
		}

		public override void FixedUpdateNetwork()
		{
			bool isCarryControlled = _carryablePlayer != null && (_carryablePlayer.IsCarried || _carryablePlayer.IsCarryThrown);
			bool isExternallyControlled = isCarryControlled;
			if (HasInputAuthority)
				PlayerInput.InputBlocked = isExternallyControlled;

			if (HasStateAuthority && KCC.Position.y < -15f)
			{
				// Player fell, let's kill him
				Health.TakeHit(1000);
			}

			if (HasStateAuthority && Health.IsFinished)
			{
				// Player is dead and death timer is finished, let's respawn the player
				Respawn(_gameManager.GetSpawnPosition());
			}

			if (isExternallyControlled)
			{
				_moveVelocity = Vector3.zero;
				_isJumping = false;
				KCC.SetActive(Health.IsAlive);
				return;
			}

			var input = default(GameplayInput);
			if (Health.IsAlive && (HasStateAuthority || HasInputAuthority))
			{
				if (!GetInput(out input))
				{
					if (HasInputAuthority)
					{
						TryGetLocalPredictionInput(out input);
					}
					else if (HasStateAuthority)
					{
						input = _inputAuthorityFallback;
					}
				}

				if (HasInputAuthority && !HasStateAuthority)
				{
					RPC_SubmitInputAuthorityFallback(input.LookRotation, input.MoveDirection, input.JumpSequence, input.Fire);
				}

				ProcessInput(input, HasStateAuthority);
			}

			if (HasStateAuthority && KCC.IsGrounded)
			{
				// Stop jumping
				_isJumping = false;
			}

			KCC.SetActive(Health.IsAlive);
		}

		public override void Render()
		{
			bool isCarryControlled = _carryablePlayer != null && (_carryablePlayer.IsCarried || _carryablePlayer.IsCarryThrown);
			bool isExternallyControlled = isCarryControlled;
			PlayerInput.InputBlocked = isExternallyControlled;

			if (HasInputAuthority && !isExternallyControlled)
			{
				// Set look rotation for Render.
				KCC.SetLookRotation(PlayerInput.CurrentInput.LookRotation, -90f, 90f);
			}

			// Transform velocity vector to local space.
			var moveSpeed = transform.InverseTransformVector(KCC.RealVelocity);

			Animator.SetFloat(_animIDSpeedX, moveSpeed.x, 0.1f, Time.deltaTime);
			Animator.SetFloat(_animIDSpeedZ, moveSpeed.z, 0.1f, Time.deltaTime);
			Animator.SetBool(_animIDGrounded, KCC.IsGrounded);
			Animator.SetFloat(_animIDPitch, KCC.GetLookRotation(true, false).x, 0.02f, Time.deltaTime);
			UpdateHoldingAnimationLayers();

			FootstepSound.enabled = KCC.IsGrounded && KCC.RealSpeed > 1f;
			ScalingRoot.localScale = Vector3.Lerp(ScalingRoot.localScale, Vector3.one, Time.deltaTime * 8f);

			var emission = DustParticles.emission;
			emission.enabled = KCC.IsGrounded && KCC.RealSpeed > 1f;

			ShowFireEffects();

			// Disable hits when player is dead
			Hitbox.enabled = Health.IsAlive;
		}

		private void Awake()
		{
			AssignAnimationIDs();
			_carryablePlayer = GetComponent<CarryablePlayer>();
		}

		private void LateUpdate()
		{
			UpdateHoldingAnimationLayers();

			if (!CanReadNetworkedState)
				return;

			if (Health.IsAlive == false)
				return;

			// Update camera pivot (influences ChestIK)
			// (KCC look rotation is set earlier in Render)
			var pitchRotation = KCC.GetLookRotation(true, false);
			CameraPivot.localRotation = Quaternion.Euler(pitchRotation);

			bool holdingItemPoseActive = HoldingItemPoseActive;
			if (holdingItemPoseActive || AimBodyWithCameraAlways)
			{
				// Dummy IK solution, we are snapping chest bone to prepared ChestTargetPosition position
				// Lerping blends the fixed position with little bit of animation position.
				float blendAmount = holdingItemPoseActive ? (HasInputAuthority ? 0.05f : 0.2f) : EmptyHandsBodyAimBlend;
				ChestBone.position = Vector3.Lerp(ChestTargetPosition.position, ChestBone.position, blendAmount);
				ChestBone.rotation = Quaternion.Lerp(ChestTargetPosition.rotation, ChestBone.rotation, blendAmount);
			}

			// Only local player needs to update the camera
			if (HasInputAuthority)
			{
				// Transfer properties from camera handle to Main Camera.
				var mainCamera = Camera.main;
				if (mainCamera != null)
					mainCamera.transform.SetPositionAndRotation(CameraHandle.position, CameraHandle.rotation);
			}
		}

		private void CacheHeadRendererModes()
		{
			if (_headRendererInitialShadowModes != null || HeadRenderers == null)
				return;

			_headRendererInitialShadowModes = new ShadowCastingMode[HeadRenderers.Length];
			for (int i = 0; i < HeadRenderers.Length; i++)
			{
				_headRendererInitialShadowModes[i] = HeadRenderers[i] != null
					? HeadRenderers[i].shadowCastingMode
					: ShadowCastingMode.On;
			}
		}

		private void SetLocalHeadVisible(bool visible)
		{
			if (!HasInputAuthority || HeadRenderers == null)
				return;

			if (_localHeadVisibilityApplied && _localHeadVisible == visible && _headRendererInitialShadowModes != null)
				return;

			CacheHeadRendererModes();
			_localHeadVisible = visible;
			_localHeadVisibilityApplied = true;

			for (int i = 0; i < HeadRenderers.Length; i++)
			{
				var headRenderer = HeadRenderers[i];
				if (headRenderer == null)
					continue;

				headRenderer.shadowCastingMode = visible && i < _headRendererInitialShadowModes.Length
					? _headRendererInitialShadowModes[i]
					: ShadowCastingMode.ShadowsOnly;
			}
		}

		private bool TryGetLocalPredictionInput(out GameplayInput input)
		{
			input = PlayerInput.LastSubmittedInput;
			if (HasAnyInput(input))
				return true;

			input = PlayerInput.CurrentInput;
			return HasAnyInput(input);
		}

		private static bool HasAnyInput(GameplayInput input)
		{
			return input.LookRotation != default ||
			       input.MoveDirection != default ||
			       input.JumpSequence != 0 ||
			       input.Fire;
		}

		private void ProcessInput(GameplayInput input, bool updateNetworkState = true)
		{
			KCC.SetLookRotation(input.LookRotation, -90f, 90f);

			// It feels better when player falls quicker
			KCC.SetGravity(KCC.RealVelocity.y >= 0f ? UpGravity : DownGravity);

			// Calculate correct move direction from input (rotated based on latest KCC rotation)
			var moveDirection = KCC.TransformRotation * new Vector3(input.MoveDirection.x, 0f, input.MoveDirection.y);
			var desiredMoveVelocity = moveDirection * WalkSpeed;

			float acceleration;
			if (desiredMoveVelocity == Vector3.zero)
			{
				// No desired move velocity - we are stopping.
				acceleration = KCC.IsGrounded == true ? GroundDeceleration : AirDeceleration;
			}
			else
			{
				acceleration = KCC.IsGrounded == true ? GroundAcceleration : AirAcceleration;
			}

			_moveVelocity = Vector3.Lerp(_moveVelocity, desiredMoveVelocity, acceleration * Runner.DeltaTime);
			float jumpImpulse = 0f;

			var hasNewJump = input.JumpSequence != 0 && input.JumpSequence != _lastProcessedJumpSequence;
			if (KCC.IsGrounded && hasNewJump)
			{
				// Set world space jump vector
				jumpImpulse = JumpImpulse;
				_lastProcessedJumpSequence = input.JumpSequence;
				if (updateNetworkState)
					_isJumping = true;
			}

			KCC.Move(_moveVelocity, jumpImpulse);

			// Update camera pivot so fire transform (CameraHandle) is correct
			var pitchRotation = KCC.GetLookRotation(true, false);
			CameraPivot.localRotation = Quaternion.Euler(pitchRotation);

			if (updateNetworkState && input.Fire)
			{
				Fire();
			}
		}

		private void Fire()
		{
			// Clear hit position in case nothing will be hit
			_hitPosition = Vector3.zero;

			// Whole projectile path and effects are immediately processed (= hitscan projectile)
			if (Physics.Raycast(CameraHandle.position, CameraHandle.forward, out var hitInfo, 200f, HitMask))
			{
				// Deal damage
				var health = hitInfo.collider != null ? hitInfo.collider.GetComponentInParent<Health>() : null;
				if (health != null)
				{
					health.Killed = OnEnemyKilled;
					health.TakeHit(1, true);
				}

				// Save hit point to correctly show bullet path on all clients.
				// This however works only for single projectile per FUN and with higher fire cadence
				// some projectiles might not be fired on proxies because we save only the position
				// of the LAST hit.
				_hitPosition = hitInfo.point;
				_hitNormal = hitInfo.normal;
			}

			// In this example projectile count property (fire count) is used not only for weapon fire effects
			// but to spawn the projectile visuals themselves.
			_fireCount++;
		}

		private void Respawn(Vector3 position)
		{
			ChickenKills = 0;
			Health.Revive();

			KCC.SetPosition(position);
			KCC.SetLookRotation(0f, 0f);

			_moveVelocity = Vector3.zero;
		}

		private void OnEnemyKilled(Health enemyHealth)
		{
			// Killing chicken grants 1 point, killing other player has -10 points penalty.
			ChickenKills += enemyHealth.GetComponent<Chicken>() != null ? 1 : -10;
		}

		private void ShowFireEffects()
		{
			// Notice we are not using OnChangedRender for fireCount property but instead
			// we are checking against a local variable and show fire effects only when visible
			// fire count is SMALLER. This prevents triggering false fire effects when
			// local player mispredicted fire (e.g. input got lost) and fireCount property got decreased.
			if (_visibleFireCount < _fireCount)
			{
				FireSound.PlayOneShot(FireSound.clip);
				MuzzleParticle.Play();
				Animator.SetTrigger(_animIDShoot);

				if (_hitPosition != Vector3.zero)
				{
					// Impact gets destroyed automatically with DestroyAfter script
					Instantiate(ImpactPrefab, _hitPosition, Quaternion.LookRotation(_hitNormal));
				}
			}

			_visibleFireCount = _fireCount;
		}

		private void AssignAnimationIDs()
		{
			_animIDSpeedX = Animator.StringToHash("SpeedX");
			_animIDSpeedZ = Animator.StringToHash("SpeedZ");
			_animIDGrounded = Animator.StringToHash("Grounded");
			_animIDPitch = Animator.StringToHash("Pitch");
			_animIDShoot = Animator.StringToHash("Shoot");
			_animLayerAim = Animator.GetLayerIndex("Aim");
			_animLayerWeapon = Animator.GetLayerIndex("Weapon");
		}

		private void UpdateHoldingAnimationLayers()
		{
			float targetWeight = CanReadNetworkedState && HoldingItemPoseActive ? 1f : 0f;

			if (_animLayerAim >= 0)
				Animator.SetLayerWeight(_animLayerAim, targetWeight);

			if (_animLayerWeapon >= 0)
				Animator.SetLayerWeight(_animLayerWeapon, targetWeight);
		}

		private void OnJumpingChanged()
		{
			if (_isJumping)
			{
				AudioSource.PlayClipAtPoint(JumpAudioClip, KCC.Position, 0.5f);
			}
			else
			{
				AudioSource.PlayClipAtPoint(LandAudioClip, KCC.Position, 1f);
			}

			if (HasInputAuthority == false)
			{
				ScalingRoot.localScale = _isJumping ? new Vector3(0.5f, 1.5f, 0.5f) : new Vector3(1.25f, 0.75f, 1.25f);
			}
		}

		private void OnNicknameChanged()
		{
			if (HasInputAuthority)
				return; // Do not show nickname for local player

			Nameplate.SetNickname(Nickname);
		}

		private void SetNickname(string nickname)
		{
			if (HasStateAuthority)
				Nickname = nickname;
			else
				RPC_SetNickname(nickname);
		}

		[Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
		private void RPC_SetNickname(string nickname)
		{
			Nickname = nickname;
		}

		[Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority, Channel = RpcChannel.Unreliable)]
		private void RPC_SubmitInputAuthorityFallback(Vector2 lookRotation, Vector2 moveDirection, int jumpSequence, NetworkBool fire)
		{
			_inputAuthorityFallback.LookRotation = lookRotation;
			_inputAuthorityFallback.MoveDirection = moveDirection;
			_inputAuthorityFallback.Jump = jumpSequence != 0;
			_inputAuthorityFallback.JumpSequence = jumpSequence;
			_inputAuthorityFallback.Fire = fire;
		}

	}
}
