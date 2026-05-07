using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

public class ShootingControls : MonoBehaviour
{
    [Header("Projectile Types")]
    [Tooltip("Array of projectile prefabs to cycle through (e.g. gray, blue, green, red, drone, plane drone, laser).")]
    [SerializeField] private GameObject[] projectilePrefabs;
    [SerializeField] private Transform firePoint;

    [Header("Inventory")]
    [Tooltip("Per-slot starting ammo. -1 = infinite (gray). 0 = locked, must be earned. >0 = N shots/seconds. " +
             "Length should match projectilePrefabs. Missing slots default to 0 (locked).")]
    [SerializeField] private int[] startingAmmo;

    [Tooltip("Per-slot ammo granted by a single crate pickup or enemy-tank-kill drop. " +
             "Length should match projectilePrefabs. For the laser slot, 1 unit = 1 second of beam time.")]
    [SerializeField] private int[] ammoPerPickup;

    [Tooltip("Per-slot damage override for ballistic projectiles. <=0 means \"use the default projectileDamage\". " +
             "Drone slots ignore this (they insta-kill via FlyingProjectile.TryKill). The laser uses its own DPS field on LaserBeam.")]
    [SerializeField] private int[] perSlotDamage;

    [Header("Firing")]
    [SerializeField] private float minProjectileVelocity = 10f;
    [SerializeField] private float maxProjectileVelocity = 40f;
    [SerializeField] private float maxChargeTime = 2f;
    [Tooltip("Default damage when perSlotDamage[slot] <= 0. Per-slot overrides take precedence.")]
    [SerializeField] private int projectileDamage = 25;
    [SerializeField] private PowerBar powerBar;

    [Header("Projectile Preview")]
    [Tooltip("How long the preview projectile appears above the tank (seconds).")]
    [SerializeField] private float previewDuration = 3f;
    [Tooltip("Target world-space size (longest axis, meters) for the preview projectile. " +
             "The preview is uniformly scaled so its largest renderer-bounds axis matches this, " +
             "so tiny balls scale up and large drones scale down to the same readable size.")]
    [SerializeField] private float previewTargetSize = 0.15f;
    [Tooltip("Override target size used when previewing a Projectile-type prefab (the small balls). " +
             "Defaults to ~50% of previewTargetSize so the ball previews don't dominate the view " +
             "the way drone previews do. Set <=0 to fall back to previewTargetSize for everything.")]
    [SerializeField] private float previewBallSize = 0.075f;
    [Tooltip("Height offset above the tank for the preview.")]
    [SerializeField] private float previewHeightOffset = 0.15f;

    private InputAction fireAction;
    private InputAction cycleProjectileAction;

    private int currentProjectileIndex = 0;
    private GameObject currentPreview;
    // Tracked alongside currentPreview so a fast cycle (within previewDuration) destroys the
    // old ammo label immediately instead of letting its still-running Destroy timer linger
    // and show stale ammo over the new model.
    private GameObject _currentAmmoLabel;

    // Per-slot ammo. -1 = infinite, 0 = locked / depleted, >0 = remaining shots (or seconds for laser).
    // Public state surface (IsUnlocked / TryUnlock) is a thin layer over this so callers don't need
    // to change as the inventory model evolved from boolean unlocks to depletable counts.
    private int[] _ammo;

    // Active laser instance, alive only while the trigger is held on a laser slot. Cleared on
    // release, on cycle, on ammo-out, or when this script is disabled (e.g. drone in flight).
    private LaserBeam _activeLaser;
    // Sub-second carry for laser ammo: ammo is integer-seconds, so we accumulate Time.deltaTime
    // and decrement once per whole second elapsed.
    private float _laserSecondAccumulator;
    // After the laser depletes mid-fire we don't want the trigger (still held) to instantly
    // start charging gray on the very next frame — that's surprising. This latch forces the
    // player to release and re-press before the next press-edge counts.
    private bool _laserRequiresTriggerRelease;

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

