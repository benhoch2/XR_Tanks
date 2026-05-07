using UnityEngine;

/// <summary>
/// Shared probability/index config for crate and enemy-kill drops. One asset per gameplay
/// configuration so we have a single source of truth for tuning — no inspector drift across
/// crate variants and enemy tank variants. <see cref="CrateReward"/> reads this and dispatches
/// the rolled outcome via its existing player-grant pipeline.
/// </summary>
[CreateAssetMenu(menuName = "XR Tanks/Reward Table", fileName = "RewardTable")]
public class RewardTableSO : ScriptableObject
{
    [Header("Probabilities (must sum to ~1)")]
    [Range(0f, 1f)] public float pctNothing = 0.30f;
    [Range(0f, 1f)] public float pctBlueAmmo = 0.17f;
    [Range(0f, 1f)] public float pctGreenAmmo = 0.17f;
    [Range(0f, 1f)] public float pctRedAmmo = 0.17f;
    [Range(0f, 1f)] public float pctHealth = 0.06f;
    [Range(0f, 1f)] public float pctDrone = 0.04f;
    [Range(0f, 1f)] public float pctPlaneDrone = 0.04f;
    [Range(0f, 1f)] public float pctLaser = 0.05f;

    [Header("Player ShootingControls slot indices")]
    [Tooltip("Slot index in ShootingControls.projectilePrefabs for each ammo type.")]
    public int blueIndex = 1;
    public int greenIndex = 2;
    public int redIndex = 3;
    public int droneIndex = 4;
    public int planeDroneIndex = 5;
    public int laserIndex = 6;

    [Header("Health roll")]
    [Tooltip("Hit points restored on a Health roll. Capped at the player Target's maxHitPoints.")]
    public int healAmount = 25;

    /// <summary>
    /// Single weighted roll. Probabilities are walked as a cumulative ladder; if the float
    /// total is short of 1.0 the remainder is treated as the final outcome (Laser). The
    /// OnValidate warning catches authoring mistakes; runtime is forgiving.
    /// </summary>
    public CrateReward.CrateRewardType Roll()
    {
        float r = Random.value;
        float c = pctNothing;
        if (r < c) return CrateReward.CrateRewardType.Nothing;
        c += pctBlueAmmo;
        if (r < c) return CrateReward.CrateRewardType.BlueAmmo;
        c += pctGreenAmmo;
        if (r < c) return CrateReward.CrateRewardType.GreenAmmo;
        c += pctRedAmmo;
        if (r < c) return CrateReward.CrateRewardType.RedAmmo;
        c += pctHealth;
        if (r < c) return CrateReward.CrateRewardType.Health;
        c += pctDrone;
        if (r < c) return CrateReward.CrateRewardType.DroneAmmo;
        c += pctPlaneDrone;
        if (r < c) return CrateReward.CrateRewardType.PlaneDroneAmmo;
        return CrateReward.CrateRewardType.LaserAmmo;
    }

    private void OnValidate()
    {
        float sum = pctNothing + pctBlueAmmo + pctGreenAmmo + pctRedAmmo
                  + pctHealth + pctDrone + pctPlaneDrone + pctLaser;
        if (Mathf.Abs(sum - 1f) > 0.01f)
            Debug.LogWarning($"[RewardTableSO] {name} probabilities sum to {sum:0.00} (expected 1.00).", this);
    }
}
