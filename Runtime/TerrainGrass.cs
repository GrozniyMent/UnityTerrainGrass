using UnityEngine;
using System.Collections.Generic;
using System;

namespace TerrainGrass {
    [RequireComponent(typeof(Terrain))]
    public class TerrainGrass : MonoBehaviour
    {
        [Header("Grass Settings")]
        public GameObject grassLOD0;
        public GameObject grassLOD1;
        public GameObject grassLOD2;
        [Range(10, 200)] public float viewDistance = 50f;
        public float seaLevel = 0f;

        [Header("LOD Distances")]
        [Range(5, 100)] public float lod0Distance = 20f;
        [Range(15, 150)] public float lod1Distance = 40f;

        [Header("LOD Density")]
        [Range(0.1f, 10)] public float highDensity = 5f;
        [Range(0.1f, 10)] public float mediumDensity = 3f;
        [Range(0.1f, 10)] public float lowDensity = 2f;

        [Header("Smooth Transition")]
        [Range(0.1f, 5f)] public float transitionSpeed = 1f;
        [Range(0f, 1f)] public float minDensityFactor = 0.3f;

        [Header("Size Variation")]
        public float minSize = 0.8f;
        public float maxSize = 1.2f;

        [Header("Wind Settings")]
        [Range(0, 2)] public float windStrength = 0.5f;
        [Range(0, 5)] public float windFrequency = 2f;
        public Vector3 windDirection = new Vector3(1, 0, 0);

        [Header("FOV Settings")]
        [Range(30, 180)] public float fovAngle = 90f;
        public float viewConeOffset = 5f;

        [Header("Debug Settings")]
        public bool showDebugInfo = true;
        public bool showCellBounds = true;
        public bool showViewCone = true;
        public bool showGrassPositions = false;
        public Color cellColor = new Color(0, 1, 0, 0.25f);
        public Color viewConeColor = new Color(1, 1, 0, 0.3f);
        public Color grassPosColor = Color.green;
        public Color playerCellColor = Color.red;
        public Color adjacentCellColor = new Color(1, 0.5f, 0, 0.3f);

        private Mesh[] grassMeshes = new Mesh[3];
        private Material[] grassMaterials = new Material[3];
        
        private List<Matrix4x4>[] lodMatrices = new List<Matrix4x4>[3];
        private Matrix4x4[] batchMatrices = new Matrix4x4[1023];
        
        private Terrain terrain;
        private TerrainData terrainData;
        private Vector3 terrainPosition;
        private Vector3 terrainSize;
        private Camera mainCamera;
        private Transform cameraTransform;
        
        private class CellData
        {
            public GrassInstance[] baseSet;
            public int currentCount;
            public int targetCount;
            public int transitionStartCount;
            public Bounds bounds;
            public int priority;
            public float transitionProgress;
            public bool isTransitioning;
        }
        
        private Dictionary<Vector2Int, CellData> cellData = new Dictionary<Vector2Int, CellData>(64);
        private const float CELL_SIZE = 15f;
        private Vector2Int lastCameraCell;
        private Vector3 lastCameraForward;
        private Plane[] cameraFrustumPlanes = new Plane[6];
        private const float MAX_DENSITY = 10f;
        
        private const int MAX_BATCH_SIZE = 1023;
        private const float ROTATION_THRESHOLD = 5f;
        private static readonly Vector3 BOUNDS_SIZE = new Vector3(CELL_SIZE, 1000, CELL_SIZE);

        private int totalGrassInstances = 0;
        private int[] lodInstanceCounts = new int[3];
        private int renderedBatches = 0;
        private int activeCells = 0;
        private float generationTime = 0;
        private float renderTime = 0;
        private GUIStyle debugStyle;

        private struct GrassInstance
        {
            public Vector3 position;
            public Quaternion rotation;
            public float scale;
            public float sortKey;
        }

        private Queue<Vector2Int> pendingCells = new Queue<Vector2Int>();
        private HashSet<Vector2Int> pendingSet = new HashSet<Vector2Int>();
        [Tooltip("Maximum number of cells to generate per frame")]
        public int maxCellsPerFrame = 2;

