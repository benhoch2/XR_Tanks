using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Start screen (MainMenu scene): pick mixed reality, VR real-time, VR turn-based hot-seat,
/// or host / join an online turn-based game by code. Built from primitives at runtime and
/// driven by a laser from either controller — point and pull the trigger. Offline choices go
/// into <see cref="GameSession"/> before the game scene loads; online games go through
/// <see cref="OnlineSession"/>, and the host's Start loads the game on every headset.
/// </summary>
[RequireComponent(typeof(OVRCameraRig))]
public class MainMenu : MonoBehaviour
{
    [Tooltip("URP Unlit material; tinted per element at runtime.")]
    [SerializeField] private Material unlitMaterial;
    [Tooltip("NetworkManager + UnityTransport prefab, created on demand for online games.")]
    [SerializeField] private GameObject networkBootstrapPrefab;
    [SerializeField] private float panelDistance = 1.3f;
    [SerializeField] private float laserLength = 4f;

    private const string HumansPref = "XRTanks.Menu.HumanPlayers";
    private const string AiPref = "XRTanks.Menu.AiTanks";
    private const string OnlineAiPref = "XRTanks.Menu.OnlineAiTanks";
    private const int JoinCodeLength = 6;
    private const string CodeAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private static readonly Color ButtonColor = new Color(0.16f, 0.22f, 0.32f);
    private static readonly Color HoverColor = new Color(0.25f, 0.45f, 0.75f);
    private static readonly Color PanelColor = new Color(0.05f, 0.07f, 0.1f);
    private static readonly Color SubtleText = new Color(0.75f, 0.8f, 0.9f);
    private static readonly Color HintText = new Color(0.6f, 0.65f, 0.75f);
    private static readonly Color ErrorText = new Color(1f, 0.55f, 0.45f);

    private class Button
    {
        public Collider collider;
        public Renderer renderer;
        public System.Action onClick;
    }

    private OVRCameraRig _rig;
    private Transform _panel;
    private readonly List<Button> _buttons = new List<Button>();
    private readonly Dictionary<Color, Material> _materials = new Dictionary<Color, Material>();
    private System.Action _refreshScreen;
    private TextMeshPro _status;
    private int _humans;
    private int _ai;
    private int _onlineAi;
    private string _joinCode = "";
    private bool _busy;
    private bool _loading;

    private InputAction _leftTrigger;
    private InputAction _rightTrigger;
    private LineRenderer _leftLaser;
    private LineRenderer _rightLaser;
    private Button _hovered;

    private void Awake()
    {
        _rig = GetComponent<OVRCameraRig>();
        _humans = Mathf.Clamp(PlayerPrefs.GetInt(HumansPref, 1), 1, GameSession.MaxHumanPlayers);
        _ai = Mathf.Clamp(PlayerPrefs.GetInt(AiPref, 3), 1, GameSession.MaxAiTanks);
        _onlineAi = Mathf.Clamp(PlayerPrefs.GetInt(OnlineAiPref, 2), 0, GameSession.MaxAiTanks);
    }

    private void OnEnable()
    {
        _leftTrigger = new InputAction("MenuSelectLeft", InputActionType.Button, "<XRController>{LeftHand}/trigger");
        _rightTrigger = new InputAction("MenuSelectRight", InputActionType.Button, "<XRController>{RightHand}/trigger");
        _leftTrigger.Enable();
        _rightTrigger.Enable();
        OnlineSession.MatchSetupReceived += OnMatchStarting;
    }

    private void OnDisable()
    {
        _leftTrigger?.Disable();
        _rightTrigger?.Disable();
        _leftTrigger?.Dispose();
        _rightTrigger?.Dispose();
        _leftTrigger = null;
        _rightTrigger = null;
        OnlineSession.MatchSetupReceived -= OnMatchStarting;
    }

