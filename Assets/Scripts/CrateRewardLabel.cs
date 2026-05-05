using TMPro;
using UnityEngine;

/// <summary>
/// Floating world-space label spawned by <see cref="CrateReward"/> when a crate dispenses a reward.
/// Rises a small distance, fades out, billboards toward the player camera, then self-destructs.
/// If no <see cref="text"/> is wired (e.g. spawned at runtime without a styled prefab), one is
/// created on the fly with sensible defaults so the feature works without a prefab dependency.
/// </summary>
public class CrateRewardLabel : MonoBehaviour
{
    [Tooltip("TMP_Text used for the label. If null, one is created at runtime when Show() is called.")]
    [SerializeField] private TMP_Text text;

    [Tooltip("Total lifetime in seconds (rise + fade + destroy).")]
    [SerializeField] private float lifetime = 1.2f;

    [Tooltip("How far the label rises during its lifetime, in meters.")]
    [SerializeField] private float riseDistance = 0.25f;

    [Tooltip("Default font size applied when the script has to create the TMP component itself.")]
    [SerializeField] private float runtimeFontSize = 1.5f;

    private Camera _cam;
    private Vector3 _basePos;
    private float _spawnTime = -1f;

    /// <summary>Set the label text and colour, then start the rise/fade animation. Caller is
    /// expected to spawn this object at the desired world position before calling Show.</summary>
    public void Show(string label, Color color)
    {
        EnsureText();
        if (text != null)
        {
            text.text = label;
            color.a = 1f;
            text.color = color;
        }
        _basePos = transform.position;
        _spawnTime = Time.time;
        Destroy(gameObject, lifetime);
    }

    private void EnsureText()
    {
        if (text != null) return;
        text = GetComponentInChildren<TMP_Text>();
        if (text != null) return;

        // Build a minimal world-space TextMeshPro on a child so the label is self-contained
        // without needing a styled prefab. Default font asset from TMP_Settings is used, which
        // is the same one TMPro auto-creates on demand for new world-space text.
        var child = new GameObject("Text");
        child.transform.SetParent(transform, false);
        var tmp = child.AddComponent<TextMeshPro>();
        tmp.fontSize = runtimeFontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.outlineWidth = 0.2f;
        tmp.outlineColor = new Color(0f, 0f, 0f, 1f);
        // Shrink the rect so the bounds don't overflow into other geometry.
        var rect = tmp.GetComponent<RectTransform>();
        if (rect != null) rect.sizeDelta = new Vector2(2f, 0.6f);
        text = tmp;
    }

    private void LateUpdate()
    {
        if (_spawnTime < 0f) return;

        float t = lifetime > 0f ? Mathf.Clamp01((Time.time - _spawnTime) / lifetime) : 1f;

        // Eased rise: out-cubic feels good for a quick "+pop" cue.
        float ease = 1f - Mathf.Pow(1f - t, 3f);
        transform.position = _basePos + Vector3.up * (riseDistance * ease);

        if (text != null)
        {
            Color c = text.color;
            c.a = 1f - t;
            text.color = c;
        }

        Camera cam = GetCam();
        if (cam != null)
        {
            Vector3 dir = transform.position - cam.transform.position;
            if (dir.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }
    }

    private Camera GetCam()
    {
        if (_cam != null) return _cam;

        if (Camera.main != null)
        {
            _cam = Camera.main;
            return _cam;
        }

        Camera[] cams = Camera.allCameras;
        if (cams == null || cams.Length == 0) return null;

        foreach (var c in cams)
        {
            string n = (c.name ?? "").ToLowerInvariant();
            string r = c.transform.root != null ? c.transform.root.name.ToLowerInvariant() : "";
            if (n.Contains("camerarig") || r.Contains("camerarig"))
            {
                _cam = c;
                return _cam;
            }
        }

        _cam = cams[0];
        return _cam;
    }
}
