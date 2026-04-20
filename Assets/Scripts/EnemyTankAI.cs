using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class EnemyTankAI : MonoBehaviour
{
    [HideInInspector] public float moveSpeed = 0.5f;
    [HideInInspector] public float chaseRange = 4f;
    [HideInInspector] public float stopRange = 1.25f;

    [SerializeField] private float rotationSpeed = 100f;
    [SerializeField] private float reverseSpeedFactor = 0.6f;
    [SerializeField] private float turnMoveReduction = 0.5f;
    [SerializeField] private float wanderRadius = 1.5f;
    [SerializeField] private float repathInterval = 1.5f;

    private NavMeshAgent _agent;
    private Transform _player;
    private Vector3 _homePosition;
    private float _nextRepathTime;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        _homePosition = transform.position;

        if (_agent != null)
        {
            _agent.updatePosition = true;
            _agent.updateRotation = false;
        }
    }

    private void Update()
    {
        if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
            return;

        _agent.speed = moveSpeed;
        _agent.stoppingDistance = stopRange;

        if (_player == null)
        {
            ShootingControls playerControls = FindAnyObjectByType<ShootingControls>();
            if (playerControls != null)
                _player = playerControls.transform;
        }

        bool hasTarget = false;
        if (_player != null)
        {
            Vector3 toPlayer = _player.position - transform.position;
            toPlayer.y = 0f;
            float distanceToPlayer = toPlayer.magnitude;

            if (distanceToPlayer <= chaseRange)
            {
                hasTarget = true;
                if (distanceToPlayer > stopRange)
                {
                    _agent.SetDestination(_player.position);
                }
                else
                {
                    _agent.ResetPath();
                    RotateToward(toPlayer.normalized);
                }
            }
        }

        if (!hasTarget && Time.time >= _nextRepathTime && (!_agent.hasPath || _agent.remainingDistance <= _agent.stoppingDistance + 0.1f))
        {
            SetRandomDestination();
            _nextRepathTime = Time.time + repathInterval;
        }

        DriveLikeTank();
    }

    private void DriveLikeTank()
    {
        if (!_agent.hasPath)
            return;

        Vector3 desiredDirection = _agent.steeringTarget - transform.position;
        desiredDirection.y = 0f;

        if (desiredDirection.sqrMagnitude < 0.001f)
            return;

        Vector3 localDirection = transform.InverseTransformDirection(desiredDirection.normalized);
        float turnInput = Mathf.Clamp(localDirection.x, -1f, 1f);
        float moveInput = Mathf.Clamp(localDirection.z, -1f, 1f);

        if (Mathf.Abs(turnInput) > 0.01f)
            transform.Rotate(Vector3.up, turnInput * rotationSpeed * Time.deltaTime);

        if (Mathf.Abs(moveInput) > 0.01f)
        {
            float speedFactor = moveInput < 0f ? reverseSpeedFactor : 1f;
            speedFactor *= Mathf.Lerp(1f, turnMoveReduction, Mathf.Abs(turnInput));

            Vector3 step = transform.forward * moveInput * moveSpeed * speedFactor * Time.deltaTime;
            _agent.Move(step);
        }
    }

    private void RotateToward(Vector3 worldDirection)
    {
        if (worldDirection.sqrMagnitude < 0.001f)
            return;

        Vector3 localDirection = transform.InverseTransformDirection(worldDirection.normalized);
        float turnInput = Mathf.Clamp(localDirection.x, -1f, 1f);
        if (Mathf.Abs(turnInput) > 0.01f)
            transform.Rotate(Vector3.up, turnInput * rotationSpeed * Time.deltaTime);
    }

    private void SetRandomDestination()
    {
        for (int i = 0; i < 10; i++)
        {
            Vector2 random2D = Random.insideUnitCircle * wanderRadius;
            Vector3 candidate = _homePosition + new Vector3(random2D.x, 0f, random2D.y);

            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 1.5f, NavMesh.AllAreas))
            {
                _agent.SetDestination(hit.position);
                return;
            }
        }
    }
}
