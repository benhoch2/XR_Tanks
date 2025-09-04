using UnityEngine;

[CreateAssetMenu(fileName = "GameConfigAsset", menuName = "Config/Game Config")]
public class GameConfigAsset : ScriptableObject
{
    public static int numberOfEnemies = 10;
    public static int numberOfCrates = 2;

    public static int projectileMinSpeed = 1;
    public static int projectileMaxSpeed = 20;
    public static float powerUpDuration = 2f;
}