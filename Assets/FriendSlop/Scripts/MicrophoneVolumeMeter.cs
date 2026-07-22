using Photon.Voice.Unity;
using UnityEngine;
using UnityEngine.UI;

namespace FriendSlop
{
    public sealed class MicrophoneVolumeMeter : MonoBehaviour
    {
        public enum VolumeInputSource
        {
            UnityMicrophone,
            PhotonVoiceRecorder
        }

        public static MicrophoneVolumeMeter Instance { get; private set; }

        [Header("Source")]
        public VolumeInputSource InputSource = VolumeInputSource.PhotonVoiceRecorder;
        public Recorder PhotonRecorder;
        public bool AutoFindPhotonRecorder = true;

        [Header("Microphone")]
        public bool StartRecordingOnEnable = true;
        public bool UseFirstAvailableDeviceIfEmpty = true;
        public bool LogDevicesOnStart = true;
        public string MicrophoneDevice;
        public int SampleRate = 48000;
        public int SampleWindow = 1024;

        [Header("Volume")]
        [Range(0f, 0.2f)] public float NoiseFloor = 0.003f;
        [Range(0.1f, 100f)] public float Gain = 35f;
        public float RiseSmoothing = 25f;
        public float FallSmoothing = 10f;

        [Header("Optional Indicator")]
        public Slider Slider;
        public Image FillImage;
        public RectTransform BarScale;
        public bool AutoConfigureSlider = true;
        public bool ResizeSimpleImage = true;
        public Vector2 MinImageSize = new Vector2(2f, 8f);
        public Vector2 MaxImageSize = new Vector2(100f, 8f);
        public bool TintImageByVolume;
        public Color QuietColor = new Color(1f, 1f, 1f, 0.35f);
        public Color LoudColor = Color.green;

        [Header("Debug")]
        [SerializeField] private float _debugRawRms;
        [SerializeField] private float _debugVolume01;

        public float RawRms { get; private set; }
        public float Volume01 { get; private set; }
        public bool IsRecording => InputSource == VolumeInputSource.PhotonVoiceRecorder
            ? PhotonRecorder != null && PhotonRecorder.LevelMeter != null
            : _clip != null && Microphone.IsRecording(ActiveDeviceName);
        public string ActiveDeviceName => _activeDeviceName;
        public string LastStatus => _lastStatus;

        private AudioClip _clip;
        private float[] _sampleBuffer;
        private string _activeDeviceName;
        private RectTransform _fillImageRect;
        private string _lastAutoFindStatus;

        [SerializeField, TextArea]
        private string _lastStatus;

        private void Awake()
        {
            if (Instance == null)
                Instance = this;

            CacheIndicatorReferences();
            ConfigureSlider();
        }

        private void OnEnable()
        {
            if (Instance == null)
                Instance = this;

            if (StartRecordingOnEnable)
                StartRecording();
        }

        private void OnDisable()
        {
            StopRecording();

            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            if (InputSource == VolumeInputSource.PhotonVoiceRecorder && AutoFindPhotonRecorder && !HasUsablePhotonRecorder())
                PhotonRecorder = FindBestPhotonRecorder();

            UpdateVolume();
            _debugRawRms = RawRms;
            _debugVolume01 = Volume01;
            UpdateIndicator();
        }

        public void StartRecording()
        {
            StopRecording();

            if (InputSource == VolumeInputSource.PhotonVoiceRecorder)
            {
                if (AutoFindPhotonRecorder && !HasUsablePhotonRecorder())
                    PhotonRecorder = FindBestPhotonRecorder();

                SetStatus(HasUsablePhotonRecorder()
                    ? $"Using Photon Voice Recorder level meter: {PhotonRecorder.name}."
                    : "Photon Voice Recorder level meter not ready yet.");
                return;
            }

            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                SetStatus("No microphone devices found.");
                return;
            }

            if (LogDevicesOnStart)
                Debug.Log($"Microphone devices: {string.Join(", ", Microphone.devices)}", this);

            _activeDeviceName = ResolveMicrophoneDevice();
            SampleRate = Mathf.Max(8000, SampleRate);
            SampleWindow = Mathf.Clamp(SampleWindow, 64, SampleRate);
            _sampleBuffer = new float[SampleWindow];

            ClampSampleRateToDevice();
            _clip = Microphone.Start(ActiveDeviceName, true, 1, SampleRate);

            SetStatus(_clip != null
                ? $"Recording microphone: {(string.IsNullOrEmpty(ActiveDeviceName) ? "Default" : ActiveDeviceName)}"
                : $"Microphone.Start failed: {(string.IsNullOrEmpty(ActiveDeviceName) ? "Default" : ActiveDeviceName)}");
        }

        public void StopRecording()
        {
            if (_clip != null)
            {
                Microphone.End(ActiveDeviceName);
                _clip = null;
            }

            RawRms = 0f;
            Volume01 = 0f;
            UpdateIndicator();
        }

