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
        Scanning,
        Shooting
    }

    private enum ShootingPhase
    {
        AimAtPlayer,
        Fire,
        WaitForImpact,
        BetweenShots,
        Done
    }

    [HideInInspector] public float moveSpeed = 0.5f;
    [HideInInspector] public float chaseRange = 4f;
    [HideInInspector] public float stopRange = 1.25f;
    [HideInInspector] public BehaviorMode behaviorMode = BehaviorMode.ChaseWander;
    [HideInInspector] public float obstacleCheckDistance = 0.25f;
    [HideInInspector] public float turretScanAngle = 45f;
    [HideInInspector] public float turretScanSpeed = 25f;

    [Header("Scan")]
    [Tooltip("Inclusive minimum number of half-sweeps per scan. Randomized per scan so enemies desync.")]
    [SerializeField] private int minScanSwipes = 1;
    [Tooltip("Inclusive maximum number of half-sweeps per scan.")]
    [SerializeField] private int maxScanSwipes = 4;

    [SerializeField] private float rotationSpeed = 100f;
    [SerializeField] private float reverseSpeedFactor = 0.6f;
    [SerializeField] private float turnMoveReduction = 0.5f;
    [SerializeField] private float wanderRadius = 1.5f;
    [SerializeField] private float repathInterval = 1.5f;

    [Header("Shooting")]
    [SerializeField] private GameObject enemyProjectilePrefab;
    [Tooltip("Optional muzzle transform. If null, falls back to turret forward + small offset.")]
    [SerializeField] private Transform firePoint;
    [SerializeField] private int maxShotsPerVolley = 3;
    [SerializeField] private int enemyProjectileDamage = 15;
    [SerializeField] private float minShotSpeed = 3f;
    [SerializeField] private float maxShotSpeed = 8f;
    [SerializeField] private float aimYawSpeedDegPerSec = 90f;
    [SerializeField] private float aimSettleThresholdDeg = 5f;
    [SerializeField] private float aimMaxDuration = 1.5f;
    [SerializeField] private float betweenShotsDelay = 0.75f;
    [SerializeField] private float shotImpactTimeout = 4f;
    [SerializeField] private float losMaxRange = 8f;
    [Tooltip("Max random error as a fraction of distance to target (e.g. 0.1 = up to 10%). Applied per shot on top of any learned correction.")]
    [SerializeField] private float aimErrorMaxFactor = 0.1f;
    [Tooltip("Minimum elevation (degrees above horizontal) for a shot — stops enemies from firing flat.")]
    [SerializeField] private float minLaunchElevationDeg = 15f;

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

    private ShootingPhase _shootingPhase;
    private int _shotsFiredThisVolley;
    private Vector3 _currentAimTarget;
    private Vector3 _lastAimTarget;
    private Vector3 _lastImpactPosition;
    private bool _hasLastImpact;
    private float _phaseStartTime;
    private bool _awaitingImpact;

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
        _awaitingImpact = false;
        _hasLastImpact = false;
        _shotsFiredThisVolley = 0;
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
                {
                    if (TryBeginShootingVolley())
                        _patrolScanState = PatrolScanState.Shooting;
                    else
                        _patrolScanState = PatrolScanState.PickDirection;
                }
                break;

            case PatrolScanState.Shooting:
                UpdateShootingVolley();
                break;
        }
    }

    private bool TryBeginShootingVolley()
    {
        if (enemyProjectilePrefab == null)
            return false;

        if (!HasLineOfSightToPlayer(out Vector3 playerPos))
            return false;

        _shotsFiredThisVolley = 0;
        _hasLastImpact = false;
        _currentAimTarget = playerPos;
        _shootingPhase = ShootingPhase.AimAtPlayer;
        _phaseStartTime = Time.time;
        return true;
    }

    private void UpdateShootingVolley()
    {
        // Refresh aim target to player's current position each frame (so they're tracked if moving).
        if (_player != null)
            _currentAimTarget = _player.position;

        switch (_shootingPhase)
        {
            case ShootingPhase.AimAtPlayer:
            {
                bool settled = AimTurretYawAtWorldPosition(_currentAimTarget);
                bool timedOut = Time.time - _phaseStartTime > aimMaxDuration;
                if (settled || timedOut)
                    TransitionShootingPhase(ShootingPhase.Fire);
                break;
            }

            case ShootingPhase.Fire:
                FireShot();
                TransitionShootingPhase(ShootingPhase.WaitForImpact);
                break;

            case ShootingPhase.WaitForImpact:
                // Keep yaw-tracking while waiting — feels alive.
                AimTurretYawAtWorldPosition(_currentAimTarget);
                if (!_awaitingImpact || Time.time - _phaseStartTime > shotImpactTimeout)
                {
                    // Timeout counts as a miss with no correction feedback.
                    _awaitingImpact = false;
                    AdvanceAfterShot();
                }
                break;

            case ShootingPhase.BetweenShots:
                AimTurretYawAtWorldPosition(_currentAimTarget);
                if (Time.time - _phaseStartTime >= betweenShotsDelay)
                    TransitionShootingPhase(ShootingPhase.Fire);
                break;

            case ShootingPhase.Done:
                _patrolScanState = PatrolScanState.PickDirection;
                break;
        }
    }

    private void TransitionShootingPhase(ShootingPhase next)
    {
        _shootingPhase = next;
        _phaseStartTime = Time.time;
    }

    private void AdvanceAfterShot()
    {
        _shotsFiredThisVolley++;
        if (_shotsFiredThisVolley >= Mathf.Max(1, maxShotsPerVolley))
            TransitionShootingPhase(ShootingPhase.Done);
        else
            TransitionShootingPhase(ShootingPhase.BetweenShots);
    }

    private void FireShot()
    {
        if (enemyProjectilePrefab == null)
        {
            AdvanceAfterShot();
            return;
        }

        Vector3 firePos = GetFirePosition();

        // Correct the aim target by the previous shot's error so that both angle and power
        // re-estimate when the solver is re-run against the corrected virtual target.
        Vector3 virtualTarget = _currentAimTarget;
        if (_hasLastImpact)
        {
            Vector3 error = _lastImpactPosition - _lastAimTarget;
            virtualTarget = _currentAimTarget - error;
        }

        float horizontalDistance = new Vector2(virtualTarget.x - firePos.x, virtualTarget.z - firePos.z).magnitude;

        // Per-shot random estimation error, scaled by range. Horizontal only so the miss looks like
        // aim imprecision rather than ranging error in Y.
        if (aimErrorMaxFactor > 0f && horizontalDistance > 0.01f)
        {
            float errorMag = horizontalDistance * Random.Range(0f, aimErrorMaxFactor);
            float errorAngle = Random.Range(0f, Mathf.PI * 2f);
            virtualTarget += new Vector3(Mathf.Cos(errorAngle) * errorMag, 0f, Mathf.Sin(errorAngle) * errorMag);
            horizontalDistance = new Vector2(virtualTarget.x - firePos.x, virtualTarget.z - firePos.z).magnitude;
        }

        float launchSpeed = ComputeLaunchSpeed(horizontalDistance);
        SolveBallistic(firePos, virtualTarget, launchSpeed, out Vector3 launchVelocity);
        launchVelocity = EnforceMinElevation(launchVelocity);

        GameObject projObj = Instantiate(
            enemyProjectilePrefab,
            firePos,
            Quaternion.LookRotation(launchVelocity.sqrMagnitude > 0.0001f ? launchVelocity.normalized : transform.forward, Vector3.up));

        Projectile proj = projObj.GetComponent<Projectile>();
        if (proj == null)
            proj = projObj.AddComponent<Projectile>();
        proj.damage = enemyProjectileDamage;
        proj.shooter = transform;
        proj.onImpact = OnProjectileImpact;

        Collider projCol = projObj.GetComponent<Collider>();
        if (projCol != null)
        {
            foreach (var selfCol in GetComponentsInChildren<Collider>())
                Physics.IgnoreCollision(projCol, selfCol);
        }

        Rigidbody rb = projObj.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.linearVelocity = launchVelocity;
        }

        _lastAimTarget = virtualTarget;
        _awaitingImpact = true;
    }

    private void OnProjectileImpact(Vector3 impactPos)
    {
        if (!_awaitingImpact)
            return;

        _lastImpactPosition = impactPos;
        _hasLastImpact = true;
        _awaitingImpact = false;
        AdvanceAfterShot();
    }

    private Vector3 GetFirePosition()
    {
        if (firePoint != null)
            return firePoint.position;

        Transform source = _turretTransform != null ? _turretTransform : transform;
        return source.position + source.forward * 0.15f + Vector3.up * 0.08f;
    }

    private float ComputeLaunchSpeed(float horizontalDistance)
    {
        // For zero height difference, v² = g·d gives a 45° low-arc. Scale up slightly for a flatter,
        // snappier arc, and clamp to the configured range so the cannon feels consistent.
        float g = Mathf.Max(0.01f, -Physics.gravity.y);
        float ideal = Mathf.Sqrt(g * Mathf.Max(0.1f, horizontalDistance)) * 1.25f;
        return Mathf.Clamp(ideal, minShotSpeed, maxShotSpeed);
    }

    private Vector3 EnforceMinElevation(Vector3 launchVelocity)
    {
        if (minLaunchElevationDeg <= 0f)
            return launchVelocity;

        float speed = launchVelocity.magnitude;
        if (speed < 0.001f)
            return launchVelocity;

        Vector3 horiz = new Vector3(launchVelocity.x, 0f, launchVelocity.z);
        float horizMag = horiz.magnitude;
        float currentElevDeg = Mathf.Atan2(launchVelocity.y, horizMag) * Mathf.Rad2Deg;
        if (currentElevDeg >= minLaunchElevationDeg)
            return launchVelocity;

        Vector3 horizDir = horizMag > 0.0001f ? horiz / horizMag : transform.forward;
        float elevRad = minLaunchElevationDeg * Mathf.Deg2Rad;
        return (horizDir * Mathf.Cos(elevRad) + Vector3.up * Mathf.Sin(elevRad)) * speed;
    }

    private bool SolveBallistic(Vector3 firePos, Vector3 targetPos, float speed, out Vector3 launchVelocity)
    {
        Vector3 delta = targetPos - firePos;
        Vector3 horizontal = new Vector3(delta.x, 0f, delta.z);
        float d = horizontal.magnitude;

        if (d < 0.01f)
        {
            launchVelocity = Vector3.up * speed;
            return false;
        }

        float h = delta.y;
        float g = -Physics.gravity.y;
        float v2 = speed * speed;
        float disc = v2 * v2 - g * (g * d * d + 2f * h * v2);

        Vector3 horizUnit = horizontal / d;

        if (disc < 0f)
        {
            // Out of range at this speed — best-effort 45° lob toward the target.
            launchVelocity = (horizUnit + Vector3.up).normalized * speed;
            return false;
        }

        float tanTheta = (v2 - Mathf.Sqrt(disc)) / (g * d);
        float theta = Mathf.Atan(tanTheta);
        launchVelocity = (horizUnit * Mathf.Cos(theta) + Vector3.up * Mathf.Sin(theta)) * speed;
        return true;
    }

    private bool HasLineOfSightToPlayer(out Vector3 playerPos)
    {
        playerPos = Vector3.zero;

        if (_player == null)
        {
            ShootingControls pc = FindAnyObjectByType<ShootingControls>();
            if (pc == null)
                return false;
            _player = pc.transform;
        }

        Transform origin = _turretTransform != null ? _turretTransform : transform;
        Vector3 originPos = origin.position + Vector3.up * 0.05f;
        playerPos = _player.position;

        Vector3 toPlayer = playerPos - originPos;
        float dist = toPlayer.magnitude;
        if (dist < 0.01f || dist > losMaxRange)
            return false;

        // Walk all hits in distance order so our own colliders (which almost always sit
        // between the turret and the player) don't masquerade as "LOS clear". First
        // non-self hit decides: player → clear, anything else (MRUK wall etc.) → blocked.
        RaycastHit[] hits = Physics.RaycastAll(
            originPos,
            toPlayer / dist,
            dist,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);

        if (hits.Length == 0)
            return true;

        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (var hit in hits)
        {
            Transform t = hit.transform;
            if (t == null)
                continue;

            if (t == transform || t.IsChildOf(transform))
                continue;

            return t == _player || t.IsChildOf(_player);
        }

        return true;
    }

    private bool AimTurretYawAtWorldPosition(Vector3 worldPos)
    {
        if (_turretTransform == null)
            return true;

        Vector3 toTarget = worldPos - _turretTransform.position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.0001f)
            return true;

        float desiredYaw = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
        Vector3 euler = _turretTransform.eulerAngles;
        float newYaw = Mathf.MoveTowardsAngle(euler.y, desiredYaw, aimYawSpeedDegPerSec * Time.deltaTime);
        euler.y = newYaw;
        _turretTransform.eulerAngles = euler;

        return Mathf.Abs(Mathf.DeltaAngle(newYaw, desiredYaw)) <= aimSettleThresholdDeg;
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
        int lo = Mathf.Max(1, minScanSwipes);
        int hi = Mathf.Max(lo, maxScanSwipes);
        // Random.Range(int,int) is inclusive-exclusive, so +1 on the upper bound for 1..4 inclusive.
        _remainingHalfSweeps = Random.Range(lo, hi + 1);
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
