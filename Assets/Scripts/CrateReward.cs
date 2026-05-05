using UnityEngine;

/// <summary>
/// Attached to crate prefabs (e.g. TargetBox.prefab). When the crate is destroyed by a player
/// shot, <see cref="Target.HandleHit"/> calls <see cref="RollAndApply"/> with the player tank
/// root as the recipient. Rolls a weighted random outcome and either unlocks a projectile slot
/// on the player's <see cref="ShootingControls"/> or heals via <see cref="Target.Heal"/>.
/// </summary>
public class CrateReward : MonoBehaviour
{
    public enum CrateRewardType
    {
        Nothing,
        BlueAmmo,
        GreenAmmo,
        RedAmmo,
        Health
    }

    [Header("Probabilities (must sum to ~1)")]
    [Range(0f, 1f), SerializeField] private float pctNothing = 0.5f;
    [Range(0f, 1f), SerializeField] private float pctBlueAmmo = 0.15f;
    [Range(0f, 1f), SerializeField] private float pctGreenAmmo = 0.15f;
    [Range(0f, 1f), SerializeField] private float pctRedAmmo = 0.15f;
    [Range(0f, 1f), SerializeField] private float pctHealth = 0.05f;

    [Header("Projectile slot indices on player ShootingControls")]
    [Tooltip("Index in ShootingControls.projectilePrefabs that the BlueAmmo reward should unlock.")]
    [SerializeField] private int blueIndex = 1;
    [Tooltip("Index in ShootingControls.projectilePrefabs that the GreenAmmo reward should unlock.")]
    [SerializeField] private int greenIndex = 2;
    [Tooltip("Index in ShootingControls.projectilePrefabs that the RedAmmo reward should unlock.")]
    [SerializeField] private int redIndex = 3;

    [Header("Health")]
    [Tooltip("Hit points restored to the player on a Health roll. Capped at the player Target's maxHitPoints.")]
    [SerializeField] private int healAmount = 25;

    [Header("Feedback")]
    [Tooltip("Optional styled prefab for the floating-text label. If null (default), a basic " +
             "TextMeshPro label is created at runtime by CrateRewardLabel — feature works either way.")]
    [SerializeField] private GameObject rewardLabelPrefab;
    [Tooltip("If true, unlocking a new projectile type immediately switches the active type to it (player can fire it on the next trigger pull).")]
    [SerializeField] private bool autoSwitchOnUnlock = true;

    // Hex-source colours for the floating label per reward type.
    private static readonly Color BlueColor = new Color(0.247f, 0.714f, 1f);    // #3FB6FF
    private static readonly Color GreenColor = new Color(0.384f, 0.835f, 0.384f); // #62D562
    private static readonly Color RedColor = new Color(1f, 0.314f, 0.314f);     // #FF5050
    private static readonly Color HealthColor = new Color(0.486f, 1f, 0.627f);  // #7CFFA0

    private void OnValidate()
    {
        float sum = pctNothing + pctBlueAmmo + pctGreenAmmo + pctRedAmmo + pctHealth;
        if (Mathf.Abs(sum - 1f) > 0.01f)
            Debug.LogWarning($"[CrateReward] {name} probabilities sum to {sum:0.00} (expected 1.00).", this);
    }

    /// <summary>
    /// Rolls a single reward and applies it to <paramref name="playerTank"/>. Spawns the
    /// floating-text label at <paramref name="hitPoint"/> and fires a haptic when the reward
    /// actually took effect (already-unlocked / already-full-HP rolls suppress UI feedback).
    /// </summary>
    public void RollAndApply(GameObject playerTank, Vector3 hitPoint)
    {
        if (playerTank == null) return;

        CrateRewardType outcome = Roll();
        Apply(outcome, playerTank, hitPoint);
    }

    private CrateRewardType Roll()
    {
        float r = Random.value;
        float c = pctNothing;
        if (r < c) return CrateRewardType.Nothing;
        c += pctBlueAmmo;
        if (r < c) return CrateRewardType.BlueAmmo;
        c += pctGreenAmmo;
        if (r < c) return CrateRewardType.GreenAmmo;
        c += pctRedAmmo;
        if (r < c) return CrateRewardType.RedAmmo;
        return CrateRewardType.Health;
    }

    private void Apply(CrateRewardType outcome, GameObject playerTank, Vector3 hitPoint)
    {
        ShootingControls shooting = playerTank.GetComponent<ShootingControls>();
        Target health = playerTank.GetComponent<Target>();

        switch (outcome)
        {
            case CrateRewardType.BlueAmmo:
                if (shooting != null && shooting.TryUnlock(blueIndex, autoSwitchOnUnlock))
                    ShowFeedback(playerTank, hitPoint, "Blue Ammo", BlueColor, mediumPulse: true);
                break;

            case CrateRewardType.GreenAmmo:
                if (shooting != null && shooting.TryUnlock(greenIndex, autoSwitchOnUnlock))
                    ShowFeedback(playerTank, hitPoint, "Green Ammo", GreenColor, mediumPulse: true);
                break;

            case CrateRewardType.RedAmmo:
                if (shooting != null && shooting.TryUnlock(redIndex, autoSwitchOnUnlock))
                    ShowFeedback(playerTank, hitPoint, "Red Ammo", RedColor, mediumPulse: true);
                break;

            case CrateRewardType.Health:
                if (health != null && health.Heal(healAmount))
                    ShowFeedback(playerTank, hitPoint, $"+{healAmount} HP", HealthColor, mediumPulse: false);
                break;

            case CrateRewardType.Nothing:
            default:
                // No label, no haptic — the existing crate-explosion VFX is sufficient.
                break;
        }
    }

    private void ShowFeedback(GameObject playerTank, Vector3 hitPoint, string text, Color color, bool mediumPulse)
    {
        Vector3 labelPos = hitPoint + Vector3.up * 0.05f;
        GameObject labelObj;
        if (rewardLabelPrefab != null)
        {
            labelObj = Instantiate(rewardLabelPrefab, labelPos, Quaternion.identity);
        }
        else
        {
            // Spawn a self-styled label without requiring a prefab.
            labelObj = new GameObject("CrateRewardLabel");
            labelObj.transform.position = labelPos;
            labelObj.AddComponent<CrateRewardLabel>();
        }

        CrateRewardLabel labelComp = labelObj.GetComponent<CrateRewardLabel>();
        if (labelComp != null)
            labelComp.Show(text, color);

        // Heavier pulse for HP, lighter for ammo.
        ShootingControls shooting = playerTank != null ? playerTank.GetComponent<ShootingControls>() : null;
        if (shooting != null)
        {
            if (mediumPulse)
                Haptics.Pulse(shooting, OVRInput.Controller.RTouch, 0.4f, 0.7f, 0.15f);
            else
                Haptics.Pulse(shooting, OVRInput.Controller.RTouch, 0.6f, 0.9f, 0.25f);
        }
    }
}
