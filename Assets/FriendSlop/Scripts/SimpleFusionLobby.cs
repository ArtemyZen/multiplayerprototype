using System.Collections.Generic;
using Fusion;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FriendSlop
{
    public sealed class SimpleFusionLobby : MonoBehaviour
    {
        [Header("Runner")]
        public NetworkRunner RunnerPrefab;
        public string GameModeIdentifier = "FriendSlop";
        public int MaxPlayers = 8;

        [Header("UI")]
        public TMP_InputField SessionNameInput;
        public TextMeshProUGUI StatusLabel;
        public GameObject LobbyPanel;

        private NetworkRunner _runner;

        public async void JoinSession()
        {
            if (_runner != null)
                await _runner.Shutdown();

            var sessionName = SessionNameInput != null && string.IsNullOrWhiteSpace(SessionNameInput.text) == false
                ? SessionNameInput.text.Trim()
                : "FriendSlopRoom";

            _runner = Instantiate(RunnerPrefab);

            var sceneInfo = new NetworkSceneInfo();
            sceneInfo.AddSceneRef(SceneRef.FromIndex(SceneManager.GetActiveScene().buildIndex));

            SetStatus($"Connecting to {sessionName}...");

            var result = await _runner.StartGame(new StartGameArgs
            {
                GameMode = GameMode.Shared,
                SessionName = sessionName,
                PlayerCount = MaxPlayers,
                Scene = sceneInfo,
                SessionProperties = new Dictionary<string, SessionProperty> { ["GameMode"] = GameModeIdentifier },
            });

            if (result.Ok)
            {
                SetStatus(string.Empty);
                if (LobbyPanel != null)
                    LobbyPanel.SetActive(false);
            }
            else
            {
                SetStatus($"Connection failed: {result.ShutdownReason}");
                Destroy(_runner.gameObject);
                _runner = null;
            }
        }

        public async void LeaveSession()
        {
            if (_runner == null)
                return;

            SetStatus("Disconnecting...");
            await _runner.Shutdown();
            _runner = null;
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        private void SetStatus(string status)
        {
            if (StatusLabel != null)
                StatusLabel.text = status;
        }
    }
}
