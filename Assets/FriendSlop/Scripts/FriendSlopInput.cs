using UnityEngine;

namespace FriendSlop
{
    public struct FriendSlopInputState
    {
        public Vector2 LookDelta;
        public Vector2 Move;
        public bool JumpPressed;
        public bool GrabHeld;
        public bool ThrowPressed;
        public bool InteractPressed;
    }

    public sealed class FriendSlopInput : MonoBehaviour
    {
        public FriendSlopInputState Current => _current;

        [Header("Input")]
        public KeyCode GrabKey = KeyCode.Mouse1;
        public KeyCode ThrowKey = KeyCode.Mouse0;
        public KeyCode InteractKey = KeyCode.F;

        private FriendSlopInputState _current;

        public void ResetAfterNetworkTick()
        {
            _current.LookDelta = Vector2.zero;
            _current.Move = Vector2.zero;
            _current.JumpPressed = false;
            _current.ThrowPressed = false;
            _current.InteractPressed = false;
        }

        private void Update()
        {
            if (Cursor.lockState != CursorLockMode.Locked)
                return;

            _current.LookDelta += new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
            _current.Move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")).normalized;
            _current.JumpPressed |= Input.GetButtonDown("Jump");
            _current.GrabHeld = Input.GetKey(GrabKey);
            _current.ThrowPressed |= Input.GetKeyDown(ThrowKey);
            _current.InteractPressed |= Input.GetKeyDown(InteractKey);
        }
    }
}
