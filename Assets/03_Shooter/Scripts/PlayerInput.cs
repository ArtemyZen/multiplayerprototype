using UnityEngine;

using Fusion;

namespace Starter.Shooter
{
	/// <summary>
	/// Structure holding player input.
	/// </summary>
	public struct GameplayInput : INetworkInput
	{
		public Vector2 LookRotation;
		public Vector2 MoveDirection;
		public bool Jump;
		public int JumpSequence;
		public bool Fire;
	}

	/// <summary>
	/// PlayerInput handles accumulating player input from Unity.
	/// </summary>
	public sealed class PlayerInput : MonoBehaviour
	{
		public GameplayInput CurrentInput => _input;
		public GameplayInput LastSubmittedInput { get; private set; }
		public int LastSubmittedInputFrame { get; private set; } = -1;
		public bool InputBlocked { get; set; }
		public bool FireBlocked { get; set; }
		public bool ShootingEnabled { get; set; } = false;
		public float LookSensitivityMultiplier { get; set; } = 1f;
		private GameplayInput _input;
		private int _jumpSequence;

		public void ResetInput()
		{
			// Reset input after it was used to detect changes correctly again
			_input.MoveDirection = default;
			_input.Jump = false;
			_input.JumpSequence = 0;
			_input.Fire = false;
		}

		public GameplayInput ConsumeNetworkInput()
		{
			LastSubmittedInput = _input;
			LastSubmittedInputFrame = Time.frameCount;
			ResetInput();
			return LastSubmittedInput;
		}

		private void Update()
		{
			// Accumulate input only if the cursor is locked.
			if (Cursor.lockState != CursorLockMode.Locked)
				return;

			if (InputBlocked)
			{
				_input = default;
				return;
			}

			// Accumulate input from Keyboard/Mouse. Input accumulation is mandatory (at least for look rotation here) as Update can be
			// called multiple times before next FixedUpdateNetwork is called - common if rendering speed is faster than Fusion simulation.

			var lookScale = Mathf.Max(0.01f, LookSensitivityMultiplier);
			_input.LookRotation += new Vector2(-Input.GetAxisRaw("Mouse Y"), Input.GetAxisRaw("Mouse X")) * lookScale;

			var moveDirection = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
			_input.MoveDirection = moveDirection.normalized;

			_input.Fire |= ShootingEnabled && FireBlocked == false && Input.GetButtonDown("Fire1");
			if (Input.GetButtonDown("Jump"))
			{
				_jumpSequence++;
				_input.Jump = true;
				_input.JumpSequence = _jumpSequence;
			}
		}
	}
}