        void Start()
        {
            terrain = GetComponent<Terrain>();
            terrainData = terrain.terrainData;
            terrainPosition = terrain.transform.position;
            terrainSize = terrainData.size;
            
            FindMainCamera();
            
            for (int i = 0; i < 3; i++)
            {
                lodMatrices[i] = new List<Matrix4x4>(2048);
            }
            
            LoadLODAssets(0, grassLOD0);
            LoadLODAssets(1, grassLOD1);
            LoadLODAssets(2, grassLOD2);
            
            debugStyle = new GUIStyle();
            debugStyle.fontSize = 16;
            debugStyle.normal.textColor = Color.white;
            debugStyle.padding = new RectOffset(10, 10, 10, 10);
            debugStyle.contentOffset = new Vector2(5, 5);
        }

        void LoadLODAssets(int lodLevel, GameObject prefab)
        {
            if (prefab != null)
            {
                MeshFilter prefabFilter = prefab.GetComponent<MeshFilter>();
                if (prefabFilter != null && prefabFilter.sharedMesh != null)
                {
                    grassMeshes[lodLevel] = prefabFilter.sharedMesh;
                    MeshRenderer prefabRenderer = prefab.GetComponent<MeshRenderer>();
                    if (prefabRenderer != null && prefabRenderer.sharedMaterial != null)
                    {
                        grassMaterials[lodLevel] = new Material(prefabRenderer.sharedMaterial);
                        grassMaterials[lodLevel].enableInstancing = true;
                    }
                }
            }
            
            if (grassMaterials[lodLevel] == null)
            {
                Debug.LogWarning($"Grass LOD{lodLevel} material not assigned. Using default.");
                grassMaterials[lodLevel] = new Material(Shader.Find("Standard"));
                grassMaterials[lodLevel].enableInstancing = true;
            }
        }

        void FindMainCamera()
        {
            mainCamera = Camera.main;
            if (mainCamera != null)
            {
                cameraTransform = mainCamera.transform;
            }
            else
            {
                Debug.LogError("Main camera not found! Please tag your camera as MainCamera.");
            }
        }

        void Update()
        {
            if (mainCamera == null || cameraTransform == null) 
            {
                FindMainCamera();
                if (mainCamera == null || cameraTransform == null) return;
            }

            Vector3 normalizedWind = windDirection.normalized;
            for (int i = 0; i < 3; i++)
            {
                if (grassMaterials[i] != null)
                {
                    grassMaterials[i].SetFloat("_WindStrength", windStrength);
                    grassMaterials[i].SetFloat("_WindFrequency", windFrequency);
                    grassMaterials[i].SetVector("_WindDirection", normalizedWind);
                }
            }

            GeometryUtility.CalculateFrustumPlanes(mainCamera, cameraFrustumPlanes);

            Vector3 camPos = cameraTransform.position;
            Vector3 camForward = cameraTransform.forward;
            Vector2Int cameraCell = new Vector2Int(
                Mathf.FloorToInt(camPos.x / CELL_SIZE),
                Mathf.FloorToInt(camPos.z / CELL_SIZE)
            );

            bool cameraMoved = cameraCell != lastCameraCell;
            bool cameraRotated = Vector3.Angle(camForward, lastCameraForward) > ROTATION_THRESHOLD;
            
            if (cameraMoved || cameraRotated)
            {
                float startTime = Time.realtimeSinceStartup;
                UpdateVisibleCells(cameraCell, camPos, camForward);
                generationTime = Time.realtimeSinceStartup - startTime;
                lastCameraCell = cameraCell;
                lastCameraForward = camForward;
            }

            ProcessPendingCells();

            UpdateDensityTransitions();

            PrepareLODMatrices(camPos);

            float renderStartTime = Time.realtimeSinceStartup;
            RenderBatches();
            renderTime = Time.realtimeSinceStartup - renderStartTime;
        }

        void ProcessPendingCells()
        {
            int processed = 0;
            while (pendingCells.Count > 0 && processed < maxCellsPerFrame)
            {
                Vector2Int cell = pendingCells.Dequeue();
                pendingSet.Remove(cell);
                GenerateGrassInCell(cell, 2);
                processed++;
            }
        }

