using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class LevelGenerator : NetworkBehaviour
{
    public static LevelGenerator Instance { get; private set; }

    [Header("Generation Settings")]
    [SerializeField] private int maxRooms = 15;
    [SerializeField] private int maxPlacementAttempts = 500;
    [SerializeField] private LayerMask roomBoundsLayer;

    [Header("Room Prefabs")]
    [SerializeField] private List<Room> roomPrefabs = new List<Room>();
    [SerializeField] private GameObject deadEndWallPrefab;

    private NetworkVariable<int> levelSeed = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly List<Transform> openSockets = new List<Transform>();
    private readonly List<Room> spawnedRooms = new List<Room>();
    private readonly Dictionary<Transform, Room> socketOwner = new Dictionary<Transform, Room>();
    private System.Random prng;

    /// <summary>
    /// True once GenerateLevel() has finished running locally (on this peer).
    /// Both server and client set this independently after running their own generation pass.
    /// </summary>
    public bool IsGenerationComplete { get; private set; }

    /// <summary>
    /// Raised locally after this peer finishes generating the level.
    /// </summary>
    public event System.Action OnGenerationComplete;

    private void Awake()
    {
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        levelSeed.OnValueChanged += OnSeedReceived;

        if (IsServer)
        {
            levelSeed.Value = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
        }
        else if (levelSeed.Value != 0)
        {
            GenerateLevel(levelSeed.Value);
        }
    }

    public override void OnNetworkDespawn()
    {
        levelSeed.OnValueChanged -= OnSeedReceived;
        if (Instance == this)
            Instance = null;
    }

    private void OnSeedReceived(int previousSeed, int newSeed)
    {
        if (newSeed != 0)
        {
            GenerateLevel(newSeed);
        }
    }

    public void GenerateLevel(int seed)
    {
        IsGenerationComplete = false;

        if (StartingRoom.Instance == null)
        {
            Debug.LogError("[LevelGenerator] StartingRoom.Instance not found. " +
                "Make sure LevelGenerator lives in the level scene (loaded additively), not a persistent/bootstrap scene.");
            return;
        }

        ClearExistingLevel();
        prng = new System.Random(seed);

        // 1. Hook into the existing Starting Room instead of instantiating one
        Room startRoom = StartingRoom.Instance;
        spawnedRooms.Add(startRoom);
        openSockets.AddRange(startRoom.doorwaySockets);
        foreach (var socket in startRoom.doorwaySockets)
            socketOwner[socket] = startRoom;

        int attempts = 0;

        // 2. Main Generation Loop (branches directly off the Starting Room's socket)
        while (spawnedRooms.Count < maxRooms && openSockets.Count > 0 && attempts < maxPlacementAttempts)
        {
            attempts++;

            int targetSocketIndex = prng.Next(0, openSockets.Count);
            Transform targetSocket = openSockets[targetSocketIndex];

            if (!socketOwner.TryGetValue(targetSocket, out Room targetRoom))
            {
                Debug.LogWarning("[LevelGenerator] Socket missing owner mapping; skipping.");
                openSockets.RemoveAt(targetSocketIndex);
                continue;
            }

            // NEW: pick a prefab, avoiding the same prefab as the room we're connecting to.
            Room candidatePrefab = PickPrefabExcluding(targetRoom.sourcePrefab);
            if (candidatePrefab == null)
            {
                // No valid alternative prefab exists (e.g. only one prefab configured) - cap this socket instead.
                attempts++;
                continue;
            }

            Room candidateRoom = Instantiate(candidatePrefab, transform);
            candidateRoom.sourcePrefab = candidatePrefab;

            int newSocketIndex = prng.Next(0, candidateRoom.doorwaySockets.Count);
            Transform newSocket = candidateRoom.doorwaySockets[newSocketIndex];

            AlignRoomToSocket(candidateRoom.transform, newSocket, targetSocket);
            Physics.SyncTransforms();

            if (IsRoomOverlapping(candidateRoom))
            {
                DestroyImmediate(candidateRoom.gameObject);
            }
            else
            {
                spawnedRooms.Add(candidateRoom);
                openSockets.RemoveAt(targetSocketIndex);
                socketOwner.Remove(targetSocket);

                for (int i = 0; i < candidateRoom.doorwaySockets.Count; i++)
                {
                    if (i != newSocketIndex)
                    {
                        Transform remainingSocket = candidateRoom.doorwaySockets[i];
                        openSockets.Add(remainingSocket);
                        socketOwner[remainingSocket] = candidateRoom;
                    }
                }
            }
        }

        // 3. Seal all remaining unused doorways
        CapOpenDoorways();

        IsGenerationComplete = true;
        OnGenerationComplete?.Invoke();
    }

    /// <summary>
    /// Picks a random room prefab, excluding the given prefab if possible.
    /// Returns null if excludedPrefab is the only candidate available (no valid alternative).
    /// </summary>
    private Room PickPrefabExcluding(Room excludedPrefab)
    {
        if (excludedPrefab == null || roomPrefabs.Count <= 1)
            return roomPrefabs[prng.Next(0, roomPrefabs.Count)];

        // Build the eligible list (cheap for typical small prefab counts).
        List<Room> eligible = new List<Room>(roomPrefabs.Count);
        foreach (var prefab in roomPrefabs)
        {
            if (prefab != excludedPrefab)
                eligible.Add(prefab);
        }

        if (eligible.Count == 0)
            return null; // only one prefab type configured total

        return eligible[prng.Next(0, eligible.Count)];
    }

    private void AlignRoomToSocket(Transform newRoom, Transform newSocket, Transform targetSocket)
    {
        Quaternion targetRotation = Quaternion.LookRotation(-targetSocket.forward, Vector3.up);
        Quaternion rotationOffset = targetRotation * Quaternion.Inverse(newSocket.rotation);
        newRoom.rotation = rotationOffset * newRoom.rotation;

        Vector3 positionOffset = targetSocket.position - newSocket.position;
        newRoom.position += positionOffset;
    }

    private Collider[] overlapHitBuffer = new Collider[16];

    private bool IsRoomOverlapping(Room candidateRoom)
    {
        foreach (BoxCollider box in candidateRoom.boundsColliders)
        {
            Vector3 worldCenter = box.transform.TransformPoint(box.center);
            Vector3 worldHalfExtents = Vector3.Scale(box.size * 0.5f, box.transform.lossyScale);
            Quaternion worldRotation = box.transform.rotation;

            int hitCount = Physics.OverlapBoxNonAlloc(
                worldCenter,
                worldHalfExtents,
                overlapHitBuffer,
                worldRotation,
                roomBoundsLayer,
                QueryTriggerInteraction.Collide
            );

            // If the buffer was completely filled, there may be more hits we didn't see.
            // Grow it and retry this box so we don't miss an overlap.
            if (hitCount == overlapHitBuffer.Length)
            {
                overlapHitBuffer = new Collider[overlapHitBuffer.Length * 2];
                hitCount = Physics.OverlapBoxNonAlloc(
                    worldCenter,
                    worldHalfExtents,
                    overlapHitBuffer,
                    worldRotation,
                    roomBoundsLayer,
                    QueryTriggerInteraction.Collide
                );
            }

            for (int i = 0; i < hitCount; i++)
            {
                if (overlapHitBuffer[i].transform.root != candidateRoom.transform.root &&
                    !overlapHitBuffer[i].transform.IsChildOf(candidateRoom.transform))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void CapOpenDoorways()
    {
        if (deadEndWallPrefab == null) return;

        foreach (Transform socket in openSockets)
        {
            Quaternion wallRotation = Quaternion.LookRotation(-socket.forward, Vector3.up);
            Instantiate(deadEndWallPrefab, socket.position, wallRotation, transform);
        }

        openSockets.Clear();
    }

    private void ClearExistingLevel()
    {
        // Only destroys children of LevelGenerator; leaves StartingRoom untouched
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            DestroyImmediate(transform.GetChild(i).gameObject);
        }

        openSockets.Clear();
        spawnedRooms.Clear();
        socketOwner.Clear();
    }
}
