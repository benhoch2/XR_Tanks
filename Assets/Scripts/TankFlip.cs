using UnityEngine;
using UnityEngine.InputSystem;

public class TankFlip : MonoBehaviour
{
    // How long B must be held to trigger (seconds)
    public float holdDuration = 3f;

    // Upward velocity applied after reset
    public float upVelocity = 2f;

    private InputAction flipAction;

    private float holdTimer = 0f;
    private bool triggered = false;
    private Rigidbody rb;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
    }

    void OnEnable()
    {
        flipAction = new InputAction("FlipTank", InputActionType.Button, "<XRController>{RightHand}/secondaryButton");
        flipAction.Enable();
    }

    void OnDisable()
    {
        flipAction?.Disable();
        flipAction?.Dispose();
        flipAction = null;
    }

    void Update()
    {
        bool bPressed = flipAction?.IsPressed() ?? false;

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
        // Reset rotation to zero (relative to parent if any).
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
