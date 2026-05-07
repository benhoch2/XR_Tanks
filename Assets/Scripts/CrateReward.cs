using UnityEngine;

/// <summary>
/// Attached to crate prefabs (e.g. TargetBox.prefab) AND to enemy tank prefabs that drop loot
/// on death. When the crate is destroyed by a player shot, <see cref="Target.HandleHit"/>
/// calls <see cref="RollAndApply"/> on the insta-kill path. When an enemy tank dies,
/// <see cref="Target.HandleLethalHit"/> does the same. Rolls a weighted outcome via the
/// shared <see cref="RewardTableSO"/> and either grants ammo on the player's
/// <see cref="ShootingControls"/> or heals via <see cref="Target.Heal"/>.
/// </summary>
public class CrateReward : MonoBehaviour
{
    public enum CrateRewardType
    {
        Nothing,
        BlueAmmo,
        GreenAmmo,
        RedAmmo,
        Health,
        DroneAmmo,
        PlaneDroneAmmo,
        LaserAmmo
    }

    [Tooltip("Shared reward configuration. Probabilities, slot indices, and heal amount all live on this asset so crates and enemy tanks can be rebalanced from one place.")]
    [SerializeField] private RewardTableSO table;

    [Header("Feedback")]
    [Tooltip("Optional styled prefab for the floating-text label. If null (default), a basic " +
             "TextMeshPro label is created at runtime by CrateRewardLabel — feature works either way.")]
    [SerializeField] private GameObject rewardLabelPrefab;
    [Tooltip("If true, granting a slot's first ammo immediately switches the active type to it (player can fire it on the next trigger pull).")]
    [SerializeField] private bool autoSwitchOnUnlock = true;

    // Hex-source colours for the floating label per reward type.
    private static readonly Color BlueColor = new Color(0.247f, 0.714f, 1f);     // #3FB6FF
    private static readonly Color GreenColor = new Color(0.384f, 0.835f, 0.384f); // #62D562
    private static readonly Color RedColor = new Color(1f, 0.314f, 0.314f);      // #FF5050
    private static readonly Color HealthColor = new Color(0.486f, 1f, 0.627f);   // #7CFFA0
    private static readonly Color DroneColor = new Color(1f, 0.62f, 0.18f);      // #FF9E2E orange
    private static readonly Color PlaneDroneColor = new Color(0.78f, 0.45f, 1f); // #C672FF purple
    private static readonly Color LaserColor = new Color(1f, 0.95f, 0.4f);       // #FFF266 hot yellow

    /// <summary>
    /// Rolls a single reward and applies it to <paramref name="playerTank"/>. Spawns the
    /// floating-text label at <paramref name="hitPoint"/> and fires a haptic when the reward
    /// actually took effect (already-full-HP rolls suppress UI feedback).
    /// </summary>
    public void RollAndApply(GameObject playerTank, Vector3 hitPoint)
    {
        if (playerTank == null || table == null) return;

        CrateRewardType outcome = table.Roll();
        Apply(outcome, playerTank, hitPoint);
    }

    private void Apply(CrateRewardType outcome, GameObject playerTank, Vector3 hitPoint)
    {
        ShootingControls shooting = playerTank.GetComponent<ShootingControls>();
        Target health = playerTank.GetComponent<Target>();

        switch (outcome)
        {
            case CrateRewardType.BlueAmmo:
                if (shooting != null && shooting.TryUnlock(table.blueIndex, autoSwitchOnUnlock))
                    ShowFeedback(playerTank, hitPoint, "Blue Ammo", BlueColor, mediumPulse: true);
                break;

            case CrateRewardType.GreenAmmo:
                if (shooting != null && shooting.TryUnlock(table.greenIndex, autoSwitchOnUnlock))
                    ShowFeedback(playerTank, hitPoint, "Green Ammo", GreenColor, mediumPulse: true);
                break;

            case CrateRewardType.RedAmmo:
                if (shooting != null && shooting.TryUnlock(table.redIndex, autoSwitchOnUnlock))
                    ShowFeedback(playerTank, hitPoint, "Red Ammo", RedColor, mediumPulse: true);
                break;

            case CrateRewardType.DroneAmmo:
                if (shooting != null && shooting.TryUnlock(table.droneIndex, autoSwitchOnUnlock))
                    ShowFeedback(playerTank, hitPoint, "Drone", DroneColor, mediumPulse: true);
                break;

            case CrateRewardType.PlaneDroneAmmo:
                if (shooting != null && shooting.TryUnlock(table.planeDroneIndex, autoSwitchOnUnlock))
                    ShowFeedback(playerTank, hitPoint, "Plane Drone", PlaneDroneColor, mediumPulse: true);
                break;

            case CrateRewardType.LaserAmmo:
                if (shooting != null && shooting.TryUnlock(table.laserIndex, autoSwitchOnUnlock))
                    ShowFeedback(playerTank, hitPoint, "Laser", LaserColor, mediumPulse: true);
                break;

            case CrateRewardType.Health:
                if (health != null && health.Heal(table.healAmount))
                    ShowFeedback(playerTank, hitPoint, $"+{table.healAmount} HP", HealthColor, mediumPulse: false);
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
