using UnityEngine;

public class Projectile : MonoBehaviour
{
    [Tooltip("Damage dealt on hit. Set to 0 or less for instant kill.")]
    public int damage = 25;
}