        private void UpdateVolume()
        {
            if (InputSource == VolumeInputSource.PhotonVoiceRecorder)
            {
                UpdateFromPhotonRecorder();
                return;
            }

            if (_clip == null || !Microphone.IsRecording(ActiveDeviceName))
            {
                RawRms = 0f;
                Volume01 = Mathf.MoveTowards(Volume01, 0f, Time.deltaTime * FallSmoothing);
                return;
            }

            var micPosition = Microphone.GetPosition(ActiveDeviceName);
            if (micPosition <= 0 || _sampleBuffer == null || _sampleBuffer.Length == 0)
                return;

            var readPosition = micPosition - _sampleBuffer.Length;
            while (readPosition < 0)
                readPosition += _clip.samples;

            _clip.GetData(_sampleBuffer, readPosition);

            var sum = 0f;
            for (int i = 0; i < _sampleBuffer.Length; i++)
                sum += _sampleBuffer[i] * _sampleBuffer[i];

            RawRms = Mathf.Sqrt(sum / _sampleBuffer.Length);
            var targetVolume = Mathf.Clamp01((RawRms - NoiseFloor) * Gain);
            var smoothing = targetVolume > Volume01 ? RiseSmoothing : FallSmoothing;
            var t = 1f - Mathf.Exp(-Mathf.Max(0f, smoothing) * Time.deltaTime);
            Volume01 = Mathf.Lerp(Volume01, targetVolume, t);
        }

        private void UpdateFromPhotonRecorder()
        {
            if (!HasUsablePhotonRecorder())
            {
                RawRms = 0f;
                Volume01 = Mathf.MoveTowards(Volume01, 0f, Time.deltaTime * FallSmoothing);
                return;
            }

            RawRms = PhotonRecorder.LevelMeter.CurrentAvgAmp;
            var targetVolume = Mathf.Clamp01((RawRms - NoiseFloor) * Gain);
            var smoothing = targetVolume > Volume01 ? RiseSmoothing : FallSmoothing;
            var t = 1f - Mathf.Exp(-Mathf.Max(0f, smoothing) * Time.deltaTime);
            Volume01 = Mathf.Lerp(Volume01, targetVolume, t);
        }

        private bool HasUsablePhotonRecorder()
        {
            return PhotonRecorder != null && PhotonRecorder.LevelMeter != null;
        }

        private Recorder FindBestPhotonRecorder()
        {
            var recorders = FindObjectsOfType<Recorder>();
            Recorder fallback = null;

            for (int i = 0; i < recorders.Length; i++)
            {
                var recorder = recorders[i];
                if (recorder == null || !recorder.isActiveAndEnabled)
                    continue;

                if (fallback == null)
                    fallback = recorder;

                if (recorder.LevelMeter == null)
                    continue;

                if (recorder.IsCurrentlyTransmitting)
                    return recorder;

                fallback = recorder;
            }

            if (fallback != PhotonRecorder)
            {
                var status = fallback != null
                    ? $"Waiting for Photon Recorder level meter: {fallback.name}."
                    : "Photon Recorder not found.";

                if (_lastAutoFindStatus != status)
                {
                    _lastAutoFindStatus = status;
                    SetStatus(status);
                }
            }

            return fallback;
        }

        private string ResolveMicrophoneDevice()
        {
            var requestedDevice = string.IsNullOrWhiteSpace(MicrophoneDevice) ? null : MicrophoneDevice.Trim();
            if (!string.IsNullOrEmpty(requestedDevice))
            {
                for (int i = 0; i < Microphone.devices.Length; i++)
                {
                    if (Microphone.devices[i] == requestedDevice)
                        return requestedDevice;
                }

                Debug.LogWarning($"Requested microphone '{requestedDevice}' was not found.", this);
            }

            return UseFirstAvailableDeviceIfEmpty ? Microphone.devices[0] : null;
        }

        private void ClampSampleRateToDevice()
        {
            Microphone.GetDeviceCaps(ActiveDeviceName, out var minFrequency, out var maxFrequency);
            if (minFrequency <= 0 && maxFrequency <= 0)
                return;

            if (minFrequency > 0 && SampleRate < minFrequency)
                SampleRate = minFrequency;

            if (maxFrequency > 0 && SampleRate > maxFrequency)
                SampleRate = maxFrequency;
        }

        private void UpdateIndicator()
        {
            if (Slider != null)
            {
                if (AutoConfigureSlider)
                    ConfigureSlider();

                Slider.value = Volume01;
            }

            if (FillImage != null)
            {
                FillImage.fillAmount = Volume01;

                if (ResizeSimpleImage && FillImage.type != Image.Type.Filled)
                {
                    if (_fillImageRect == null)
                        _fillImageRect = FillImage.rectTransform;

                    var targetSize = Vector2.Lerp(MinImageSize, MaxImageSize, Volume01);
                    _fillImageRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetSize.x);
                    _fillImageRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, targetSize.y);
                }

                if (TintImageByVolume)
                    FillImage.color = Color.Lerp(QuietColor, LoudColor, Volume01);
            }

            if (BarScale != null)
            {
                var scale = BarScale.localScale;
                scale.x = Volume01;
                BarScale.localScale = scale;
            }
        }

        private void CacheIndicatorReferences()
        {
            if (FillImage != null)
                _fillImageRect = FillImage.rectTransform;
        }

        private void ConfigureSlider()
        {
            if (Slider == null)
                return;

            Slider.minValue = 0f;
            Slider.maxValue = 1f;
            Slider.wholeNumbers = false;
        }

        private void SetStatus(string status)
        {
            _lastStatus = status;
            Debug.Log(status, this);
        }
    }
}
