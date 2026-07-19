using Fusion;
using UnityEngine;

namespace FriendSlop
{
    public interface IGrabHoldInteractable
    {
        bool BeginHold(PlayerRef interactor, NetworkObject instigator);
        void UpdateHold(PlayerRef interactor, NetworkObject instigator, Vector2 moveInput);
        void EndHold(PlayerRef interactor, NetworkObject instigator);
    }
}
