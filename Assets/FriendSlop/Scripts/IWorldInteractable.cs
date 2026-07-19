using Fusion;

namespace FriendSlop
{
    public interface IWorldInteractable
    {
        void Interact(PlayerRef interactor, NetworkObject instigator);
    }
}