        void UpdateDensityTransitions()
        {
            foreach (var cell in cellData.Values)
            {
                if (cell.isTransitioning)
                {
                    cell.transitionProgress = Mathf.Min(1f, cell.transitionProgress + Time.deltaTime * transitionSpeed);
                    cell.currentCount = (int)Mathf.Lerp(cell.transitionStartCount, cell.targetCount, cell.transitionProgress);
                    
                    if (cell.transitionProgress >= 1f)
                    {
                        cell.isTransitioning = false;
                    }
                }
            }
        }

        void PrepareLODMatrices(Vector3 cameraPosition)
        {
            for (int i = 0; i < 3; i++)
            {
                lodMatrices[i].Clear();
                lodInstanceCounts[i] = 0;
            }

            foreach (var data in cellData.Values)
            {
                if (GeometryUtility.TestPlanesAABB(cameraFrustumPlanes, data.bounds))
                {
                    int count = data.currentCount;
                    for (int i = 0; i < count; i++)
                    {
                        ref GrassInstance instance = ref data.baseSet[i];
                        
                        if (instance.position.y < seaLevel) continue;

                        float distance = Vector3.Distance(instance.position, cameraPosition);
                        
                        int lodLevel = 0;
                        if (distance > lod1Distance) lodLevel = 2;
                        else if (distance > lod0Distance) lodLevel = 1;

                        Matrix4x4 matrix = Matrix4x4.TRS(
                            instance.position,
                            instance.rotation,
                            Vector3.one * instance.scale
                        );

                        if (lodLevel < 3 && grassMeshes[lodLevel] != null)
                        {
                            lodMatrices[lodLevel].Add(matrix);
                            lodInstanceCounts[lodLevel]++;
                        }
                    }
                }
            }
        }

        void RenderBatches()
        {
            renderedBatches = 0;
            
            for (int lod = 0; lod < 3; lod++)
            {
                if (grassMeshes[lod] == null || grassMaterials[lod] == null) continue;
                
                int instanceCount = lodMatrices[lod].Count;
                if (instanceCount == 0) continue;

                int batchCount = Mathf.CeilToInt((float)instanceCount / MAX_BATCH_SIZE);
                renderedBatches += batchCount;

                for (int i = 0; i < batchCount; i++)
                {
                    int startIndex = i * MAX_BATCH_SIZE;
                    int count = Mathf.Min(MAX_BATCH_SIZE, instanceCount - startIndex);
                    
                    lodMatrices[lod].CopyTo(startIndex, batchMatrices, 0, count);
                    
                    Graphics.DrawMeshInstanced(
                        grassMeshes[lod], 
                        0, 
                        grassMaterials[lod], 
                        batchMatrices, 
                        count,
                        null,
                        UnityEngine.Rendering.ShadowCastingMode.Off,
                        false
                    );
                }
            }
        }

