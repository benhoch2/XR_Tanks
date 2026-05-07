using System.Collections;
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
    [Tooltip("Target world-space size (longest axis, meters) for the preview projectile. " +
             "The preview is uniformly scaled so its largest renderer-bounds axis matches this, " +
             "so tiny balls scale up and large drones scale down to the same readable size.")]
    [SerializeField] private float previewTargetSize = 0.15f;
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

    // Active laser instance, alive only while the trigger is held on a laser slot. Cleared on
    // release, on cycle, or when this script is disabled (e.g. while a drone is in flight).
    private LaserBeam _activeLaser;

    private float chargeStartTime = 0f;
    private bool isCharging = false;
    private bool fullChargeNotified = false;

    private bool lastTriggerPressed = false;

    // Bookkeeping for the "hide power bar while a preview is on screen" coroutine. We hold the
    // handle so a back-to-back cycle stops the previous restore-timer instead of letting it fire
    // mid-second-preview and re-show the bar over the new model.
    private Coroutine _hidePowerBarCo;

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
        // If the script is disabled mid-fire (e.g. when a drone Bind() suspends us), kill the
        // beam so it doesn't keep raycasting and damaging while the player has switched modes.
        DespawnLaser();
        isCharging = false;
        if (powerBar != null) powerBar.power = 0f;

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

        bool slotIsLaser = IsLaserSlot();

        // Press: spawn beam on laser slot, otherwise begin charging.
        if (triggerPressed)
        {
            if (slotIsLaser)
            {
                SpawnLaser();
            }
            else
            {
                chargeStartTime = Time.time;
                isCharging = true;
                fullChargeNotified = false;
                if (powerBar != null) powerBar.power = 0f;
            }
        }

        // Charge bar update only applies to ballistic slots; the laser doesn't charge.
        if (isCharging && !slotIsLaser)
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

        // Release: stop laser beam if active, otherwise fire the charged ballistic.
        if (triggerReleased)
        {
            if (_activeLaser != null)
            {
                DespawnLaser();
            }
            else if (isCharging)
            {
                float chargeDuration = Mathf.Clamp(Time.time - chargeStartTime, 0f, maxChargeTime);
                float t = (powerBar != null) ? Mathf.Clamp01(powerBar.power) : ((maxChargeTime > 0f) ? chargeDuration / maxChargeTime : 1f);
                float velocity = Mathf.Lerp(minProjectileVelocity, maxProjectileVelocity, t);
                Shoot(velocity);
                isCharging = false;
                if (powerBar != null) powerBar.power = 0f;
            }
        }
    }

    private bool IsLaserSlot()
    {
        GameObject prefab = CurrentPrefab;
        return prefab != null && prefab.GetComponent<LaserBeam>() != null;
    }

    private void SpawnLaser()
    {
        if (_activeLaser != null) return;
        GameObject prefab = CurrentPrefab;
        if (prefab == null || firePoint == null) return;

        GameObject obj = Instantiate(prefab, firePoint.position, firePoint.rotation, firePoint);
        _activeLaser = obj.GetComponent<LaserBeam>();
        if (_activeLaser != null)
            _activeLaser.Bind(firePoint, transform);

        // Light "power on" pulse so the player feels the laser engaging.
        Haptics.Pulse(this, OVRInput.Controller.RTouch, 0.5f, 0.6f, 0.06f);
    }

    private void DespawnLaser()
    {
        if (_activeLaser == null) return;
        Destroy(_activeLaser.gameObject);
        _activeLaser = null;
    }

    private void Shoot(float velocity)
    {
        GameObject prefab = CurrentPrefab;
        if (prefab == null || firePoint == null) return;

        // Crisper "thunk" pulse on fire. Stronger than the charge ping.
        Haptics.Pulse(this, OVRInput.Controller.RTouch, 0.5f, 0.8f, 0.1f);

        GameObject spawned = Instantiate(prefab, firePoint.position, firePoint.rotation);

        // FlyingProjectile (Drone, Plane, future variants) is a special weapon class: it spawns
        // as a player-flown weapon rather than a high-velocity ballistic projectile, and grabs
        // the player's input until it explodes. Detect via the base class so adding new flight
        // models doesn't require changes here.
        FlyingProjectile flyer = spawned.GetComponent<FlyingProjectile>();
        if (flyer != null)
        {
            ConfigureFlyingProjectileRigidbody(spawned);
            IgnoreSelfCollision(spawned);
            flyer.Bind(gameObject);
            return;
        }

        Projectile proj = spawned.GetComponent<Projectile>();
        if (proj == null)
            proj = spawned.AddComponent<Projectile>();
        proj.damage = projectileDamage;
        proj.shooter = transform;

        IgnoreSelfCollision(spawned);

        Rigidbody rb = spawned.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.linearVelocity = firePoint.forward * velocity;
        }
    }

    // Make sure the flyer's Rigidbody is set up for player-driven flight rather than gravity-fall
    // ballistic motion. We give it a small forward "release" velocity so the tank visibly ejects
    // it, then the FlyingProjectile takes over and drives velocity from stick input.
    private void ConfigureFlyingProjectileRigidbody(GameObject obj)
    {
        Rigidbody rb = obj.GetComponent<Rigidbody>();
        if (rb == null) return;
        rb.useGravity = false;
        rb.linearDamping = 0f;
        rb.angularDamping = 0f;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.linearVelocity = firePoint.forward * 0.5f;
    }

    private void IgnoreSelfCollision(GameObject spawned)
    {
        Collider spawnedCol = spawned.GetComponent<Collider>();
        if (spawnedCol == null) return;
        foreach (var tankCol in GetComponentsInChildren<Collider>())
            Physics.IgnoreCollision(spawnedCol, tankCol);
    }

    private void CycleProjectile()
    {
        if (projectilePrefabs == null || projectilePrefabs.Length == 0) return;
        if (_unlocked == null || _unlocked.Length != projectilePrefabs.Length) InitInventory();

        // Cycling out of (or while in) a laser fire mid-beam should end the beam cleanly.
        // Same for an in-progress charge — the new slot may not even support charging.
        DespawnLaser();
        if (isCharging)
        {
            isCharging = false;
            if (powerBar != null) powerBar.power = 0f;
        }

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
        // Spawn with the tank's rotation so the floating preview faces wherever the body
        // points — visually consistent with how the projectile launches when fired.
        currentPreview = Instantiate(CurrentPrefab, previewPos, transform.rotation, transform);
        currentPreview.name = "ProjectilePreview";

        // Drone / Plane prefabs read joystick input every FixedUpdate and rotate themselves to
        // face the player's command. We don't want that on a preview model — disable any
        // FlyingProjectile-derived script so the preview hangs in place at the spawn rotation.
        var flyer = currentPreview.GetComponent<FlyingProjectile>();
        if (flyer != null) flyer.enabled = false;

        // The laser preview is a LineRenderer whose bounds extend from world origin to
        // wherever the prefab's beam endpoint sits — useless for fitting to previewTargetSize.
        // Skip the auto-scale for laser slots and let LaserBeam.Awake render its own placeholder.
        bool isLaserPreview = currentPreview.GetComponent<LaserBeam>() != null;

        if (previewTargetSize > 0f && !isLaserPreview)
        {
            var renderers = currentPreview.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                Bounds combined = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) combined.Encapsulate(renderers[i].bounds);
                float biggest = Mathf.Max(combined.size.x, combined.size.y, combined.size.z);
                if (biggest > 0.0001f)
                    currentPreview.transform.localScale *= previewTargetSize / biggest;
            }
        }

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

        // Hide every PowerBar on the tank (charge bar AND health bar — both read as bobbing
        // banners next to the floating preview). Restored after previewDuration via a tracked
        // coroutine so a follow-up cycle doesn't get its bars re-shown by the previous timer
        // firing mid-preview. We grab them fresh on every preview rather than caching, since
        // the bars aren't expected to be added/removed during play.
        var bars = GetComponentsInChildren<PowerBar>(true);
        for (int i = 0; i < bars.Length; i++)
        {
            if (bars[i] != null) bars[i].gameObject.SetActive(false);
        }
        if (_hidePowerBarCo != null) StopCoroutine(_hidePowerBarCo);
        _hidePowerBarCo = StartCoroutine(RestorePowerBarsAfterPreview(bars, previewDuration));
    }

    private IEnumerator RestorePowerBarsAfterPreview(PowerBar[] bars, float delay)
    {
        yield return new WaitForSeconds(delay);
        for (int i = 0; i < bars.Length; i++)
        {
            if (bars[i] != null) bars[i].gameObject.SetActive(true);
        }
        _hidePowerBarCo = null;
    }
}
