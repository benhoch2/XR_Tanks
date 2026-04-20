using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class EnemyTankAI : MonoBehaviour
{
    public enum BehaviorMode
    {
        ChaseWander = 0,
        PatrolScan = 1
    }

    private enum PatrolScanState
    {
        PickDirection,
        Driving,
        Scanning
    }

    [HideInInspector] public float moveSpeed = 0.5f;
    [HideInInspector] public float chaseRange = 4f;
    [HideInInspector] public float stopRange = 1.25f;
    [HideInInspector] public BehaviorMode behaviorMode = BehaviorMode.ChaseWander;
    [HideInInspector] public float obstacleCheckDistance = 0.25f;
    [HideInInspector] public float turretScanAngle = 45f;
    [HideInInspector] public float turretScanSpeed = 25f;
    [HideInInspector] public int turretScanRepeats = 2;

    [SerializeField] private float rotationSpeed = 100f;
    [SerializeField] private float reverseSpeedFactor = 0.6f;
    [SerializeField] private float turnMoveReduction = 0.5f;
    [SerializeField] private float wanderRadius = 1.5f;
    [SerializeField] private float repathInterval = 1.5f;

    private NavMeshAgent _agent;
    private Transform _player;
    private Vector3 _homePosition;
    private float _nextRepathTime;
    private PatrolScanState _patrolScanState = PatrolScanState.PickDirection;
    private Vector3 _patrolDirection = Vector3.forward;
    private Transform _turretTransform;
    private float _scanCenterYaw;
    private float _scanTargetYaw;
    private float _scanDirection = 1f;
    private int _remainingHalfSweeps;
    private bool _isRecentering;
    private float _driveStateStartTime;
    private BehaviorMode _lastBehaviorMode;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        _homePosition = transform.position;
        _turretTransform = FindTurretTransform();

        if (_agent != null)
        {
            _agent.updatePosition = true;
            _agent.updateRotation = false;
        }

        _lastBehaviorMode = behaviorMode;
        ResetBehaviorState();
    }

    public void ResetBehaviorState()
    {
        if (_agent != null)
            _agent.ResetPath();

        _nextRepathTime = 0f;
        _patrolScanState = PatrolScanState.PickDirection;
        _patrolDirection = transform.forward;
        _driveStateStartTime = Time.time;
        ResetTurretToForward();
    }

    private void Update()
    {
        if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
            return;

        SyncConfigFromManager();

        _agent.speed = moveSpeed;
        _agent.stoppingDistance = stopRange;

        if (_lastBehaviorMode != behaviorMode)
        {
            _lastBehaviorMode = behaviorMode;
            ResetBehaviorState();
        }

        switch (behaviorMode)
        {
            case BehaviorMode.PatrolScan:
                UpdatePatrolScanMode();
                break;

            default:
                UpdateChaseWanderMode();
                break;
        }
    }

    private void SyncConfigFromManager()
    {
        if (GameConfigManager.Instance == null)
            return;

        moveSpeed = GameConfigManager.Instance.enemyMoveSpeed;
        chaseRange = GameConfigManager.Instance.enemyChaseRange;
        stopRange = GameConfigManager.Instance.enemyStopRange;
        obstacleCheckDistance = GameConfigManager.Instance.enemyObstacleCheckDistance;
        turretScanAngle = GameConfigManager.Instance.enemyScanAngle;
        turretScanSpeed = GameConfigManager.Instance.enemyScanSpeed;
        turretScanRepeats = GameConfigManager.Instance.enemyScanRepetitions;
        behaviorMode = GameConfigManager.Instance.enemyAIMode == 1 ? BehaviorMode.PatrolScan : BehaviorMode.ChaseWander;
    }

    private void UpdateChaseWanderMode()
    {
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
                    DriveLikeTank(_agent.steeringTarget - transform.position);
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

        if (!hasTarget && _agent.hasPath)
            DriveLikeTank(_agent.steeringTarget - transform.position);

        ResetTurretToForward();
    }

    private void UpdatePatrolScanMode()
    {
        switch (_patrolScanState)
        {
            case PatrolScanState.PickDirection:
                PickRandomPatrolDirection();
                break;

            case PatrolScanState.Driving:
                DriveLikeTank(_patrolDirection);

                if (Time.time < _driveStateStartTime + 0.75f)
                    return;

                if (Vector3.Dot(transform.forward, _patrolDirection) < 0.6f)
                    return;

                if (IsBlockedAhead(_patrolDirection) || IsAtNavMeshEdge(_patrolDirection))
                {
                    _patrolScanState = PatrolScanState.Scanning;
                    BeginTurretScan();
                    return;
                }
                break;

            case PatrolScanState.Scanning:
                if (UpdateTurretScan())
                    _patrolScanState = PatrolScanState.PickDirection;
                break;
        }
    }

    private void DriveLikeTank(Vector3 desiredDirection)
    {
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

    private void PickRandomPatrolDirection()
    {
        _agent.ResetPath();

        for (int i = 0; i < 12; i++)
        {
            float randomYaw = Random.Range(0f, 360f);
            Vector3 direction = Quaternion.Euler(0f, randomYaw, 0f) * Vector3.forward;

            if (CanDriveInDirection(direction))
            {
                _patrolDirection = direction.normalized;
                _driveStateStartTime = Time.time;
                _patrolScanState = PatrolScanState.Driving;
                return;
            }
        }

        _patrolDirection = transform.forward;
        _patrolScanState = PatrolScanState.Scanning;
        BeginTurretScan();
    }

    private bool CanDriveInDirection(Vector3 direction)
    {
        Vector3 candidate = transform.position + direction.normalized * Mathf.Max(0.2f, obstacleCheckDistance);
        if (NavMesh.Raycast(transform.position, candidate, out _, NavMesh.AllAreas))
            return false;

        return NavMesh.SamplePosition(candidate, out _, 0.3f, NavMesh.AllAreas);
    }

    private bool IsBlockedAhead(Vector3 direction)
    {
        Vector3 forward = direction.sqrMagnitude > 0.001f ? direction.normalized : transform.forward;
        Vector3 origin = transform.position + forward * 0.12f + Vector3.up * 0.08f;
        if (Physics.SphereCast(origin, 0.04f, forward, out RaycastHit hit, Mathf.Max(0.25f, obstacleCheckDistance), Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            if (hit.transform == null)
                return false;

            if (hit.transform == transform || hit.transform.IsChildOf(transform))
                return false;

            return true;
        }

        return false;
    }

    private bool IsAtNavMeshEdge(Vector3 direction)
    {
        Vector3 forward = direction.sqrMagnitude > 0.001f ? direction.normalized : transform.forward;
        Vector3 start = transform.position + Vector3.up * 0.02f;
        Vector3 nextPoint = start + forward * Mathf.Max(0.45f, obstacleCheckDistance * 1.5f);
        return NavMesh.Raycast(start, nextPoint, out _, NavMesh.AllAreas);
    }

    private void BeginTurretScan()
    {
        if (_turretTransform == null)
            return;

        _scanCenterYaw = _turretTransform.localEulerAngles.y;
        _scanDirection = 1f;
        _scanTargetYaw = _scanCenterYaw + turretScanAngle;
        _remainingHalfSweeps = Mathf.Max(1, turretScanRepeats * 2);
        _isRecentering = false;
    }

    private bool UpdateTurretScan()
    {
        if (_turretTransform == null)
            return true;

        Vector3 localEuler = _turretTransform.localEulerAngles;
        float nextYaw = Mathf.MoveTowardsAngle(localEuler.y, _scanTargetYaw, turretScanSpeed * Time.deltaTime);
        localEuler.y = nextYaw;
        _turretTransform.localEulerAngles = localEuler;

        if (Mathf.Abs(Mathf.DeltaAngle(nextYaw, _scanTargetYaw)) > 0.5f)
            return false;

        if (_isRecentering)
            return true;

        _remainingHalfSweeps--;
        if (_remainingHalfSweeps <= 0)
        {
            _isRecentering = true;
            _scanTargetYaw = _scanCenterYaw;
            return false;
        }

        _scanDirection *= -1f;
        _scanTargetYaw = _scanCenterYaw + (_scanDirection * turretScanAngle);
        return false;
    }

    private void ResetTurretToForward()
    {
        if (_turretTransform == null)
            return;

        Vector3 localEuler = _turretTransform.localEulerAngles;
        localEuler.y = Mathf.MoveTowardsAngle(localEuler.y, 0f, turretScanSpeed * 2f * Time.deltaTime);
        _turretTransform.localEulerAngles = localEuler;
    }

    private Transform FindTurretTransform()
    {
        foreach (Transform child in GetComponentsInChildren<Transform>(true))
        {
            string lowerName = child.name.ToLowerInvariant();
            if (lowerName.Contains("tower") || lowerName.Contains("turret"))
                return child;
        }

        return null;
    }
}
