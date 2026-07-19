using Fusion;

namespace FriendSlop
{
    public interface IHeldItemAction
    {
        void UseHeld(PlayerRef interactor, NetworkObject instigator);
    }
}
