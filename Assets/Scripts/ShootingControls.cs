using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ShootingControls : MonoBehaviour
{
    [SerializeField] private GameObject projectilePrefab; // Prefab for the projectile
    [SerializeField] private Transform firePoint; // Point from where the projectile will be fired
    [SerializeField] private float minProjectileVelocity = 10f; // Minimum speed
    [SerializeField] private float maxProjectileVelocity = 40f; // Maximum speed
    [SerializeField] private float maxChargeTime = 2f; // Time to reach max velocity

    private float chargeStartTime = 0f;
    private bool isCharging = false;
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        // Start charging when space is pressed
        if (Input.GetKeyDown(KeyCode.Space))
        {
            chargeStartTime = Time.time;
            isCharging = true;
        }
        // Shoot when space is released
        if (Input.GetKeyUp(KeyCode.Space) && isCharging)
        {
            float chargeDuration = Mathf.Clamp(Time.time - chargeStartTime, 0f, maxChargeTime);
            float velocity = Mathf.Lerp(minProjectileVelocity, maxProjectileVelocity, chargeDuration / maxChargeTime);
            Shoot(velocity);
            isCharging = false;
        }
    }

    private void Shoot(float velocity)
    {
        if (projectilePrefab != null && firePoint != null)
        {
            GameObject projectile = Instantiate(projectilePrefab, firePoint.position, firePoint.rotation);
            Rigidbody rb = projectile.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = firePoint.forward * velocity;
            }
        }
    }
}
