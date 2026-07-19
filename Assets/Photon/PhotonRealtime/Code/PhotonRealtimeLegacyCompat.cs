// ----------------------------------------------------------------------------
// Compatibility shims for mixed Photon Realtime v4/v5 source imports.
// Keep this file small: it only restores legacy types that older PUN/Voice code
// still references while the v5 split Realtime sources provide the real API.
// ----------------------------------------------------------------------------

namespace Photon.Realtime
{
    using System;
    using System.Collections.Generic;
    using Photon.Client;

    /// <summary>Legacy name for RealtimeClient kept for older integrations.</summary>
    [Obsolete("Use RealtimeClient instead.")]
    public class LoadBalancingClient : RealtimeClient
    {
        public LoadBalancingClient(ConnectionProtocol protocol = ConnectionProtocol.Udp) : base(protocol)
        {
        }
    }

    /// <summary>Legacy name kept for editor/debug APIs that still mention LoadBalancingPeer.</summary>
    [Obsolete("Use PhotonPeer instead.")]
    public class LoadBalancingPeer : PhotonPeer
    {
        public LoadBalancingPeer(ConnectionProtocol protocolType) : base(protocolType)
        {
        }

        public LoadBalancingPeer(IPhotonPeerListener listener, ConnectionProtocol protocolType) : base(listener, protocolType)
        {
        }
    }

    /// <summary>Legacy port override struct used by LoadBalancingClient.</summary>
    public struct PhotonPortDefinition
    {
        public static readonly PhotonPortDefinition AlternativeUdpPorts = new PhotonPortDefinition()
        {
            NameServerPort = 27000,
            MasterServerPort = 27001,
            GameServerPort = 27002
        };

        public ushort NameServerPort;
        public ushort MasterServerPort;
        public ushort GameServerPort;
    }

    /// <summary>Legacy options object used by old PUN/Voice wrappers around OpRaiseEvent.</summary>
    public class RaiseEventOptions
    {
        public static readonly RaiseEventOptions Default = new RaiseEventOptions();

        public EventCaching CachingOption;
        public byte InterestGroup;
        public int[] TargetActors;
        public ReceiverGroup Receivers;

        [Obsolete("Not used where SendOptions are a parameter too. Use SendOptions.Channel instead.")]
        public byte SequenceChannel;

        public WebFlags Flags = WebFlags.Default;
    }

    /// <summary>Callback for legacy WebRpc responses.</summary>
    public interface IWebRpcCallback
    {
        void OnWebRpcResponse(OperationResponse response);
    }

    internal class WebRpcCallbacksContainer : List<IWebRpcCallback>, IWebRpcCallback
    {
        private readonly LoadBalancingClient client;

        public WebRpcCallbacksContainer(LoadBalancingClient client)
        {
            this.client = client;
        }

        public void OnWebRpcResponse(OperationResponse response)
        {
            foreach (IWebRpcCallback target in this)
            {
                target.OnWebRpcResponse(response);
            }
        }
    }
}