    private IEnumerator Start()
    {
        _panel = new GameObject("Menu Panel").transform;
        _panel.SetParent(transform.parent, false);
        _leftLaser = CreateLaser("Left Laser");
        _rightLaser = CreateLaser("Right Laser");

        // Back in the menu means no online game, whatever happened before.
        if (OnlineSession.IsConnected)
            _ = OnlineSession.LeaveAsync();
        GameSession.ResetOnline();

        ShowMainScreen();
        if (!string.IsNullOrEmpty(OnlineSession.PendingMessage))
        {
            SetStatus(OnlineSession.PendingMessage, ErrorText);
            OnlineSession.PendingMessage = null;
        }

        // Head tracking needs a moment before its pose means anything.
        _panel.gameObject.SetActive(false);
        yield return new WaitForSeconds(0.3f);
        PlacePanel();
        _panel.gameObject.SetActive(true);

        while (true)
        {
            _refreshScreen?.Invoke();
            yield return new WaitForSeconds(0.5f);
        }
    }

    private void OnDestroy()
    {
        foreach (Material m in _materials.Values)
        {
            if (m != null)
                Destroy(m);
        }
    }

    private void Update()
    {
        if (_panel == null || !_panel.gameObject.activeSelf || _loading)
            return;

        Button hoverLeft = UpdateLaser(_rig.leftHandAnchor, _leftLaser);
        Button hoverRight = UpdateLaser(_rig.rightHandAnchor, _rightLaser);
        Button hovered = hoverRight ?? hoverLeft;

        if (hovered != _hovered)
        {
            if (_hovered != null && _hovered.renderer != null) _hovered.renderer.sharedMaterial = Colored(ButtonColor);
            if (hovered != null) hovered.renderer.sharedMaterial = Colored(HoverColor);
            _hovered = hovered;
        }

        if (hoverRight != null && _rightTrigger.WasPressedThisFrame())
            Click(hoverRight, OVRInput.Controller.RTouch);
        else if (hoverLeft != null && _leftTrigger.WasPressedThisFrame())
            Click(hoverLeft, OVRInput.Controller.LTouch);
    }

    private Button UpdateLaser(Transform hand, LineRenderer laser)
    {
        if (hand == null)
        {
            laser.enabled = false;
            return null;
        }

        Vector3 origin = hand.position;
        Vector3 end = origin + hand.forward * laserLength;
        Button hit = null;
        if (Physics.Raycast(origin, hand.forward, out RaycastHit info, laserLength, 1 << LayerMask.NameToLayer("UI"), QueryTriggerInteraction.Collide))
        {
            end = info.point;
            hit = _buttons.Find(b => b.collider == info.collider);
        }

        laser.enabled = true;
        laser.SetPosition(0, origin);
        laser.SetPosition(1, end);
        return hit;
    }

    private void Click(Button button, OVRInput.Controller controller)
    {
        if (_busy)
            return;
        Haptics.Pulse(this, controller, 0.5f, 0.4f, 0.05f);
        button.onClick?.Invoke();
    }

    // ---------- Screens ----------

    private void ShowMainScreen()
    {
        BuildScreen(0.98f, 0.04f);
        AddText("XR TANKS", 0.43f, 0.08f, Color.white);
        AddText("Choose how to play", 0.35f, 0.03f, SubtleText);

        AddButton("Mixed Reality  -  real-time", 0.26f, 0.62f, () => Play(GameMode.RealTime, GameSession.MixedRealitySceneName));
        AddButton("VR Mountains  -  real-time", 0.16f, 0.62f, () => Play(GameMode.RealTime, GameSession.VirtualRealitySceneName));
        AddButton("VR Mountains  -  turn-based", 0.06f, 0.62f, () => Play(GameMode.TurnBased, GameSession.VirtualRealitySceneName));

        AddText("Turn-based setup (this headset)", -0.03f, 0.028f, SubtleText);
        TextMeshPro humansLabel = AddText("", -0.1f, 0.035f, Color.white);
        AddButton("-", -0.1f, 0.07f, () => { _humans = Mathf.Clamp(_humans - 1, 1, GameSession.MaxHumanPlayers); }, -0.24f);
        AddButton("+", -0.1f, 0.07f, () => { _humans = Mathf.Clamp(_humans + 1, 1, GameSession.MaxHumanPlayers); }, 0.24f);
        TextMeshPro aiLabel = AddText("", -0.18f, 0.035f, Color.white);
        AddButton("-", -0.18f, 0.07f, () => { _ai = Mathf.Clamp(_ai - 1, 1, GameSession.MaxAiTanks); }, -0.24f);
        AddButton("+", -0.18f, 0.07f, () => { _ai = Mathf.Clamp(_ai + 1, 1, GameSession.MaxAiTanks); }, 0.24f);

        AddText("Online turn-based (one player per headset)", -0.27f, 0.028f, SubtleText);
        AddButton("Host online game", -0.35f, 0.3f, HostOnline, -0.16f);
        AddButton("Join online game", -0.35f, 0.3f, ShowJoinScreen, 0.16f);
        _status = AddText("", -0.43f, 0.022f, HintText);

        _refreshScreen = () =>
        {
            humansLabel.text = $"Players: {_humans}";
            aiLabel.text = $"AI tanks: {_ai}";
        };
        _refreshScreen();
    }

