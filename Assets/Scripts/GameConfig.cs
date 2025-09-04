using UnityEditor.PackageManager;

[System.Serializable]
public class GameConfig
{
    public static int numberOfEnemies = 10;
    public static int numberOfCrates = 2;

    public static int projectileMinSpeed = 1;
    public static int projectileMaxSpeed = 20;
    public static float powerUpDuration = 2f;
}