        void UpdateVisibleCells(Vector2Int cameraCell, Vector3 cameraPosition, Vector3 cameraForward)
        {
            float maxAngle = (fovAngle * 0.5f) + viewConeOffset;
            float viewDistanceSqr = viewDistance * viewDistance;
            float cosMaxAngle = Mathf.Cos(maxAngle * Mathf.Deg2Rad);

            HashSet<Vector2Int> adjacentCells = GetAdjacentCells(cameraCell);

            List<Vector2Int> cellsToRemove = new List<Vector2Int>(cellData.Count);
            foreach (var cell in cellData.Keys)
            {
                if (adjacentCells.Contains(cell)) continue;
                if (cell == cameraCell) continue;

                Vector3 cellCenter = new Vector3(
                    (cell.x + 0.5f) * CELL_SIZE,
                    cameraPosition.y,
                    (cell.y + 0.5f) * CELL_SIZE
                );
                
                Vector3 toCell = cellCenter - cameraPosition;
                float distSqr = toCell.sqrMagnitude;

                if (distSqr > viewDistanceSqr)
                {
                    cellsToRemove.Add(cell);
                    continue;
                }

                float dot = Vector3.Dot(cameraForward, toCell.normalized);
                if (dot <= cosMaxAngle)
                {
                    cellsToRemove.Add(cell);
                }
            }
            
            foreach (var cell in cellsToRemove)
            {
                cellData.Remove(cell);
            }

            List<Vector2Int> pendingToRemove = new List<Vector2Int>();
            foreach (Vector2Int cell in pendingSet)
            {
                Vector3 cellCenter = new Vector3(
                    (cell.x + 0.5f) * CELL_SIZE,
                    cameraPosition.y,
                    (cell.y + 0.5f) * CELL_SIZE
                );
                Vector3 toCell = cellCenter - cameraPosition;
                float distSqr = toCell.sqrMagnitude;
                
                if (distSqr > viewDistanceSqr || 
                    Vector3.Dot(cameraForward, toCell.normalized) <= cosMaxAngle)
                {
                    pendingToRemove.Add(cell);
                }
            }

            foreach (var cell in pendingToRemove)
            {
                pendingSet.Remove(cell);
            }

            ProcessCell(cameraCell, 0);

            foreach (var cell in adjacentCells)
            {
                ProcessCell(cell, 0);
            }

            int cellsInView = Mathf.CeilToInt(viewDistance / CELL_SIZE);
            activeCells = 0;
            totalGrassInstances = 0;
            
            for (int x = -cellsInView; x <= cellsInView; x++)
            {
                for (int z = -cellsInView; z <= cellsInView; z++)
                {
                    Vector2Int cell = new Vector2Int(cameraCell.x + x, cameraCell.y + z);
                    float dist = Vector2Int.Distance(cell, cameraCell) * CELL_SIZE;
                    
                    if (dist > viewDistance) continue;
                    if (cell == cameraCell || adjacentCells.Contains(cell)) continue;
                        
                    Vector3 cellCenter = new Vector3(
                        (cell.x + 0.5f) * CELL_SIZE,
                        cameraPosition.y,
                        (cell.y + 0.5f) * CELL_SIZE
                    );
                    
                    Vector3 toCell = cellCenter - cameraPosition;
                    float dot = Vector3.Dot(cameraForward, toCell.normalized);
                    
                    if (dot > cosMaxAngle)
                    {
                        if (!cellData.ContainsKey(cell) && !pendingSet.Contains(cell))
                        {
                            pendingSet.Add(cell);
                            pendingCells.Enqueue(cell);
                        }
                    }
                    
                    if (cellData.TryGetValue(cell, out CellData data))
                    {
                        activeCells++;
                        totalGrassInstances += data.currentCount;
                    }
                }
            }
            
            foreach (var cell in adjacentCells)
            {
                if (cellData.TryGetValue(cell, out CellData data))
                {
                    activeCells++;
                    totalGrassInstances += data.currentCount;
                }
            }
            
            if (cellData.TryGetValue(cameraCell, out CellData playerData))
            {
                activeCells++;
                totalGrassInstances += playerData.currentCount;
            }
        }

        void ProcessCell(Vector2Int cell, int priority)
        {
            if (cellData.TryGetValue(cell, out CellData data))
            {
                if (data.priority != priority)
                {
                    StartDensityTransition(data, priority);
                }
            }
            else
            {
                if (priority == 0)
                {
                    GenerateGrassInCell(cell, priority);
                }
            }
        }

        void StartDensityTransition(CellData data, int newPriority)
        {
            if (data.priority == newPriority) return;
            
            float density = GetDensityForPriority(newPriority);
            int newTargetCount = Mathf.RoundToInt(data.baseSet.Length * density / MAX_DENSITY);
            
            data.transitionStartCount = data.currentCount;
            data.targetCount = newTargetCount;
            data.priority = newPriority;
            data.transitionProgress = 0f;
            data.isTransitioning = true;
        }

        void GenerateGrassInCell(Vector2Int cell, int priority)
        {
            GrassInstance[] baseSet = GenerateGrassInstances(cell);
            float density = GetDensityForPriority(priority);
            int targetCount = Mathf.RoundToInt(baseSet.Length * density / MAX_DENSITY);
            
            Vector3 cellCenter = new Vector3(
                (cell.x + 0.5f) * CELL_SIZE,
                0,
                (cell.y + 0.5f) * CELL_SIZE
            );
            
            cellData[cell] = new CellData
            {
                baseSet = baseSet,
                currentCount = targetCount,
                targetCount = targetCount,
                bounds = new Bounds(cellCenter, BOUNDS_SIZE),
                priority = priority
            };
        }

