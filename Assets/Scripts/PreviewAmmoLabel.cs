using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Floating ammo-count label spawned by <see cref="ShootingControls.ShowPreview"/> next to the
/// floating preview projectile. Built as a stack of TMP layers along local +Z so it reads as
/// a 3D-extruded slab rather than a flat sticker — front face white, back layers fade dark for
/// depth shading. Billboards toward the player camera every LateUpdate (mirrors
/// <see cref="CrateRewardLabel"/>) so the depth still reads from any angle.
///
/// Lifetime is tracked by an explicit <see cref="Object.Destroy(Object,float)"/> timer matching
/// the floating preview's lifetime. ShootingControls also keeps a reference to the most recent
/// label and destroys it on the next cycle so a stale "20" doesn't linger over a freshly-shown
/// "5" preview.
/// </summary>
public class PreviewAmmoLabel : MonoBehaviour
{
    // Five-six layers gives a clearly 3D look without being expensive — TMP geometry is small.
    // Total Z-thickness is intentionally tiny so the depth feels like extrusion, not a banner.
    private const int LayerCount = 6;
    private const float StackThickness = 0.04f;

    private readonly List<TMP_Text> _layers = new List<TMP_Text>(LayerCount);
    private Camera _cam;

    /// <summary>
    /// Spawn a 3D ammo-count label in world space at <paramref name="worldPos"/> + an upward
    /// offset, parented under <paramref name="ownerTank"/> at scale (1,1,1) so the preview's
    /// fit-scale doesn't compress the text. Auto-destroys after <paramref name="lifetime"/>.
    /// </summary>
    public static PreviewAmmoLabel Spawn(Transform ownerTank, Vector3 worldPos, string label, float lifetime)
    {
        var go = new GameObject("PreviewAmmoLabel");
        go.transform.SetParent(ownerTank, worldPositionStays: true);
        // Sit just above the preview model — close enough to read as "this preview's ammo"
        // rather than a detached HUD element. Tune via the constant if you want more headroom.
        go.transform.position = worldPos + Vector3.up * 0.12f;
        go.transform.localScale = Vector3.one;

        var pal = go.AddComponent<PreviewAmmoLabel>();
        pal.SetText(label);
        Destroy(go, lifetime);
        return pal;
    }

    private void Awake()
    {
        // Build a Z-stack of TMP renderers. Front (i=0) sits at parent anchor; back layers
        // step in +Z (away from the camera after billboarding) and darken progressively so the
        // edges shade correctly when viewed slightly off-axis.
        for (int i = 0; i < LayerCount; i++)
        {
            var layer = new GameObject($"Layer{i}");
            layer.transform.SetParent(transform, false);
            float z = (LayerCount > 1) ? (i * StackThickness / (LayerCount - 1)) : 0f;
            layer.transform.localPosition = new Vector3(0f, 0f, z);

            var tmp = layer.AddComponent<TextMeshPro>();
            tmp.fontSize = 15f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.outlineWidth = 0.2f;
            tmp.outlineColor = Color.black;

            // White on the front face, darker on the back for a fake-shading depth cue.
            float t = (LayerCount > 1) ? ((float)i / (LayerCount - 1)) : 0f;
            tmp.color = Color.Lerp(Color.white, new Color(0.2f, 0.2f, 0.2f), t);

            var rect = tmp.GetComponent<RectTransform>();
            if (rect != null) rect.sizeDelta = new Vector2(20f, 6f);
            _layers.Add(tmp);
        }
    }

    public void SetText(string label)
    {
        for (int i = 0; i < _layers.Count; i++)
            if (_layers[i] != null) _layers[i].text = label;
    }

    private void LateUpdate()
    {
        Camera cam = GetCam();
        if (cam == null) return;
        Vector3 dir = transform.position - cam.transform.position;
        if (dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
    }

    private Camera GetCam()
    {
        if (_cam != null) return _cam;
        if (Camera.main != null) { _cam = Camera.main; return _cam; }
        Camera[] cams = Camera.allCameras;
        if (cams == null || cams.Length == 0) return null;
        foreach (var c in cams)
        {
            string n = (c.name ?? "").ToLowerInvariant();
            string r = c.transform.root != null ? c.transform.root.name.ToLowerInvariant() : "";
            if (n.Contains("camerarig") || r.Contains("camerarig")) { _cam = c; return _cam; }
        }
        _cam = cams[0];
        return _cam;
    }
}
