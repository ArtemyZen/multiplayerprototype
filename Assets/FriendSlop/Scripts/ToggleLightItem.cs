using Fusion;
using UnityEngine;

namespace FriendSlop
{
    [RequireComponent(typeof(NetworkObject))]
    public sealed class ToggleLightItem : NetworkBehaviour, IWorldInteractable, IHeldItemAction
    {
        [Header("State")]
        public bool StartOn = true;

        [Header("Light")]
        public Light[] Lights;
        public Renderer[] EmissiveRenderers;
        public Color EmissionOnColor = Color.white;
        public Color EmissionOffColor = Color.black;
        public float EmissionIntensity = 2f;

        [Networked] private NetworkBool IsOn { get; set; }

        private MaterialPropertyBlock _propertyBlock;
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        public override void Spawned()
        {
            CacheReferences();

            if (HasStateAuthority)
                IsOn = StartOn;

            ApplyState();
        }

        public override void Render()
        {
            ApplyState();
        }

        public void Interact(PlayerRef interactor, NetworkObject instigator)
        {
            Toggle(interactor);
        }

        public void UseHeld(PlayerRef interactor, NetworkObject instigator)
        {
            Toggle(interactor);
        }

        private void Toggle(PlayerRef interactor)
        {
            if (!HasStateAuthority)
            {
                RPC_Toggle(interactor);
                return;
            }

            ToggleInternal();
        }

        private void ToggleInternal()
        {
            IsOn = !IsOn;
            ApplyState();
        }

        private void CacheReferences()
        {
            if (Lights == null || Lights.Length == 0)
                Lights = GetComponentsInChildren<Light>(true);

            if (EmissiveRenderers == null || EmissiveRenderers.Length == 0)
                EmissiveRenderers = GetComponentsInChildren<Renderer>(true);

            if (_propertyBlock == null)
                _propertyBlock = new MaterialPropertyBlock();
        }

        private void ApplyState()
        {
            CacheReferences();

            foreach (var itemLight in Lights)
            {
                if (itemLight != null)
                    itemLight.enabled = IsOn;
            }

            var emissionColor = IsOn ? EmissionOnColor * EmissionIntensity : EmissionOffColor;
            foreach (var itemRenderer in EmissiveRenderers)
            {
                if (itemRenderer == null)
                    continue;

                itemRenderer.GetPropertyBlock(_propertyBlock);
                _propertyBlock.SetColor(EmissionColorId, emissionColor);
                itemRenderer.SetPropertyBlock(_propertyBlock);
            }
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RPC_Toggle(PlayerRef interactor)
        {
            ToggleInternal();
        }
    }
}