        GrassInstance[] GenerateGrassInstances(Vector2Int cell)
        {
            int baseCount = Mathf.RoundToInt(CELL_SIZE * CELL_SIZE * MAX_DENSITY);
            GrassInstance[] instances = new GrassInstance[baseCount];
            System.Random rand = new System.Random(cell.x * 10000 + cell.y);
            
            for (int i = 0; i < baseCount; i++)
            {
                Vector3 position = new Vector3(
                    (float)(cell.x * CELL_SIZE + rand.NextDouble() * CELL_SIZE),
                    0,
                    (float)(cell.y * CELL_SIZE + rand.NextDouble() * CELL_SIZE)
                );

                position.y = terrain.SampleHeight(position);
                
                if (position.y < terrain.transform.position.y) 
                {
                    position.y = terrain.transform.position.y;
                }

                float normX = (position.x - terrainPosition.x) / terrainSize.x;
                float normZ = (position.z - terrainPosition.z) / terrainSize.z;
                
                Vector3 normal = terrainData.GetInterpolatedNormal(normX, normZ);
                
                float yRotation = (float)(rand.NextDouble() * 360);
                
                Quaternion normalRotation = Quaternion.FromToRotation(Vector3.up, normal);
                Quaternion finalRotation = normalRotation * Quaternion.Euler(0, yRotation, 0);

                instances[i] = new GrassInstance
                {
                    position = position,
                    rotation = finalRotation,
                    scale = minSize + (float)rand.NextDouble() * (maxSize - minSize),
                    sortKey = (float)rand.NextDouble()
                };
            }
            
            Array.Sort(instances, (a, b) => a.sortKey.CompareTo(b.sortKey));
            return instances;
        }

        float GetDensityForPriority(int priority)
        {
            return priority switch
            {
                0 => highDensity,
                1 => mediumDensity,
                _ => lowDensity
            };
        }

        HashSet<Vector2Int> GetAdjacentCells(Vector2Int centerCell)
        {
            HashSet<Vector2Int> adjacentCells = new HashSet<Vector2Int>();
            
            for (int x = -1; x <= 1; x++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    if (x == 0 && z == 0) continue;
                    adjacentCells.Add(new Vector2Int(centerCell.x + x, centerCell.y + z));
                }
            }
            
            return adjacentCells;
        }

        void OnGUI()
        {
            if (!showDebugInfo) return;
            
            string debugText = $"Dynamic Grass Debug\n" +
                            $"Active Cells: {activeCells}\n" +
                            $"Total Instances: {totalGrassInstances}\n" +
                            $"LOD0: {lodInstanceCounts[0]} | LOD1: {lodInstanceCounts[1]} | LOD2: {lodInstanceCounts[2]}\n" +
                            $"Rendered Batches: {renderedBatches}\n" +
                            $"Generation Time: {generationTime * 1000:0.00}ms\n" +
                            $"Render Time: {renderTime * 1000:0.00}ms\n" +
                            $"Camera Cell: {lastCameraCell}\n" +
                            $"Sea Level: {seaLevel}\n" +
                            $"LOD Distances: {lod0Distance}/{lod1Distance}/{viewDistance}\n" +
                            $"Densities: High:{highDensity} | Med:{mediumDensity} | Low:{lowDensity}\n" +
                            $"FOV Angle: {fovAngle}°\n" +
                            $"Transition Speed: {transitionSpeed}\n" +
                            $"Pending Cells: {pendingCells.Count}";
            
            GUI.Box(new Rect(10, 10, 400, 330), "");
            GUI.Label(new Rect(20, 20, 380, 310), debugText, debugStyle);
        }

