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

    private bool lastTriggerPressed = false;
    private bool lastAButtonPressed = false;

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
        bool triggerPressed = false;
        bool triggerReleased = false;
        bool aButtonPressed = false;

        float triggerValue = fireAction?.ReadValue<float>() ?? 0f;
        bool isPressedNow = triggerValue > 0.1f;
        triggerPressed = isPressedNow && !lastTriggerPressed;
        triggerReleased = !isPressedNow && lastTriggerPressed;
        lastTriggerPressed = isPressedNow;

        bool aValue = cycleProjectileAction?.WasPerformedThisFrame() ?? false;
        aButtonPressed = aValue && !lastAButtonPressed;
        lastAButtonPressed = aValue;

        // Cycle projectile type
        if (aButtonPressed)
        {
            CycleProjectile();
        }

        // Start charging
        if (triggerPressed)
        {
            chargeStartTime = Time.time;
            isCharging = true;
            if (powerBar != null) powerBar.power = 0f;
        }

        // Update slider while charging
        if (isCharging)
        {
            float chargeDurationNow = Mathf.Clamp(Time.time - chargeStartTime, 0f, maxChargeTime);
            float normalized = (maxChargeTime > 0f) ? (chargeDurationNow / maxChargeTime) : 1f;
            normalized = Mathf.Clamp01(normalized);
            if (powerBar != null) powerBar.power = normalized;
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

        currentProjectileIndex = (currentProjectileIndex + 1) % projectilePrefabs.Length;
        Debug.Log($"Projectile switched to: {CurrentPrefab.name} ({currentProjectileIndex + 1}/{projectilePrefabs.Length})");

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

        Destroy(currentPreview, previewDuration);
    }
}