    private void ShowHostScreen()
    {
        BuildScreen(0.8f, 0f);
        AddText("Online game", 0.33f, 0.06f, Color.white);
        AddText(OnlineSession.JoinCode != null ? "Join code" : "Hosting on the local network", 0.25f, 0.03f, SubtleText);
        AddText(OnlineSession.JoinCode ?? "", 0.17f, 0.08f, new Color(1f, 0.85f, 0.3f));
        TextMeshPro players = AddText("", 0.07f, 0.035f, Color.white);

        TextMeshPro aiLabel = AddText("", -0.02f, 0.035f, Color.white);
        AddButton("-", -0.02f, 0.07f, () => { _onlineAi = Mathf.Clamp(_onlineAi - 1, 0, GameSession.MaxAiTanks); }, -0.24f);
        AddButton("+", -0.02f, 0.07f, () => { _onlineAi = Mathf.Clamp(_onlineAi + 1, 0, GameSession.MaxAiTanks); }, 0.24f);

        AddButton("Start match", -0.13f, 0.4f, StartOnlineMatch);
        AddButton("Cancel", -0.23f, 0.4f, LeaveOnline);
        _status = AddText("", -0.32f, 0.022f, HintText);

        _refreshScreen = () =>
        {
            int count = OnlineSession.PlayerCount;
            players.text = $"Players connected: {count} / {OnlineSession.MaxPlayers}";
            aiLabel.text = _onlineAi == 0 ? "AI tanks: 0 (free-for-all)" : $"AI tanks: {_onlineAi}";
        };
        _refreshScreen();
    }

    private void ShowJoinScreen()
    {
        _joinCode = "";
        BuildScreen(0.98f, 0f);
        AddText("Join online game", 0.42f, 0.06f, Color.white);
        AddText("Enter the host's code", 0.35f, 0.028f, SubtleText);
        TextMeshPro codeLabel = AddText("", 0.27f, 0.07f, new Color(1f, 0.85f, 0.3f));

        // 6 x 6 keypad: A-Z then 0-9.
        const int columns = 6;
        const float keyWidth = 0.08f, keySpacingX = 0.095f, keySpacingY = 0.07f, firstRowY = 0.17f;
        for (int i = 0; i < CodeAlphabet.Length; i++)
        {
            char key = CodeAlphabet[i];
            int row = i / columns, column = i % columns;
            float x = (column - (columns - 1) * 0.5f) * keySpacingX;
            AddButton(key.ToString(), firstRowY - row * keySpacingY, keyWidth, () =>
            {
                if (_joinCode.Length < JoinCodeLength)
                    _joinCode += key;
            }, x, 0.055f);
        }

        float actionsY = firstRowY - 6 * keySpacingY - 0.01f;
        AddButton("Delete", actionsY, 0.18f, () =>
        {
            if (_joinCode.Length > 0)
                _joinCode = _joinCode.Substring(0, _joinCode.Length - 1);
        }, -0.2f);
        AddButton("Join", actionsY, 0.18f, JoinOnline, 0f);
        AddButton("Back", actionsY, 0.18f, ShowMainScreen, 0.2f);
        _status = AddText("", actionsY - 0.07f, 0.022f, HintText);

        _refreshScreen = () => codeLabel.text = _joinCode.PadRight(JoinCodeLength, '_');
        _refreshScreen();
    }

