using UnityEngine;
using UnityEngine.InputSystem;

public class ShootingControls : MonoBehaviour
{
    [Header("Projectile Types")]
    [Tooltip("Array of projectile prefabs to cycle through (e.g. gray, green, red, blue).")]
    [SerializeField] private GameObject[] projectilePrefabs;
    [SerializeField] private Transform firePoint;

    [Header("Firing")]
    [SerializeField] private float minProjectileVelocity = 10f;
    [SerializeField] private float maxProjectileVelocity = 40f;
    [SerializeField] private float maxChargeTime = 2f;
    [SerializeField] private int projectileDamage = 25;
    [SerializeField] private PowerBar powerBar;

    [Header("Projectile Preview")]
    [Tooltip("How long the preview projectile appears above the tank (seconds).")]
    [SerializeField] private float previewDuration = 3f;
    [Tooltip("Scale multiplier for the preview projectile.")]
    [SerializeField] private float previewScale = 3f;
    [Tooltip("Height offset above the tank for the preview.")]
    [SerializeField] private float previewHeightOffset = 0.15f;

    private InputAction fireAction;
    private InputAction cycleProjectileAction;

    private int currentProjectileIndex = 0;
    private GameObject currentPreview;

    private float chargeStartTime = 0f;
    private bool isCharging = false;
    private bool fullChargeNotified = false;

    private bool lastTriggerPressed = false;

    /// <summary>Shots left before firing is blocked. -1 = unlimited (real-time play); the turn-based match sets 1 per turn.</summary>
    public int ShotsRemaining { get; set; } = -1;

    /// <summary>Raised with the projectile right after it is launched.</summary>
    public event System.Action<Projectile> ShotFired;

    public bool IsCharging => isCharging;
    public Transform FirePoint => firePoint;
    public int ProjectileDamage => projectileDamage;

    public GameObject GetProjectilePrefab(int index) =>
        projectilePrefabs != null && index >= 0 && index < projectilePrefabs.Length ? projectilePrefabs[index] : null;

    /// <summary>Launch speed the shot would leave with if released now (the minimum while not charging).</summary>
    public float CurrentLaunchSpeed =>
        Mathf.Lerp(minProjectileVelocity, maxProjectileVelocity, isCharging && powerBar != null ? Mathf.Clamp01(powerBar.power) : 0f);

    private GameObject CurrentPrefab =>
        (projectilePrefabs != null && projectilePrefabs.Length > 0)
            ? projectilePrefabs[currentProjectileIndex]
            : null;

    void Start()
    {
        var config = GameConfigManager.Instance;
        if (config != null)
        {
            maxChargeTime = config.powerUpDuration;
            minProjectileVelocity = config.projectileMinSpeed;
            maxProjectileVelocity = config.projectileMaxSpeed;
        }

        if (powerBar != null) powerBar.power = 0f;
    }

    void OnEnable()
    {
        fireAction = new InputAction("Fire", InputActionType.Value, "<XRController>{RightHand}/trigger");
        cycleProjectileAction = new InputAction("CycleProjectile", InputActionType.Button, "<XRController>{RightHand}/primaryButton");
        fireAction.Enable();
        cycleProjectileAction.Enable();

        // Drop any charge left over from before the last disable (turn-based hands control
        // back and forth): a stale "was pressed" would otherwise fire on the first frame.
        isCharging = false;
        lastTriggerPressed = false;
        if (powerBar != null) powerBar.power = 0f;
    }

    void OnDisable()
    {
        fireAction?.Disable();
        cycleProjectileAction?.Disable();
        fireAction?.Dispose();
        cycleProjectileAction?.Dispose();
        fireAction = null;
        cycleProjectileAction = null;
    }

