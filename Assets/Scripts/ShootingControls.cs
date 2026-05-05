using UnityEngine;
using UnityEngine.InputSystem;

public class ShootingControls : MonoBehaviour
{
    [Header("Projectile Types")]
    [Tooltip("Array of projectile prefabs to cycle through (e.g. gray, green, red, blue).")]
    [SerializeField] private GameObject[] projectilePrefabs;
    [SerializeField] private Transform firePoint;

    [Header("Inventory")]
    [Tooltip("Per-slot unlocked state at scene start. Length should match projectilePrefabs. " +
             "If the array is empty or shorter, missing slots default to unlocked (back-compat). " +
             "Player tanks set this to [true,false,...] so projectile types must be earned from crates.")]
    [SerializeField] private bool[] unlockedAtStart;

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

    // Per-slot unlock state, initialized from unlockedAtStart in Start. Forward-compat note:
    // when we move to depletable ammo, swap this for int[] _ammo and treat >0 as unlocked;
    // the public IsUnlocked / TryUnlock surface stays the same shape for external callers.
    private bool[] _unlocked;

    private float chargeStartTime = 0f;
    private bool isCharging = false;
    private bool fullChargeNotified = false;

    private bool lastTriggerPressed = false;

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

        InitInventory();
    }

    private void InitInventory()
    {
        int count = projectilePrefabs != null ? projectilePrefabs.Length : 0;
        _unlocked = new bool[count];
        for (int i = 0; i < count; i++)
        {
            // If unlockedAtStart isn't configured (or is shorter), default missing slots to
            // unlocked so older prefabs without the field keep working.
            _unlocked[i] = (unlockedAtStart == null || i >= unlockedAtStart.Length) || unlockedAtStart[i];
        }

        if (count > 0 && (currentProjectileIndex < 0 || currentProjectileIndex >= count || !_unlocked[currentProjectileIndex]))
            currentProjectileIndex = FirstUnlocked();
    }

    public bool IsUnlocked(int index)
    {
        return _unlocked != null && index >= 0 && index < _unlocked.Length && _unlocked[index];
    }

    /// <summary>
    /// Unlocks the projectile slot at <paramref name="index"/>. Returns true if the slot
    /// was previously locked and is now unlocked (so callers can decide whether to surface
    /// "got new ammo" feedback). When <paramref name="autoSwitch"/> is true and the unlock
    /// was newly granted, the active projectile switches to it and the preview spawns.
    /// </summary>
    public bool TryUnlock(int index, bool autoSwitch)
    {
        if (_unlocked == null || index < 0 || index >= _unlocked.Length) return false;
        if (_unlocked[index]) return false;

        _unlocked[index] = true;
        if (autoSwitch)
        {
            currentProjectileIndex = index;
            ShowPreview();
        }
        return true;
    }

    private int FirstUnlocked()
    {
        if (_unlocked == null) return 0;
        for (int i = 0; i < _unlocked.Length; i++)
            if (_unlocked[i]) return i;
        return 0;
    }

    void OnEnable()
    {
        fireAction = new InputAction("Fire", InputActionType.Value, "<XRController>{RightHand}/trigger");
        cycleProjectileAction = new InputAction("CycleProjectile", InputActionType.Button, "<XRController>{RightHand}/primaryButton");
        fireAction.Enable();
        cycleProjectileAction.Enable();
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
        if (triggerPressed)
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
        }
    }

    private void CycleProjectile()
    {
        if (projectilePrefabs == null || projectilePrefabs.Length == 0) return;
        if (_unlocked == null || _unlocked.Length != projectilePrefabs.Length) InitInventory();

        // Walk forward from the current slot until we hit the next unlocked one. If only one
        // slot is unlocked we fall back to the original index after a full loop (no-op cycle).
        int len = projectilePrefabs.Length;
        for (int step = 1; step <= len; step++)
        {
            int candidate = (currentProjectileIndex + step) % len;
            if (_unlocked[candidate])
            {
                currentProjectileIndex = candidate;
                ShowPreview();
                return;
            }
        }
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
