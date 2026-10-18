using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Start screen (MainMenu scene): pick mixed reality, VR real-time, or VR turn-based with
/// the number of hot-seat players and AI tanks. Built from primitives at runtime and driven
/// by a laser from either controller — point and pull the trigger. The choice goes into
/// <see cref="GameSession"/> before the game scene loads.
/// </summary>
[RequireComponent(typeof(OVRCameraRig))]
public class MainMenu : MonoBehaviour
{
    [Tooltip("URP Unlit material; tinted per element at runtime.")]
    [SerializeField] private Material unlitMaterial;
    [SerializeField] private float panelDistance = 1.3f;
    [SerializeField] private float laserLength = 4f;

    private const string HumansPref = "XRTanks.Menu.HumanPlayers";
    private const string AiPref = "XRTanks.Menu.AiTanks";

    private static readonly Color ButtonColor = new Color(0.16f, 0.22f, 0.32f);
    private static readonly Color HoverColor = new Color(0.25f, 0.45f, 0.75f);
    private static readonly Color PanelColor = new Color(0.05f, 0.07f, 0.1f);

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
    private TextMeshPro _humansLabel;
    private TextMeshPro _aiLabel;
    private int _humans;
    private int _ai;
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
    }

    private void OnEnable()
    {
        _leftTrigger = new InputAction("MenuSelectLeft", InputActionType.Button, "<XRController>{LeftHand}/trigger");
        _rightTrigger = new InputAction("MenuSelectRight", InputActionType.Button, "<XRController>{RightHand}/trigger");
        _leftTrigger.Enable();
        _rightTrigger.Enable();
    }

    private void OnDisable()
    {
        _leftTrigger?.Disable();
        _rightTrigger?.Disable();
        _leftTrigger?.Dispose();
        _rightTrigger?.Dispose();
        _leftTrigger = null;
        _rightTrigger = null;
    }

    private IEnumerator Start()
    {
        BuildPanel();
        _leftLaser = CreateLaser("Left Laser");
        _rightLaser = CreateLaser("Right Laser");

        // Head tracking needs a moment before its pose means anything.
        _panel.gameObject.SetActive(false);
        yield return new WaitForSeconds(0.3f);
        PlacePanel();
        _panel.gameObject.SetActive(true);
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
            if (_hovered != null) _hovered.renderer.sharedMaterial = Colored(ButtonColor);
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
        Haptics.Pulse(this, controller, 0.5f, 0.4f, 0.05f);
        button.onClick?.Invoke();
    }

    private void Play(GameMode mode, string sceneName)
    {
        GameSession.Mode = mode;
        GameSession.HumanPlayers = _humans;
        GameSession.AiTanks = _ai;
        PlayerPrefs.SetInt(HumansPref, _humans);
        PlayerPrefs.SetInt(AiPref, _ai);
        PlayerPrefs.Save();

        _loading = true;
        _leftLaser.enabled = false;
        _rightLaser.enabled = false;
        SceneManager.LoadScene(sceneName);
    }

    private void ChangeHumans(int delta)
    {
        _humans = Mathf.Clamp(_humans + delta, 1, GameSession.MaxHumanPlayers);
        _humansLabel.text = $"Players: {_humans}";
    }

    private void ChangeAi(int delta)
    {
        _ai = Mathf.Clamp(_ai + delta, 1, GameSession.MaxAiTanks);
        _aiLabel.text = $"AI tanks: {_ai}";
    }

    // ---------- Building the panel ----------

    private void BuildPanel()
    {
        _panel = new GameObject("Menu Panel").transform;
        _panel.SetParent(transform.parent, false);

        CreateQuad("Backdrop", new Vector3(0f, 0.01f, 0.015f), new Vector3(0.78f, 0.86f, 0.01f), PanelColor);
        CreateText("XR TANKS", new Vector3(0f, 0.34f, 0f), 0.08f, Color.white);
        CreateText("Choose how to play", new Vector3(0f, 0.26f, 0f), 0.03f, new Color(0.75f, 0.8f, 0.9f));

        CreateButton("Mixed Reality  -  real-time", new Vector3(0f, 0.16f, 0f), new Vector2(0.62f, 0.08f),
            () => Play(GameMode.RealTime, GameSession.MixedRealitySceneName));
        CreateButton("VR Mountains  -  real-time", new Vector3(0f, 0.06f, 0f), new Vector2(0.62f, 0.08f),
            () => Play(GameMode.RealTime, GameSession.VirtualRealitySceneName));
        CreateButton("VR Mountains  -  turn-based", new Vector3(0f, -0.04f, 0f), new Vector2(0.62f, 0.08f),
            () => Play(GameMode.TurnBased, GameSession.VirtualRealitySceneName));

        CreateText("Turn-based setup", new Vector3(0f, -0.13f, 0f), 0.028f, new Color(0.75f, 0.8f, 0.9f));
        _humansLabel = CreateText("", new Vector3(0f, -0.2f, 0f), 0.035f, Color.white);
        CreateButton("-", new Vector3(-0.24f, -0.2f, 0f), new Vector2(0.07f, 0.06f), () => ChangeHumans(-1));
        CreateButton("+", new Vector3(0.24f, -0.2f, 0f), new Vector2(0.07f, 0.06f), () => ChangeHumans(1));
        _aiLabel = CreateText("", new Vector3(0f, -0.29f, 0f), 0.035f, Color.white);
        CreateButton("-", new Vector3(-0.24f, -0.29f, 0f), new Vector2(0.07f, 0.06f), () => ChangeAi(-1));
        CreateButton("+", new Vector3(0.24f, -0.29f, 0f), new Vector2(0.07f, 0.06f), () => ChangeAi(1));
        CreateText("Players share the headset and take turns against the AI.", new Vector3(0f, -0.36f, 0f), 0.02f, new Color(0.6f, 0.65f, 0.75f));

        ChangeHumans(0);
        ChangeAi(0);
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

    private void CreateButton(string label, Vector3 position, Vector2 size, System.Action onClick)
    {
        GameObject quad = CreateQuad($"Button {label}", position, new Vector3(size.x, size.y, 0.012f), ButtonColor, keepCollider: true);
        quad.layer = LayerMask.NameToLayer("UI");
        CreateText(label, position + new Vector3(0f, 0f, -0.008f), size.y * 0.42f, Color.white);
        _buttons.Add(new Button
        {
            collider = quad.GetComponent<Collider>(),
            renderer = quad.GetComponent<Renderer>(),
            onClick = onClick,
        });
    }

    private GameObject CreateQuad(string quadName, Vector3 position, Vector3 scale, Color color, bool keepCollider = false)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = quadName;
        cube.transform.SetParent(_panel, false);
        cube.transform.localPosition = position;
        cube.transform.localScale = scale;
        cube.GetComponent<Renderer>().sharedMaterial = Colored(color);
        cube.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
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