        void OnDrawGizmosSelected()
        {
            if (mainCamera == null || cameraTransform == null) return;
            
            Vector3 cameraPos = cameraTransform.position;
            Vector3 cameraForward = cameraTransform.forward;
            Vector2Int cameraCell = new Vector2Int(
                Mathf.FloorToInt(cameraPos.x / CELL_SIZE),
                Mathf.FloorToInt(cameraPos.z / CELL_SIZE)
            );
            
            HashSet<Vector2Int> adjacentCells = GetAdjacentCells(cameraCell);

            if (showViewCone)
            {
                Gizmos.color = viewConeColor;
                float halfFOV = (fovAngle + viewConeOffset) * 0.5f * Mathf.Deg2Rad;
                float coneDistance = viewDistance;
                
                Vector3[] conePoints = new Vector3[4];
                
                Vector3 leftDir = Quaternion.AngleAxis(-halfFOV * Mathf.Rad2Deg, Vector3.up) * cameraForward;
                conePoints[0] = cameraPos + leftDir * coneDistance;
                
                Vector3 rightDir = Quaternion.AngleAxis(halfFOV * Mathf.Rad2Deg, Vector3.up) * cameraForward;
                conePoints[1] = cameraPos + rightDir * coneDistance;
                
                conePoints[2] = cameraPos + cameraForward * coneDistance;
                
                Gizmos.DrawLine(cameraPos, conePoints[0]);
                Gizmos.DrawLine(cameraPos, conePoints[1]);
                Gizmos.DrawLine(conePoints[0], conePoints[2]);
                Gizmos.DrawLine(conePoints[1], conePoints[2]);
                
                Vector3[] fillVerts = new Vector3[] { cameraPos, conePoints[0], conePoints[2], conePoints[1] };
                UnityEditor.Handles.DrawAAConvexPolygon(fillVerts);
            }
            
            if (showCellBounds)
            {
                foreach (var kvp in cellData)
                {
                    Vector2Int cell = kvp.Key;
                    CellData data = kvp.Value;
                    
                    Vector3 center = data.bounds.center;
                    Vector3 size = data.bounds.size;
                    size.y = 0.1f;
                    
                    if (cell == cameraCell || adjacentCells.Contains(cell))
                    {
                        Gizmos.color = playerCellColor;
                    }
                    else
                    {
                        Gizmos.color = cellColor;
                    }
                    
                    if (data.isTransitioning)
                    {
                        Gizmos.color = new Color(Gizmos.color.r, Gizmos.color.g, Gizmos.color.b, 0.5f);
                    }
                    
                    Gizmos.DrawWireCube(center, size);
                    
                    #if UNITY_EDITOR
                    string status = data.isTransitioning ? $"T:{data.transitionProgress:F1}" : $"P{data.priority}";
                    UnityEditor.Handles.Label(
                        center + Vector3.up * 2f, 
                        $"{data.currentCount}/{data.baseSet.Length} ({status})",
                        new GUIStyle { normal = { textColor = Color.white }, fontSize = 10 }
                    );
                    #endif
                }
            }
            
            Gizmos.color = Color.blue;
            Gizmos.DrawLine(
                new Vector3(-1000, seaLevel, -1000),
                new Vector3(1000, seaLevel, 1000)
            );
            
            if (showGrassPositions)
            {
                Gizmos.color = grassPosColor;
                foreach (var kvp in cellData)
                {
                    CellData data = kvp.Value;
                    
                    if (GeometryUtility.TestPlanesAABB(cameraFrustumPlanes, data.bounds))
                    {
                        int count = Mathf.Min(data.currentCount, data.baseSet.Length);
                        for (int i = 0; i < count; i++)
                        {
                            ref GrassInstance instance = ref data.baseSet[i];
                            if (instance.position.y >= seaLevel)
                            {
                                Gizmos.DrawWireSphere(instance.position, 0.1f);
                            }
                        }
                    }
                }
            }
        }

        public void SetHighDensity(float density) => highDensity = Mathf.Clamp(density, 0.1f, 10);
        public void SetMediumDensity(float density) => mediumDensity = Mathf.Clamp(density, 0.1f, 10);
        public void SetLowDensity(float density) => lowDensity = Mathf.Clamp(density, 0.1f, 10);
        public void SetViewDistance(float distance) => viewDistance = Mathf.Clamp(distance, 10, 200);
        public void SetSeaLevel(float level) => seaLevel = level;
        public void SetFOV(float angle) => fovAngle = Mathf.Clamp(angle, 30, 180);
        public void SetTransitionSpeed(float speed) => transitionSpeed = Mathf.Clamp(speed, 0.1f, 5f);
        public void ToggleDebug() => showDebugInfo = !showDebugInfo;
    }
}