    private void ShowWaitingScreen()
    {
        BuildScreen(0.6f, 0f);
        AddText("Joined", 0.2f, 0.06f, Color.white);
        AddText(OnlineSession.JoinCode ?? "", 0.12f, 0.05f, new Color(1f, 0.85f, 0.3f));
        TextMeshPro players = AddText("", 0.03f, 0.035f, Color.white);
        AddText("Waiting for the host to start the match...", -0.05f, 0.026f, SubtleText);
        AddButton("Leave", -0.15f, 0.3f, LeaveOnline);
        _status = AddText("", -0.23f, 0.022f, HintText);

        _refreshScreen = () => players.text = $"Players: {OnlineSession.PlayerCount}";
        _refreshScreen();
    }

    // ---------- Actions ----------

    private void Play(GameMode mode, string sceneName)
    {
        GameSession.ResetOnline();
        GameSession.Mode = mode;
        GameSession.HumanPlayers = _humans;
        GameSession.AiTanks = _ai;
        SavePrefs();
        LoadScene(sceneName);
    }

    private async void HostOnline()
    {
        _busy = true;
        SetStatus("Creating game...", HintText);
        try
        {
            OnlineSession.EnsureNetworkManager(networkBootstrapPrefab);
            await OnlineSession.HostAsync();
            ShowHostScreen();
        }
        catch (System.Exception e)
        {
            await OnlineSession.LeaveAsync();
            SetStatus(OnlineErrorText(e), ErrorText);
            Debug.LogWarning($"[Online] Hosting failed: {e}");
        }
        finally
        {
            _busy = false;
        }
    }

    private async void JoinOnline()
    {
        if (_joinCode.Length != JoinCodeLength)
        {
            SetStatus($"The code has {JoinCodeLength} characters.", ErrorText);
            return;
        }

        _busy = true;
        SetStatus("Joining...", HintText);
        try
        {
            OnlineSession.EnsureNetworkManager(networkBootstrapPrefab);
            await OnlineSession.JoinAsync(_joinCode);
            ShowWaitingScreen();
        }
        catch (System.Exception e)
        {
            await OnlineSession.LeaveAsync();
            SetStatus(OnlineErrorText(e), ErrorText);
            Debug.LogWarning($"[Online] Joining failed: {e}");
        }
        finally
        {
            _busy = false;
        }
    }

    private void StartOnlineMatch()
    {
        if (OnlineSession.PlayerCount < 2 && _onlineAi == 0)
        {
            SetStatus("Add AI tanks or wait for another player.", ErrorText);
            return;
        }

        SavePrefs();
        _loading = true;
        SetLasers(false);
        OnlineSession.StartMatch(_onlineAi);
    }

    private async void LeaveOnline()
    {
        _busy = true;
        await OnlineSession.LeaveAsync();
        _busy = false;
        ShowMainScreen();
    }

    private void OnMatchStarting()
    {
        // Client: the host pressed Start; Netcode loads the game scene next.
        _loading = true;
        SetLasers(false);
        SetStatus("Starting...", HintText);
    }

    private static string OnlineErrorText(System.Exception e)
    {
        string text = e.Message;
        if (text.Contains("project") || text.Contains("Project") || e is Unity.Services.Core.ServicesInitializationException)
            return "Online needs the project linked to Unity Cloud (Relay + Lobby enabled).";
        return text.Length > 90 ? text.Substring(0, 90) + "..." : text;
    }

    private void SavePrefs()
    {
        PlayerPrefs.SetInt(HumansPref, _humans);
        PlayerPrefs.SetInt(AiPref, _ai);
        PlayerPrefs.SetInt(OnlineAiPref, _onlineAi);
        PlayerPrefs.Save();
    }

    private void LoadScene(string sceneName)
    {
        _loading = true;
        SetLasers(false);
        SceneManager.LoadScene(sceneName);
    }

