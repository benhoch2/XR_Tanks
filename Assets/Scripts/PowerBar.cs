using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PowerBar : MonoBehaviour
{
    [Range  (0f, 1f)]
    public float power = 0.5f;

    [SerializeField]
    private Transform emptyBar;
    [SerializeField]
    private Transform fullBar;

    // optional camera to face; if null will try to find one automatically
    [SerializeField]
    private Camera cameraToFace;

    // cached renderers for enabling/disabling visuals
    private MeshRenderer[] emptyRenderers;
    private MeshRenderer[] fullRenderers;

    // Start is called before the first frame update
    void Start()
    {
        ApplyFacing();
        ApplyPower();
    }

    // Update is called once per frame
    void Update()
    {
        ApplyFacing();
        ApplyPower();
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        // update in editor when value changes
        ApplyFacing();
        ApplyPower();
    }
#endif

    private void ApplyFacing()
    {
        // choose camera (cached if found)
        Camera cam = GetOrFindCamera();
        if (cam == null) return;

        // horizontal direction from this object to camera
        Vector3 dir = cam.transform.position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return;

        // Build rotation so Z faces camera, then rotate -90deg around Y so local +X faces camera.
        Quaternion look = Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Euler(0f, -90f, 0f);
        transform.rotation = look;
    }

    // Try to get the assigned camera, otherwise locate and cache a suitable camera:
    // 1) Camera.main
    // 2) any Camera whose name or root name contains "CameraRig"
    // 3) the closest Camera to this object
    // 4) any Camera found in the scene
    private Camera GetOrFindCamera()
    {
        if (cameraToFace != null) return cameraToFace;

        if (Camera.main != null)
        {
            cameraToFace = Camera.main;
            return cameraToFace;
        }

        Camera[] cams = Camera.allCameras;
        if (cams != null && cams.Length > 0)
        {
            // look for CameraRig by name
            foreach (var c in cams)
            {
                string nameLower = (c.name ?? "").ToLower();
                string rootNameLower = (c.transform.root != null ? c.transform.root.name : "").ToLower();
                if (nameLower.Contains("camerarig") || rootNameLower.Contains("camerarig"))
                {
                    cameraToFace = c;
                    return cameraToFace;
                }
            }

            // pick closest camera as fallback
            float bestDist = Mathf.Infinity;
            Camera best = null;
            Vector3 myPos = transform.position;
            foreach (var c in cams)
            {
                float d = (c.transform.position - myPos).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }
            if (best != null)
            {
                cameraToFace = best;
                return cameraToFace;
            }
        }

        // last resort: find any Camera in scene
        Camera any = GameObject.FindObjectOfType<Camera>();
        if (any != null)
        {
            cameraToFace = any;
            return cameraToFace;
        }

        return null;
    }

    // ensure we have renderer references (include inactive so we can re-enable)
    private void CacheRenderersIfNeeded()
    {
        if (emptyRenderers == null && emptyBar != null)
            emptyRenderers = emptyBar.GetComponentsInChildren<MeshRenderer>(true);
        if (fullRenderers == null && fullBar != null)
            fullRenderers = fullBar.GetComponentsInChildren<MeshRenderer>(true);
    }

    private void ApplyPower()
    {
        if (emptyBar == null || fullBar == null) return;

        CacheRenderersIfNeeded();

        // Get empty bar length along local Z
        float emptyZ = emptyBar.localScale.z;

        // Desired full bar length along Z
        float fullZ = Mathf.Clamp01(power) * emptyZ;

        // If there's no power, disable renderers on both bars and skip updates
        if (Mathf.Approximately(fullZ, 0f))
        {
            if (emptyRenderers != null)
            {
                for (int i = 0; i < emptyRenderers.Length; i++)
                    if (emptyRenderers[i] != null) emptyRenderers[i].enabled = false;
            }
            if (fullRenderers != null)
            {
                for (int i = 0; i < fullRenderers.Length; i++)
                    if (fullRenderers[i] != null) fullRenderers[i].enabled = false;
            }
            return;
        }

        // Ensure renderers are enabled when there is power
        if (emptyRenderers != null)
        {
            for (int i = 0; i < emptyRenderers.Length; i++)
                if (emptyRenderers[i] != null) emptyRenderers[i].enabled = true;
        }
        if (fullRenderers != null)
        {
            for (int i = 0; i < fullRenderers.Length; i++)
                if (fullRenderers[i] != null) fullRenderers[i].enabled = true;
        }

        // Preserve full bar's X/Y scale, set Z to computed length
        Vector3 fullScale = fullBar.localScale;
        fullScale.z = fullZ;
        fullBar.localScale = fullScale;

        // Align left edge: position the center of fullBar so its left edge
        // matches the left edge of emptyBar. Assumes both pivots are centered.
        float newLocalZ = (-0.5f * emptyZ) + (0.5f * fullZ);

        Vector3 fullPos = fullBar.localPosition;
        fullPos.z = newLocalZ;
        fullBar.localPosition = fullPos;
    }
}