    void Update()
    {
        float triggerValue = fireAction?.ReadValue<float>() ?? 0f;
        bool isPressedNow = triggerValue > 0.1f;
        bool triggerPressed = isPressedNow && !lastTriggerPressed;
        bool triggerReleased = !isPressedNow && lastTriggerPressed;
        lastTriggerPressed = isPressedNow;

        // WasPerformedThisFrame is already a one-frame pulse, no edge bookkeeping needed.
        bool aButtonPressed = cycleProjectileAction?.WasPerformedThisFrame() ?? false;

        if (aButtonPressed)
            CycleProjectile();

        // Start charging
        if (triggerPressed && ShotsRemaining != 0)
        {
            chargeStartTime = Time.time;
            isCharging = true;
            fullChargeNotified = false;
            if (powerBar != null) powerBar.power = 0f;
        }

        // Update slider while charging
        if (isCharging)
        {
            float chargeDurationNow = Mathf.Clamp(Time.time - chargeStartTime, 0f, maxChargeTime);
            float normalized = (maxChargeTime > 0f) ? (chargeDurationNow / maxChargeTime) : 1f;
            normalized = Mathf.Clamp01(normalized);
            if (powerBar != null) powerBar.power = normalized;

            // Single haptic ping when the charge bar first hits 100% so the player knows
            // they're at max power without looking at the bar.
            if (!fullChargeNotified && normalized >= 1f)
            {
                fullChargeNotified = true;
                Haptics.Pulse(this, OVRInput.Controller.RTouch, 0.3f, 0.5f, 0.06f);
            }
        }

        // Fire
        if (triggerReleased && isCharging)
        {
            float chargeDuration = Mathf.Clamp(Time.time - chargeStartTime, 0f, maxChargeTime);
            float t = (powerBar != null) ? Mathf.Clamp01(powerBar.power) : ((maxChargeTime > 0f) ? chargeDuration / maxChargeTime : 1f);
            float velocity = Mathf.Lerp(minProjectileVelocity, maxProjectileVelocity, t);
            Shoot(velocity);
            isCharging = false;
            if (powerBar != null) powerBar.power = 0f;
        }
    }

    private void Shoot(float velocity)
    {
        GameObject prefab = CurrentPrefab;
        if (prefab != null && firePoint != null)
        {
            // Crisper "thunk" pulse on fire. Stronger than the charge ping.
            Haptics.Pulse(this, OVRInput.Controller.RTouch, 0.5f, 0.8f, 0.1f);

            // Online: the host spawns and simulates the shot (see NetworkTankControl).
            if (TryGetComponent(out NetworkTankControl network) && network.IsSpawned)
            {
                network.RequestFire(currentProjectileIndex, firePoint.position, firePoint.rotation, firePoint.forward * velocity);
                if (ShotsRemaining > 0)
                    ShotsRemaining--;
                ShotFired?.Invoke(null);
                return;
            }

            GameObject projectile = Instantiate(prefab, firePoint.position, firePoint.rotation);

            Projectile proj = projectile.GetComponent<Projectile>();
            if (proj == null)
                proj = projectile.AddComponent<Projectile>();
            proj.damage = projectileDamage;
            proj.shooter = transform;

            // Ignore collisions between projectile and the player tank
            Collider projCol = projectile.GetComponent<Collider>();
            if (projCol != null)
            {
                foreach (var tankCol in GetComponentsInChildren<Collider>())
                    Physics.IgnoreCollision(projCol, tankCol);
            }

            Rigidbody rb = projectile.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.linearVelocity = firePoint.forward * velocity;
            }

            if (ShotsRemaining > 0)
                ShotsRemaining--;
            ShotFired?.Invoke(proj);
        }
    }

    private void CycleProjectile()
    {
        if (projectilePrefabs == null || projectilePrefabs.Length == 0) return;

        currentProjectileIndex = (currentProjectileIndex + 1) % projectilePrefabs.Length;
        ShowPreview();
    }

    private void ShowPreview()
    {
        // Destroy previous preview if still active
        if (currentPreview != null)
            Destroy(currentPreview);

        if (CurrentPrefab == null) return;

        Vector3 previewPos = transform.position + Vector3.up * previewHeightOffset;
        currentPreview = Instantiate(CurrentPrefab, previewPos, Quaternion.identity, transform);
        currentPreview.transform.localScale = CurrentPrefab.transform.localScale * previewScale;
        currentPreview.name = "ProjectilePreview";

        // Disable physics so the preview just floats
        Rigidbody rb = currentPreview.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.detectCollisions = false;
        }

        // Disable collider so it doesn't interfere
        Collider col = currentPreview.GetComponent<Collider>();
        if (col != null)
            col.enabled = false;

        // Blink while visible so it reads as a UI ping rather than a static prop.
        currentPreview.AddComponent<BlinkRenderer>();

        Destroy(currentPreview, previewDuration);
    }
}
