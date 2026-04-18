using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class EnemyTankAI : MonoBehaviour
{
    [HideInInspector] public float moveSpeed = 0.5f;
    [HideInInspector] public float chaseRange = 4f;
    [HideInInspector] public float stopRange = 1.25f;

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

        if (_player != null)
        {
            Vector3 toPlayer = _player.position - transform.position;
            toPlayer.y = 0f;
            float distanceToPlayer = toPlayer.magnitude;

            if (distanceToPlayer <= chaseRange)
            {
                if (distanceToPlayer > stopRange)
                {
                    _agent.SetDestination(_player.position);
                }
                else
                {
                    _agent.ResetPath();
                }
                return;
            }
        }

        if (Time.time >= _nextRepathTime && (!_agent.hasPath || _agent.remainingDistance <= _agent.stoppingDistance + 0.1f))
        {
            SetRandomDestination();
            _nextRepathTime = Time.time + repathInterval;
        }
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
