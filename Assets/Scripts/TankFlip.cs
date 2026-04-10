using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class TankFlip : MonoBehaviour
{
    // How long both X and Y must be held to trigger (seconds)
    public float holdDuration = 3f;

    // Upward velocity applied after reset
    public float upVelocity = 2f;

    private InputDevice rightDevice;
    private float holdTimer = 0f;
    private bool triggered = false;
    private Rigidbody rb;

    // Start is called before the first frame update
    void Start()
    {
        rb = GetComponent<Rigidbody>();
        TryInitializeRightDevice();
    }

    void TryInitializeRightDevice()
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller, devices);
        if (devices.Count > 0)
        {
            rightDevice = devices[0];
        }
    }

    // Update is called once per frame
    void Update()
    {
        // ensure we have a valid device reference
        if (!rightDevice.isValid)
        {
            TryInitializeRightDevice();
        }

        if (!rightDevice.isValid)
        {
            // no right controller available this frame
            return;
        }

        bool bPressed = false;

        // B button is typically secondaryButton on the right-hand controller
        rightDevice.TryGetFeatureValue(CommonUsages.secondaryButton, out bPressed);

        if (bPressed)
        {
            holdTimer += Time.deltaTime;
            if (!triggered && holdTimer >= holdDuration)
            {
                triggered = true;
                PerformResetAndBounce();
            }
        }
        else
        {
            holdTimer = 0f;
            triggered = false;
        }
    }

    private void PerformResetAndBounce()
    {
        // Reset rotation to zero
        transform.rotation = Quaternion.identity;
        transform.localRotation = Quaternion.identity;

        // Ensure we have Rigidbody reference
        if (rb == null) rb = GetComponent<Rigidbody>();

        if (rb != null)
        {
            rb.linearVelocity = Vector3.up * upVelocity;
        }
        else
        {
            Debug.LogWarning("TankFlip: Rigidbody not found. Cannot apply velocity. Applying small position bump as fallback.", this);
            transform.position += Vector3.up * 0.1f;
        }
    }
}
