using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Rotator : MonoBehaviour
{
    [Tooltip("Degrees per second around the Y axis.")]
    public float rotationSpeed = 45f;

    // random initial phase (degrees)
    private float initialPhase = 0f;

    // Start is called before the first frame update
    void Start()
    {
        // apply a random initial yaw so each instance has a different phase
        initialPhase = Random.Range(0f, 360f);
        transform.Rotate(0f, initialPhase, 0f, Space.Self);
    }

    // Update is called once per frame
    void Update()
    {
        // Rotate around local Y axis at configured speed
        transform.Rotate(0f, rotationSpeed * Time.deltaTime, 0f, Space.Self);
    }
}
