using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TowerController : MonoBehaviour
{
    [Header("Tower Rotation Settings")]
    [SerializeField] GameObject towerBase; // Reference to the tower base GameObject
    public float rotationSpeed = 100f;
    public float tiltSpeed = 50f;
    public float minTilt = -10f;
    public float maxTilt = 30f;

    [Header("Tank Movement Settings")]
    public float moveSpeed = 5f;
    public float strafeSpeed = 4f;

    private float currentTilt = 0f;

    // Start is called before the first frame update
    void Start()
    {
        // Initialization if needed
    }

    // Update is called once per frame
    void Update()
    {
        HandleInput();
    }

    // Handles input for rotation and tilt
    private void HandleInput()
    {
        float rotationInput = 0f;
        float tiltInput = 0f;
        float moveInput = 0f;
        float turnInput = 0f;

        // Keyboard input for tower
        if (Input.GetKey(KeyCode.LeftArrow))
            rotationInput = -1f;
        else if (Input.GetKey(KeyCode.RightArrow))
            rotationInput = 1f;

        if (Input.GetKey(KeyCode.UpArrow))
            tiltInput = 1f;
        else if (Input.GetKey(KeyCode.DownArrow))
            tiltInput = -1f;

        // Keyboard input for tank movement
        if (Input.GetKey(KeyCode.W))
            moveInput = 1f;
        else if (Input.GetKey(KeyCode.S))
            moveInput = -1f;

        // Tank turning (A/D)
        if (Input.GetKey(KeyCode.D))
            turnInput = 1f;
        else if (Input.GetKey(KeyCode.A))
            turnInput = -1f;

        // Future: Add Oculus controller input here
        // Example: rotationInput += OculusInput.GetTowerRotation();
        // Example: tiltInput += OculusInput.GetTowerTilt();
        // Example: moveInput += OculusInput.GetTankMove();
        // Example: turnInput += OculusInput.GetTankTurn();

        MoveTank(moveInput, turnInput);
        RotateTower(rotationInput);
        TiltTower(tiltInput);
    }

    private void RotateTower(float input)
    {
        if (input != 0f)
        {
            towerBase.transform.Rotate(Vector3.up, input * rotationSpeed * Time.deltaTime, Space.World);
        }
    }

    private void TiltTower(float input)
    {
        if (input != 0f)
        {
            currentTilt = Mathf.Clamp(currentTilt + input * tiltSpeed * Time.deltaTime, minTilt, maxTilt);
            Vector3 localEuler = towerBase.transform.localEulerAngles;
            localEuler.x = currentTilt;
            towerBase.transform.localEulerAngles = localEuler;
        }

    }

    // Moves the tank using WASD input
    private void MoveTank(float moveInput, float turnInput)
    {
        // Turn tank left/right
        if (turnInput != 0f)
        {
            transform.Rotate(Vector3.up, turnInput * rotationSpeed * Time.deltaTime, Space.World);
        }
        // Move tank forward/backward
        Vector3 move = transform.forward * moveInput * moveSpeed * Time.deltaTime;
        transform.position += move;
    }
}
