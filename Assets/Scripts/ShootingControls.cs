using System.Collections.Generic;
using Meta.XR.ImmersiveDebugger.UserInterface.Generic;
using UnityEngine;
using UnityEngine.InputSystem;     // For Keyboard.current (New Input System)
using UnityEngine.XR;              // For XRNode
using UnityEngine.UI;              // <-- can be removed if no other UI used

public class ShootingControls : MonoBehaviour
{
    [SerializeField] private GameObject projectilePrefab;
    [SerializeField] private Transform firePoint;
    [SerializeField] private float minProjectileVelocity = 10f;
    [SerializeField] private float maxProjectileVelocity = 40f;
    [SerializeField] private float maxChargeTime = 2f;
    [SerializeField] private PowerBar powerBar; // Optional PowerBar reference

    private float chargeStartTime = 0f;
    private bool isCharging = false;

    private bool lastTriggerPressed = false;

    void Start()
    {
        if (powerBar != null) powerBar.power = 0f;
    }

    void Update()
    {
        // --- Space bar ---
        bool spacePressed = Keyboard.current?.spaceKey.wasPressedThisFrame ?? false;
        bool spaceReleased = Keyboard.current?.spaceKey.wasReleasedThisFrame ?? false;

        // --- Oculus right trigger (XR) ---
        bool triggerPressed = false;
        bool triggerReleased = false;

        List<UnityEngine.XR.InputDevice> devices = new List<UnityEngine.XR.InputDevice>();
        UnityEngine.XR.InputDevices.GetDevicesAtXRNode(XRNode.RightHand, devices);

        if (devices.Count > 0)
        {
            var rightHand = devices[0];

            if (rightHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out float triggerValue))
            {
                bool isPressedNow = triggerValue > 0.1f;

                triggerPressed = isPressedNow && !lastTriggerPressed;
                triggerReleased = !isPressedNow && lastTriggerPressed;

                if (triggerPressed)
                    Debug.Log($"Oculus right trigger pressed: {triggerValue}");

                if (triggerReleased)
                    Debug.Log($"Oculus right trigger released: {triggerValue}");

                lastTriggerPressed = isPressedNow;
            }
        }

        // Start charging
        if (spacePressed || triggerPressed)
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
        if ((spaceReleased || triggerReleased) && isCharging)
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