        // Debug toggle: flood every finite slot to 1000 for QA. Gray (-1) stays infinite.
        if (config != null && config.playerStartsWithFullAmmo && _ammo != null)
        {
            for (int i = 0; i < _ammo.Length; i++)
                if (_ammo[i] != -1) _ammo[i] = 1000;
        }
    }

    private void InitInventory()
    {
        int count = projectilePrefabs != null ? projectilePrefabs.Length : 0;
        _ammo = new int[count];
        for (int i = 0; i < count; i++)
        {
            // Missing entries default to 0 (locked) so a partly-configured prefab is safe.
            _ammo[i] = (startingAmmo != null && i < startingAmmo.Length) ? startingAmmo[i] : 0;
        }

        if (count > 0 && (currentProjectileIndex < 0 || currentProjectileIndex >= count || _ammo[currentProjectileIndex] == 0))
            currentProjectileIndex = FindFirstAvailableSlot();
    }

    /// <summary>
    /// True if the slot has any ammo (finite or infinite). Public surface preserved from the
    /// boolean-unlock era so external callers don't need to track ammo themselves.
    /// </summary>
    public bool IsUnlocked(int index)
    {
        return _ammo != null && index >= 0 && index < _ammo.Length && _ammo[index] != 0;
    }

    /// <summary>
    /// Adds <c>ammoPerPickup[index]</c> ammo to the slot. Returns true iff the slot was previously
    /// empty (preserves the old TryUnlock semantics so CrateReward's "first unlock" feedback path
    /// still fires correctly). When <paramref name="autoSwitch"/> is true and the unlock was newly
    /// granted, the active projectile switches to this slot and the preview spawns. Slots that are
    /// infinite (-1) are no-ops — there's nothing to add.
    /// </summary>
    public bool TryUnlock(int index, bool autoSwitch)
    {
        if (_ammo == null || index < 0 || index >= _ammo.Length) return false;
        if (_ammo[index] == -1) return false; // infinite; can't top up

        int amount = (ammoPerPickup != null && index < ammoPerPickup.Length) ? ammoPerPickup[index] : 0;
        if (amount <= 0) return false;

        bool wasLocked = _ammo[index] == 0;
        _ammo[index] += amount;

        if (autoSwitch && wasLocked)
        {
            currentProjectileIndex = index;
            ShowPreview();
        }
        else if (index == currentProjectileIndex)
        {
            // Crate top-up on the slot that's already shown — bump the live label so the
            // player sees the count rise without waiting for the next cycle.
            RefreshAmmoLabel();
        }
        return wasLocked;
    }

    /// <summary>Raw ammo accessor for HUD code. Returns -1 for infinite slots.</summary>
    public int AmmoFor(int index)
    {
        return (_ammo != null && index >= 0 && index < _ammo.Length) ? _ammo[index] : 0;
    }

    public bool IsInfiniteSlot(int index)
    {
        return AmmoFor(index) == -1;
    }

    /// <summary>
    /// Finds the first slot whose ammo is non-zero (positive or infinite). Used as the
    /// auto-switch target when the active slot empties. Falls back to slot 0 on a degenerate
    /// inventory where everything is locked — slot 0 is conventionally the gray ammo, infinite.
    /// </summary>
    private int FindFirstAvailableSlot()
    {
        if (_ammo == null) return 0;
        // Prefer an infinite slot first (gray) as the safe fallback, then any positive slot.
        for (int i = 0; i < _ammo.Length; i++)
            if (_ammo[i] == -1) return i;
        for (int i = 0; i < _ammo.Length; i++)
            if (_ammo[i] > 0) return i;
        return 0;
    }

    private int FindInfiniteSlot()
    {
        if (_ammo == null) return 0;
        for (int i = 0; i < _ammo.Length; i++)
            if (_ammo[i] == -1) return i;
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

        // Once the player physically releases the trigger, clear the laser-out latch so the
        // next press counts again.
        if (triggerReleased) _laserRequiresTriggerRelease = false;

        // WasPerformedThisFrame is already a one-frame pulse, no edge bookkeeping needed.
        bool aButtonPressed = cycleProjectileAction?.WasPerformedThisFrame() ?? false;

        if (aButtonPressed)
            CycleProjectile();

        bool slotIsLaser = IsLaserSlot();

        // Laser ammo tick — runs while the beam is alive. If the slot is finite and the
        // accumulator crosses a whole second, decrement ammo. Cut the beam when ammo hits 0
        // and arm the trigger-release latch so the still-held trigger doesn't immediately
        // begin charging gray.
        if (_activeLaser != null)
        {
            int slot = currentProjectileIndex;
            if (_ammo != null && slot >= 0 && slot < _ammo.Length && _ammo[slot] > 0)
            {
                int prevAmmo = _ammo[slot];
                _laserSecondAccumulator += Time.deltaTime;
                while (_laserSecondAccumulator >= 1f && _ammo[slot] > 0)
                {
                    _ammo[slot]--;
                    _laserSecondAccumulator -= 1f;
                }
                if (_ammo[slot] != prevAmmo) RefreshAmmoLabel();
                if (_ammo[slot] <= 0)
                {
                    DespawnLaser();
                    currentProjectileIndex = FindInfiniteSlot();
                    _laserRequiresTriggerRelease = true;
                    _laserSecondAccumulator = 0f;
                    RefreshAmmoLabel();
                }
            }
        }

        // Press: spawn beam on laser slot, otherwise begin charging. Suppressed entirely while
        // the trigger-release latch is set (set immediately after a laser-out auto-switch).
        if (triggerPressed && !_laserRequiresTriggerRelease)
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
        // Don't spawn a beam if the slot is already empty — defensive; cycle should have skipped it.
        if (_ammo != null && currentProjectileIndex < _ammo.Length && _ammo[currentProjectileIndex] == 0) return;

        GameObject prefab = CurrentPrefab;
        if (prefab == null || firePoint == null) return;

        GameObject obj = Instantiate(prefab, firePoint.position, firePoint.rotation, firePoint);
        _activeLaser = obj.GetComponent<LaserBeam>();
        if (_activeLaser != null)
            _activeLaser.Bind(firePoint, transform);

        _laserSecondAccumulator = 0f;
        // Light "power on" pulse so the player feels the laser engaging.
        Haptics.Pulse(this, OVRInput.Controller.RTouch, 0.5f, 0.6f, 0.06f);
    }

    private void DespawnLaser()
    {
        if (_activeLaser == null) return;
        Destroy(_activeLaser.gameObject);
        _activeLaser = null;
        _laserSecondAccumulator = 0f;
    }

    private void Shoot(float velocity)
    {
        GameObject prefab = CurrentPrefab;
        if (prefab == null || firePoint == null) return;

        // Capture the slot we're firing from BEFORE Bind() flips ShootingControls.enabled off
        // for the drone-flight path. Otherwise the post-flight re-enable might find the array
        // mid-mutation if Update fires anywhere in between.
        int firedSlot = currentProjectileIndex;

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

            // Decrement ammo BEFORE Bind disables this component, so the slot count is correct
            // when Bind's RestoreTankControls re-enables us after the drone explodes.
            DecrementAmmoAndAutoSwitch(firedSlot);

            flyer.Bind(gameObject);
            return;
        }

        Projectile proj = spawned.GetComponent<Projectile>();
        if (proj == null)
            proj = spawned.AddComponent<Projectile>();
        proj.damage = ResolveDamageForSlot(firedSlot);
        proj.shooter = transform;

        IgnoreSelfCollision(spawned);

        Rigidbody rb = spawned.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.linearVelocity = firePoint.forward * velocity;
        }

        DecrementAmmoAndAutoSwitch(firedSlot);
    }

    /// <summary>
    /// Per-slot damage lookup with a fallback to <see cref="projectileDamage"/>. A non-positive
    /// entry in <c>perSlotDamage</c> means "use the default" — keeps the inspector clean for
    /// drone/laser slots whose damage doesn't flow through Projectile.damage anyway.
    /// </summary>
    private int ResolveDamageForSlot(int slot)
    {
        if (perSlotDamage != null && slot >= 0 && slot < perSlotDamage.Length && perSlotDamage[slot] > 0)
            return perSlotDamage[slot];
        return projectileDamage;
    }

    /// <summary>
    /// Decrement the just-fired slot's ammo (no-op for -1 infinite). If the slot empties and
    /// it's still the active slot, jump to the first infinite slot (gray) so the next trigger
    /// pull doesn't fire from an empty slot.
    /// </summary>
    private void DecrementAmmoAndAutoSwitch(int firedSlot)
    {
        if (_ammo == null || firedSlot < 0 || firedSlot >= _ammo.Length) return;
        if (_ammo[firedSlot] == -1) return;

        _ammo[firedSlot] = Mathf.Max(0, _ammo[firedSlot] - 1);
        if (_ammo[firedSlot] == 0 && currentProjectileIndex == firedSlot)
            currentProjectileIndex = FindInfiniteSlot();

        RefreshAmmoLabel();
    }

    /// <summary>
    /// If a preview ammo label is currently floating above the tank, push the active slot's
    /// latest count into it. Called after every ammo mutation so the label ticks down in real
    /// time as the player fires (or up, when a crate top-up happens mid-preview).
    /// </summary>
    private void RefreshAmmoLabel()
    {
        if (_currentAmmoLabel == null) return;
        var label = _currentAmmoLabel.GetComponent<PreviewAmmoLabel>();
        if (label == null) return;
        int ammo = AmmoFor(currentProjectileIndex);
        label.SetText(ammo == -1 ? "∞" : ammo.ToString());
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
        if (_ammo == null || _ammo.Length != projectilePrefabs.Length) InitInventory();

        // Cycling out of (or while in) a laser fire mid-beam should end the beam cleanly.
        // Same for an in-progress charge — the new slot may not even support charging.
        DespawnLaser();
        if (isCharging)
        {
            isCharging = false;
            if (powerBar != null) powerBar.power = 0f;
        }

        // Walk forward from the current slot until we hit the next one with ammo. If only one
        // slot is available we fall back to the original index after a full loop (no-op cycle).
        int len = projectilePrefabs.Length;
        for (int step = 1; step <= len; step++)
        {
            int candidate = (currentProjectileIndex + step) % len;
            if (_ammo[candidate] != 0)
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
        if (_currentAmmoLabel != null)
            Destroy(_currentAmmoLabel);

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

        // Ball-type slots (Projectile component but no FlyingProjectile) fit to previewBallSize so
        // the small projectiles don't visually dominate the same way drones do. Drones, lasers
        // and anything unknown fall back to previewTargetSize.
        bool isBallPreview = !isLaserPreview
            && currentPreview.GetComponent<FlyingProjectile>() == null
            && currentPreview.GetComponent<Projectile>() != null;
        float fitSize = isBallPreview && previewBallSize > 0f ? previewBallSize : previewTargetSize;

        if (fitSize > 0f && !isLaserPreview)
        {
            var renderers = currentPreview.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                Bounds combined = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) combined.Encapsulate(renderers[i].bounds);
                float biggest = Mathf.Max(combined.size.x, combined.size.y, combined.size.z);
                if (biggest > 0.0001f)
                    currentPreview.transform.localScale *= fitSize / biggest;
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

        // Spawn the ammo-count label as a sibling of the preview (parented to the tank), at a
        // fixed world position above the preview spawn point. Parenting to the tank — not
        // the preview — keeps the label at scale (1,1,1) regardless of how aggressively the
        // preview was fit-scaled. "∞" for the infinite gray slot, integer count otherwise.
        // Stash the GameObject so a follow-up cycle within previewDuration destroys it instead
        // of letting the old "20" show stale over the new "5" preview.
        int ammo = AmmoFor(currentProjectileIndex);
        string ammoText = (ammo == -1) ? "∞" : ammo.ToString();
        var ammoLabel = PreviewAmmoLabel.Spawn(transform, previewPos, ammoText, previewDuration);
        _currentAmmoLabel = ammoLabel != null ? ammoLabel.gameObject : null;

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
