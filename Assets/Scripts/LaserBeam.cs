using UnityEngine;

/// <summary>
/// Continuous laser-beam weapon. Spawned by <see cref="ShootingControls"/> as a child of the
/// player tank's firePoint while the trigger is held; the same controller despawns it on
/// release or when the player cycles to a different slot. Each frame: raycast from firePoint
/// forward, render a LineRenderer to the hit point (or max range), and apply DPS damage to any
/// <see cref="Target"/> the beam is currently touching. Crates are insta-killed on touch.
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class LaserBeam : MonoBehaviour
{
    [Tooltip("Maximum distance the beam reaches from the firePoint. Beyond this, the beam draws to range with no hit.")]
    [SerializeField] private float maxRange = 8f;

    [Tooltip("Damage per second applied to a Target while the beam is hitting it. Fractional accumulates.")]
    [SerializeField] private float damagePerSecond = 50f;

    [Tooltip("Layers the beam can hit. Default = everything; restrict if you want the beam to ignore environment colliders.")]
    [SerializeField] private LayerMask hitMask = ~0;

    [Tooltip("Beam line width in meters.")]
    [SerializeField] private float beamWidth = 0.008f;

    [Tooltip("Base beam color before the HDR multiplier and per-frame animation are applied.")]
    [SerializeField] private Color beamColor = new Color(1f, 0.15f, 0.15f, 1f);

    [Tooltip("HDR multiplier applied to beamColor. Values >1 produce HDR colors that drive bloom post-processing if enabled on the URP volume. Kept low (~1.5) by default so the beam reads correctly even when the project doesn't have HDR/Bloom enabled — otherwise an over-1 channel would clip to white on the display.")]
    [SerializeField] private float beamIntensity = 1.5f;

    [Header("Color animation")]
    [Tooltip("Peak hue shift (degrees) around the base color. The current hue oscillates ±this each cycle, giving the beam a living, shifting tone. 0 disables.")]
    [SerializeField] private float hueOscillationDeg = 15f;

    [Tooltip("Hue oscillation rate, cycles per second.")]
    [SerializeField] private float hueOscillationSpeed = 3f;

    [Tooltip("Peak brightness wobble as a fraction of the base intensity (0.25 = ±25%). 0 disables.")]
    [SerializeField] private float intensityOscillation = 0.25f;

    [Tooltip("Brightness wobble rate, cycles per second. Different from hue speed so the two oscillations don't sync up — more lively feel.")]
    [SerializeField] private float intensityOscillationSpeed = 5f;

    [Header("Impact light")]
    [Tooltip("Intensity of the point light that sits at the beam's impact point and illuminates nearby surfaces. 0 disables the light entirely.")]
    [SerializeField] private float impactLightIntensity = 3f;

    [Tooltip("Range of the impact light, in meters. Keep small so it only illuminates the immediate surface where the beam lands.")]
    [SerializeField] private float impactLightRange = 0.6f;

    private LineRenderer _lr;
    private Light _impactLight;
    private Transform _firePoint;
    private Transform _shooterRoot;
    private Collider[] _selfColliders;
    private float _damageAccumulator;
    // Reused so ApplyAnimatedColor doesn't allocate a fresh block every frame.
    private MaterialPropertyBlock _propBlock;

    private void Awake()
    {
        _lr = GetComponent<LineRenderer>();
        // Default to LOCAL space with a short forward line so the prefab renders something even
        // when nothing has called Bind() — used by ShootingControls.ShowPreview to display the
        // laser slot in the floating-preview UI. Bind() flips this back to world space when the
        // beam is actually being fired.
        _lr.useWorldSpace = false;
        _lr.startWidth = beamWidth;
        _lr.endWidth = beamWidth;
        _lr.positionCount = 2;
        _lr.SetPosition(0, Vector3.zero);
        _lr.SetPosition(1, new Vector3(0f, 0f, 0.1f));
        _lr.numCapVertices = 4;

        // The prefab may not ship with a material; assign a sensible default so the beam doesn't
        // render pink. URP/Unlit honors the LineRenderer color tint and respects HDR values for
        // bloom post-processing when the URP volume has Bloom enabled. Material is white so the
        // per-frame LineRenderer color drives the visible hue without being multiplied down.
        if (_lr.sharedMaterial == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                var mat = new Material(shader);
                mat.color = Color.white;
                _lr.material = mat;
            }
        }

        EnsureImpactLight();

        // Apply the color once before the first Update so the beam doesn't render at white for
        // a single frame after spawn.
        ApplyAnimatedColor();
    }

    private void ApplyAnimatedColor()
    {
        float t = Time.time;
        float hueShift01 = (hueOscillationDeg / 360f) * Mathf.Sin(t * hueOscillationSpeed * Mathf.PI * 2f);
        float intensityScale = 1f + intensityOscillation * Mathf.Sin(t * intensityOscillationSpeed * Mathf.PI * 2f);

        Color.RGBToHSV(beamColor, out float h, out float s, out float v);
        h = Mathf.Repeat(h + hueShift01, 1f);
        Color animated = Color.HSVToRGB(h, s, v);
        animated.a = beamColor.a;

        float intensity = Mathf.Max(0.0001f, beamIntensity * intensityScale);
        Color hdr = new Color(animated.r * intensity, animated.g * intensity, animated.b * intensity, animated.a);

        _lr.startColor = hdr;
        _lr.endColor = hdr;

        // The default LaserBeam_mat uses URP/Unlit which does NOT multiply by LineRenderer's
        // per-vertex color, so startColor/endColor alone wouldn't tint the visible line. Push
        // the color into _BaseColor via a property block so the material picks it up without
        // forcing a per-instance material clone.
        if (_propBlock == null) _propBlock = new MaterialPropertyBlock();
        _lr.GetPropertyBlock(_propBlock);
        _propBlock.SetColor("_BaseColor", hdr);
        _propBlock.SetColor("_Color", hdr);
        _lr.SetPropertyBlock(_propBlock);

        if (_impactLight != null)
        {
            _impactLight.color = animated;
            _impactLight.intensity = Mathf.Max(0f, impactLightIntensity * intensityScale);
        }
    }

    private void EnsureImpactLight()
    {
        if (impactLightIntensity <= 0f) return;

        // Create the impact light as a child if one isn't already authored on the prefab.
        // Spawning it under transform keeps it tidy and ensures Destroy(gameObject) cleans it up.
        var lightGO = new GameObject("ImpactLight");
        lightGO.transform.SetParent(transform, worldPositionStays: false);
        _impactLight = lightGO.AddComponent<Light>();
        _impactLight.type = LightType.Point;
        _impactLight.color = beamColor;
        _impactLight.intensity = impactLightIntensity;
        _impactLight.range = impactLightRange;
        _impactLight.shadows = LightShadows.None;
        _impactLight.renderMode = LightRenderMode.Auto;
    }

    /// <summary>
    /// Wires up the beam after spawn. <paramref name="firePoint"/> is the tank's muzzle
    /// transform — the beam starts at firePoint.position and shoots along firePoint.forward.
    /// <paramref name="shooterRoot"/> is the player tank root, used to skip the player's own
    /// colliders so the beam doesn't immediately damage the launcher.
    /// </summary>
    public void Bind(Transform firePoint, Transform shooterRoot)
    {
        _firePoint = firePoint;
        _shooterRoot = shooterRoot;
        _selfColliders = shooterRoot != null
            ? shooterRoot.GetComponentsInChildren<Collider>(true)
            : System.Array.Empty<Collider>();
        // Switch back to world space now that we're actually firing — the per-frame Update
        // sets the line endpoints in world coordinates from firePoint.position.
        if (_lr != null) _lr.useWorldSpace = true;
    }

    private void Update()
    {
        if (_firePoint == null)
        {
            // No firePoint binding — we're in "preview mode" (ShootingControls.ShowPreview
            // spawned us as a floating placeholder). Don't raycast, don't damage, don't
            // self-destruct (the preview owner manages our lifetime). Just keep the color
            // animation alive so the line pulses like the live beam.
            ApplyAnimatedColor();
            return;
        }

        Vector3 origin = _firePoint.position;
        Vector3 direction = _firePoint.forward;
        Vector3 endPoint = origin + direction * maxRange;

        // Walk all hits in distance order so the player tank's own colliders (which usually sit
        // between firePoint and the world) don't masquerade as the first impact. Same approach
        // EnemyTankAI.HasLineOfSightToPlayer uses for its LOS raycast.
        RaycastHit[] hits = Physics.RaycastAll(origin, direction, maxRange, hitMask, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        Target hitTarget = null;
        foreach (var hit in hits)
        {
            if (hit.transform == null) continue;
            if (_shooterRoot != null && (hit.transform == _shooterRoot || hit.transform.IsChildOf(_shooterRoot)))
                continue;

            endPoint = hit.point;
            hitTarget = hit.transform.GetComponent<Target>();
            if (hitTarget == null) hitTarget = hit.transform.GetComponentInParent<Target>();
            break;
        }

        _lr.SetPosition(0, origin);
        _lr.SetPosition(1, endPoint);

        if (_impactLight != null)
        {
            // Park the point-light just shy of the impact surface so its falloff illuminates
            // the surface clearly without being clipped behind it.
            _impactLight.transform.position = endPoint - direction * 0.02f;
        }

        ApplyAnimatedColor();

        if (hitTarget == null)
        {
            _damageAccumulator = 0f;
            return;
        }

        // Crates have maxHitPoints == 0 and use the instant-kill path; TakeDamage no-ops on
        // them. Touching one with the beam pops it instantly.
        if (hitTarget.maxHitPoints <= 0)
        {
            _damageAccumulator = 0f;
            hitTarget.Kill();
            return;
        }

        // Fractional damage accumulates so very low DPS still applies eventually rather than
        // truncating to zero each frame.
        _damageAccumulator += damagePerSecond * Time.deltaTime;
        int wholeDamage = Mathf.FloorToInt(_damageAccumulator);
        if (wholeDamage > 0)
        {
            _damageAccumulator -= wholeDamage;
            hitTarget.TakeDamage(wholeDamage);
        }
    }
}
