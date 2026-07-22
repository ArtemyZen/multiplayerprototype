using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace FriendSlop
{
    [RequireComponent(typeof(Light))]
    public sealed class VoiceLightIntensityDriver : NetworkBehaviour
    {
        public MicrophoneVolumeMeter VolumeMeter;
        public Light TargetLight;

        [Header("Intensity")]
        public float MinIntensity = 0f;
        public float MaxIntensity = 5f;
        public AnimationCurve VolumeToIntensity = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        public float ResponseSpeed = 16f;
        public float NetworkSendRate = 15f;
        public float NetworkSendThreshold = 0.03f;
        public float VoiceContributionTimeout = 0.35f;

        [Header("Optional Color")]
        public bool DriveColor;
        public Color QuietColor = Color.white;
        public Color LoudColor = Color.red;

        [Networked] private float NetworkIntensity { get; set; }
        [Networked] private float NetworkColorBlend { get; set; }

        private bool _isSpawned;
        private float _localTargetIntensity;
        private float _localTargetColorBlend;
        private float _lastSentIntensity = -1f;
        private float _lastSentColorBlend = -1f;
        private float _nextSendTime;
        private readonly Dictionary<PlayerRef, VoiceLightContribution> _voiceContributions = new Dictionary<PlayerRef, VoiceLightContribution>();
        private readonly List<PlayerRef> _expiredPlayers = new List<PlayerRef>();

        private struct VoiceLightContribution
        {
            public float Intensity;
            public float ColorBlend;
            public float LastUpdateTime;
        }

        private void Reset()
        {
            TargetLight = GetComponent<Light>();
        }

        private void Awake()
        {
            if (TargetLight == null)
                TargetLight = GetComponent<Light>();
        }

        public override void Spawned()
        {
            _isSpawned = true;

            if (TargetLight == null)
                TargetLight = GetComponent<Light>();

            if (HasStateAuthority)
            {
                NetworkIntensity = TargetLight != null ? TargetLight.intensity : MinIntensity;
                NetworkColorBlend = 0f;
            }
        }

        private void Update()
        {
            if (VolumeMeter == null)
                VolumeMeter = MicrophoneVolumeMeter.Instance;

            if (TargetLight == null)
                return;

            UpdateLocalVoiceTarget();
            PublishLocalVoiceTarget();
            UpdateNetworkLightFromContributions();
            ApplyLight();
        }

        private void UpdateLocalVoiceTarget()
        {
            if (VolumeMeter == null)
            {
                _localTargetIntensity = MinIntensity;
                _localTargetColorBlend = 0f;
                return;
            }

            var volume = Mathf.Clamp01(VolumeMeter.Volume01);
            _localTargetColorBlend = VolumeToIntensity != null ? Mathf.Clamp01(VolumeToIntensity.Evaluate(volume)) : volume;
            _localTargetIntensity = Mathf.Lerp(MinIntensity, MaxIntensity, _localTargetColorBlend);
        }

        private void PublishLocalVoiceTarget()
        {
            if (!_isSpawned)
                return;

            if (Time.time < _nextSendTime)
                return;

            var interval = NetworkSendRate <= 0f ? 0f : 1f / NetworkSendRate;
            _nextSendTime = Time.time + interval;

            var intensityChanged = Mathf.Abs(_localTargetIntensity - _lastSentIntensity) >= NetworkSendThreshold;
            var colorChanged = Mathf.Abs(_localTargetColorBlend - _lastSentColorBlend) >= NetworkSendThreshold;
            if (!intensityChanged && !colorChanged)
                return;

            _lastSentIntensity = _localTargetIntensity;
            _lastSentColorBlend = _localTargetColorBlend;

            if (HasStateAuthority)
                SetVoiceContribution(Runner != null ? Runner.LocalPlayer : PlayerRef.None, _localTargetIntensity, _localTargetColorBlend);
            else
                RPC_SetNetworkLightState(_localTargetIntensity, _localTargetColorBlend);
        }

        private void UpdateNetworkLightFromContributions()
        {
            if (!_isSpawned || !HasStateAuthority)
                return;

            var maxIntensity = MinIntensity;
            var maxColorBlend = 0f;
            _expiredPlayers.Clear();

            foreach (var pair in _voiceContributions)
            {
                if (Time.time - pair.Value.LastUpdateTime > VoiceContributionTimeout)
                {
                    _expiredPlayers.Add(pair.Key);
                    continue;
                }

                if (pair.Value.Intensity >= maxIntensity)
                {
                    maxIntensity = pair.Value.Intensity;
                    maxColorBlend = pair.Value.ColorBlend;
                }
            }

            for (int i = 0; i < _expiredPlayers.Count; i++)
                _voiceContributions.Remove(_expiredPlayers[i]);

            SetNetworkLightState(maxIntensity, maxColorBlend);
        }

        private void ApplyLight()
        {
            var targetIntensity = _isSpawned ? NetworkIntensity : _localTargetIntensity;
            var colorBlend = _isSpawned ? NetworkColorBlend : _localTargetColorBlend;
            var t = ResponseSpeed <= 0f ? 1f : 1f - Mathf.Exp(-ResponseSpeed * Time.deltaTime);

            TargetLight.intensity = Mathf.Lerp(TargetLight.intensity, targetIntensity, t);

            if (DriveColor)
                TargetLight.color = Color.Lerp(QuietColor, LoudColor, colorBlend);
        }

        private void SetNetworkLightState(float intensity, float colorBlend)
        {
            NetworkIntensity = Mathf.Clamp(intensity, MinIntensity, MaxIntensity);
            NetworkColorBlend = Mathf.Clamp01(colorBlend);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RPC_SetNetworkLightState(float intensity, float colorBlend, RpcInfo info = default)
        {
            SetVoiceContribution(info.Source, intensity, colorBlend);
        }

        private void SetVoiceContribution(PlayerRef player, float intensity, float colorBlend)
        {
            _voiceContributions[player] = new VoiceLightContribution
            {
                Intensity = Mathf.Clamp(intensity, MinIntensity, MaxIntensity),
                ColorBlend = Mathf.Clamp01(colorBlend),
                LastUpdateTime = Time.time
            };
        }
    }
}
