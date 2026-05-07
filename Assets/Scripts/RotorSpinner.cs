using UnityEngine;

/// <summary>
/// Rotates a fixed set of Transforms at a constant rate, used to spin the visible blade
/// props that sit on top of the static drone-body FBX. The Hyper3D-generated drone meshes
/// have their rotors fused into a single mesh with thousands of fragmented loose parts, so
/// we can't animate them in place — instead the prefab carries lightweight "blade" cube
/// children whose pivots this script rotates.
///
/// One spinner can drive multiple rotor pivots. Quadcopter prefabs wire all four; the plane
/// prefab wires its single nose propeller and switches the spin axis to forward.
/// </summary>
public class RotorSpinner : MonoBehaviour
{
    [Tooltip("Pivot transforms to rotate. Each spins independently around the configured axis. If left empty, Awake auto-discovers direct children whose name starts with \"Rotor\" or \"Propeller\".")]
    [SerializeField] private Transform[] rotors;

    [Tooltip("Rotation speed in degrees per second. 360 = one revolution / second. Kept low (~240) so individual blades read distinctly rather than blurring out.")]
    [SerializeField] private float degreesPerSecond = 240f;

    [Tooltip("Local-space axis the rotors spin around. (0,1,0) for a quadcopter (rotors face up); (0,0,1) for the plane's nose-mounted propeller (faces forward).")]
    [SerializeField] private Vector3 axis = Vector3.up;

    private void Awake()
    {
        if (rotors != null && rotors.Length > 0) return;
        // Auto-discover by name. Recurses through the entire hierarchy so the rotor sub-meshes
        // can sit deep inside a nested model-prefab Visual (which is how the FBX import lays
        // them out — Drone/Visual/RotorFL etc. rather than Drone/RotorFL).
        var found = new System.Collections.Generic.List<Transform>();
        var all = GetComponentsInChildren<Transform>(includeInactive: true);
        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i];
            if (t == transform) continue;
            if (t.name.StartsWith("Rotor") || t.name.StartsWith("Propeller"))
                found.Add(t);
        }
        rotors = found.ToArray();
    }

    private void Update()
    {
        if (rotors == null || rotors.Length == 0) return;
        float deg = degreesPerSecond * Time.deltaTime;
        for (int i = 0; i < rotors.Length; i++)
        {
            if (rotors[i] != null)
                rotors[i].Rotate(axis, deg, Space.Self);
        }
    }
}