    private void SetLasers(bool on)
    {
        if (_leftLaser != null) _leftLaser.enabled = on;
        if (_rightLaser != null) _rightLaser.enabled = on;
    }

    private void SetStatus(string text, Color color)
    {
        if (_status == null)
            return;
        _status.text = text;
        _status.color = color;
    }

    // ---------- Building the panel ----------

    /// <summary>Clears the panel and adds a backdrop <paramref name="height"/> tall, centered at <paramref name="centerY"/>.</summary>
    private void BuildScreen(float height, float centerY)
    {
        for (int i = _panel.childCount - 1; i >= 0; i--)
            Destroy(_panel.GetChild(i).gameObject);
        _buttons.Clear();
        _hovered = null;
        _refreshScreen = null;
        _status = null;

        CreateBox("Backdrop", new Vector3(0f, centerY, 0.015f), new Vector3(0.78f, height, 0.01f), PanelColor, keepCollider: false);
    }

    private void PlacePanel()
    {
        Transform head = _rig.centerEyeAnchor;
        Vector3 forward = head.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.forward;
        forward.Normalize();

        float eyeHeight = head.position.y > transform.position.y + 0.5f ? head.position.y : transform.position.y + 1.5f;
        _panel.position = new Vector3(head.position.x, eyeHeight - 0.1f, head.position.z) + forward * panelDistance;
        // Forward points away from the player, so the content (built facing -Z) faces them.
        _panel.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }

    private void AddButton(string label, float y, float width, System.Action onClick, float x = 0f, float height = 0.08f)
    {
        var position = new Vector3(x, y, 0f);
        GameObject box = CreateBox($"Button {label}", position, new Vector3(width, height, 0.012f), ButtonColor, keepCollider: true);
        box.layer = LayerMask.NameToLayer("UI");
        TextMeshPro text = CreateText(label, position + new Vector3(0f, 0f, -0.008f), height * 0.42f, Color.white);
        text.rectTransform.sizeDelta = new Vector2(width, height);

        _buttons.Add(new Button
        {
            collider = box.GetComponent<Collider>(),
            renderer = box.GetComponent<Renderer>(),
            // Every click may change what the screen shows.
            onClick = () =>
            {
                onClick();
                _refreshScreen?.Invoke();
            },
        });
    }

    private TextMeshPro AddText(string text, float y, float lineHeight, Color color)
    {
        return CreateText(text, new Vector3(0f, y, 0f), lineHeight, color);
    }

    private GameObject CreateBox(string boxName, Vector3 position, Vector3 scale, Color color, bool keepCollider)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = boxName;
        cube.transform.SetParent(_panel, false);
        cube.transform.localPosition = position;
        cube.transform.localScale = scale;
        Renderer cubeRenderer = cube.GetComponent<Renderer>();
        cubeRenderer.sharedMaterial = Colored(color);
        cubeRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        if (!keepCollider)
            Destroy(cube.GetComponent<Collider>());
        return cube;
    }

    private TextMeshPro CreateText(string text, Vector3 position, float lineHeight, Color color)
    {
        var go = new GameObject("Menu Text");
        go.transform.SetParent(_panel, false);
        go.transform.localPosition = position;
        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.color = color;
        // TextMeshPro (non-UI) renders roughly 0.1 m per font-size point at unit scale.
        tmp.fontSize = lineHeight * 10f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.rectTransform.sizeDelta = new Vector2(0.75f, lineHeight * 1.5f);
        return tmp;
    }

    private LineRenderer CreateLaser(string laserName)
    {
        var go = new GameObject(laserName);
        go.transform.SetParent(transform.parent, false);
        var line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.widthMultiplier = 0.004f;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.sharedMaterial = Colored(new Color(0.6f, 0.85f, 1f));
        line.enabled = false;
        return line;
    }

    private Material Colored(Color color)
    {
        if (_materials.TryGetValue(color, out Material material))
            return material;

        material = new Material(unlitMaterial);
        material.color = color;
        _materials[color] = material;
        return material;
    }
}
