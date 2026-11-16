using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class PbStandaloneApp : MonoBehaviour
{
    private const float CellSize = 20f;
    private const float MoveStep = 20f;
    private const float WaterLevel = -2f;
    private const float ToolsWidth = 330f;
    private const float StatusHeight = 28f;
    private bool showPaths;
    private float HeaderHeight => showPaths ? 183f : 88f;
    private GUIStyle titleStyle;
    private GUIStyle mutedStyle;
    private GUIStyle tabStyle;
    private GUIStyle activeTabStyle;
    private GUIStyle axisLabelStyle;
    private int transformTool; // 0 select, 1 move, 2 rotate, 3 uniform scale
    private int gizmoAxis = -1;
    private Vector2 gizmoLastMouse;
    private float gizmoTurnAccumulated;
    private string objectXText = "";
    private string objectYText = "";
    private string objectHText = "";
    private string angleZText = "";
    private string angleXText = "";
    private string angleYText = "";
    private string objectScaleText = "";
    private Texture2D chromeTexture;
    private Texture2D panelTexture;
    private Texture2D accentTexture;
    private Texture2D fieldTexture;
    private string mapPath = "";
    private string outputPath = "";
    private string modelRoot = "";
    private string message = "Enter an editor .map path, then press Load.";
    private PbStorage storage;
    private string sourceHash;
    private GameObject mapRoot;
    private Camera viewCamera;
    private readonly List<PbPlacedObject> objects = new List<PbPlacedObject>();
    private readonly List<GameObject> entityRoots = new List<GameObject>();
    private readonly List<string> typeNames = new List<string>();
    private readonly List<string> typeData = new List<string>();
    private PbVoLoader voLoader;
    private PbPlacedObject selected;
    private readonly Dictionary<Renderer, Material[]> highlighted =
        new Dictionary<Renderer, Material[]>();
    private Vector2 catalogScroll;
    private Vector2 entityScroll;
    private Vector2 playerScroll;
    private Vector2 tileScroll;
    private string typeSearch = "";
    private string brushSearch = "";
    private string tileSearch = "";
    private int panelTab;
    private bool brushTypeCatalog;
    private int playerSide;
    private PbPlayers players;
    private int activeType;
    private Material markerMaterial;
    private Material selectedMaterial;
    private Mesh terrainMesh;
    private Mesh terrainVisualMesh;
    private MeshCollider terrainCollider;
    private GameObject overlayRoot;
    private int overlayTiles;
    private int overlaySurfaces;
    private int overlayMissing;
    private int terrainTextureCount;
    private int entityCount;
    private PbMapEntity selectedEntity;
    private readonly List<PbMapTile> tileRows = new List<PbMapTile>();
    private readonly HashSet<PbMapTile> selectedTiles = new HashSet<PbMapTile>();
    private PbMapTile primaryTile;
    private Vector3 lastDecalClickPosition;
    private readonly HashSet<int> editedTerrain = new HashSet<int>();
    private bool terrainBrush;
    private bool perspective;
    private Vector3 cameraTarget;
    private float cameraYaw = 35f;
    private float cameraPitch = 55f;
    private float cameraDistance;
    private int width;
    private int height;

    private void Start()
    {
        Application.runInBackground = true;
        viewCamera = Camera.main;
        if (viewCamera == null)
        {
            var cameraObject = new GameObject("Map camera");
            viewCamera = cameraObject.AddComponent<Camera>();
        }
        viewCamera.orthographic = true;
        viewCamera.backgroundColor = new Color(0.16f, 0.20f, 0.25f);
        viewCamera.clearFlags = CameraClearFlags.SolidColor;
        var lightObject = new GameObject("Sun");
        var sun = lightObject.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.2f;
        lightObject.transform.rotation = Quaternion.Euler(55, -30, 0);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.62f, 0.65f, 0.69f);
        markerMaterial = Resources.Load<Material>("PBMarker") ??
                         MakeMaterial(new Color(0.85f, 0.30f, 0.16f));
        selectedMaterial = Resources.Load<Material>("PBSelected") ??
                           MakeMaterial(Color.yellow);
        modelRoot = Environment.GetEnvironmentVariable("PB_MODEL_ROOT");
        if (string.IsNullOrEmpty(modelRoot))
        {
            string adjacent = Path.GetFullPath(Path.Combine(Application.dataPath, "../Models"));
            modelRoot = Directory.Exists(adjacent) ? adjacent :
                "E:/disasm2500_dev/MatrixGame_analysis/robots_pkg_extract";
        }

        string[] args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
            if (File.Exists(args[i]) && args[i].EndsWith(".map", StringComparison.OrdinalIgnoreCase))
            {
                mapPath = args[i];
                break;
            }
        if (mapPath.Length == 0)
            mapPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../Maps/Training.map"));
        if (File.Exists(mapPath)) LoadMap();
        string smokeMode = Environment.GetEnvironmentVariable("PB_EDITOR_SMOKE");
        if (smokeMode == "ui_preview")
        {
            if (storage == null) throw new InvalidOperationException("UI preview map is absent.");
            PbPlacedObject target = objects.Find(item => item.Row == 739) ?? objects[0];
            Select(target);
            transformTool = 1;
            viewCamera.orthographic = true;
            viewCamera.orthographicSize = 180f;
            viewCamera.transform.position = target.transform.position + Vector3.up * 500f;
            viewCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            StartCoroutine(CaptureUiPreview(
                Environment.GetEnvironmentVariable("PB_EDITOR_PREVIEW_OUTPUT")));
            return;
        }
        if (smokeMode == "preview" || smokeMode == "preview_target" ||
            smokeMode == "preview_tile" || smokeMode == "preview_surface")
        {
            if (storage == null || voLoader.LoadedModels < 1)
            {
                Debug.LogError("Preview test could not load models: " + message);
                Application.Quit(1);
                return;
            }
            if (smokeMode == "preview_tile" || smokeMode == "preview_surface")
            {
                int previewRow = 0;
                if (smokeMode == "preview_surface")
                    int.TryParse(Environment.GetEnvironmentVariable("PB_EDITOR_PREVIEW_ROW"),
                                 out previewRow);
                PbMapTile tile = smokeMode == "preview_tile" ? tileRows[0] :
                    overlayRoot.transform.Find("Surface " + previewRow).GetComponent<PbMapTile>();
                SelectTile(tile, false);
                Bounds bounds = tile.GetComponent<MeshFilter>().sharedMesh.bounds;
                Vector3 center = bounds.center;
                viewCamera.orthographic = true;
                viewCamera.orthographicSize = Mathf.Max(120f, bounds.extents.x * 1.4f,
                                                         bounds.extents.z * 1.4f);
                viewCamera.transform.position = center + Vector3.up * 400f;
                viewCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            }
            else if (smokeMode == "preview_target")
            {
                int previewRow = -1;
                string requestedRow = Environment.GetEnvironmentVariable("PB_EDITOR_PREVIEW_ROW");
                if (!string.IsNullOrEmpty(requestedRow))
                    int.TryParse(requestedRow, out previewRow);
                PbPlacedObject target = objects.Find(item => item.TypeIndex >= 0 &&
                    item.TypeIndex < typeData.Count &&
                    (previewRow < 0 || item.Row == previewRow) &&
                    typeData[item.TypeIndex].IndexOf("robotarget.vo",
                        StringComparison.OrdinalIgnoreCase) >= 0);
                if (target == null) throw new InvalidOperationException("Target model is absent.");
                viewCamera.orthographic = false;
                viewCamera.fieldOfView = 40f;
                viewCamera.transform.position = target.transform.position +
                    new Vector3(35f, 25f, 70f);
                viewCamera.transform.LookAt(target.transform.position + Vector3.up * 16f);
            }
            else ToggleCamera();
            CapturePreview(Environment.GetEnvironmentVariable("PB_EDITOR_PREVIEW_OUTPUT"));
            Application.Quit();
        }
        if (smokeMode == "1" || smokeMode == "add" || smokeMode == "delete" ||
            smokeMode == "terrain" || smokeMode == "models" || smokeMode == "camera" ||
            smokeMode == "entities" || smokeMode == "textures" ||
            smokeMode == "mirror" || smokeMode == "players" || smokeMode == "overlays" ||
            smokeMode == "geometry")
        {
            if (storage == null)
            {
                Debug.LogError("Smoke test could not load the map: " + message);
                Application.Quit(1);
                return;
            }
            string smokeOutput = Environment.GetEnvironmentVariable("PB_EDITOR_SMOKE_OUTPUT");
            if (string.IsNullOrEmpty(smokeOutput))
                throw new InvalidOperationException("Set PB_EDITOR_SMOKE_OUTPUT.");
            outputPath = smokeOutput;
            int expectedCount = objects.Count;
            if (smokeMode == "add") { AddObject(); expectedCount++; }
            if (smokeMode == "delete") { Select(objects[0]); DeleteSelected(); expectedCount--; }
            if (smokeMode == "models" && (voLoader.LoadedModels < 1 ||
                voLoader.MissingModels != 0 || voLoader.FailedModels != 0))
                throw new InvalidOperationException("Smoke test could not load all VO types.");
            if (smokeMode == "textures" &&
                terrainTextureCount != storage.Count("textures", "Name", 5))
                throw new InvalidOperationException("Smoke test terrain palette is incomplete.");
            if (smokeMode == "textures")
            {
                PbVoLoader.Asset target = voLoader.Load("Matrix/Obj/plant/robotarget.vo");
                PbVoLoader.Asset basis = voLoader.Load("Matrix/Cannon/Basis.vo");
                if (target == null || target.Materials[0].mainTexture == null ||
                    target.Materials[0].mainTextureScale.y != -1f ||
                    basis == null || basis.Materials[0].mainTexture == null ||
                    voLoader.MissingTextures != 0 || voLoader.LoadedTextures < 50)
                    throw new InvalidOperationException("VO textures or DDS orientation are incomplete.");
            }
            if (smokeMode == "players")
            {
                players.Resources[0, 0] = "2501";
                players.Ai[1, 3] = "0.75";
                players.Maintenance = "125";
            }
            if (smokeMode == "overlays")
            {
                if (overlayTiles != storage.Count("toptextures", "Name", 5) ||
                    overlaySurfaces != storage.Count("surfaces", "Name", 5) ||
                    overlayMissing != 0)
                    throw new InvalidOperationException("Map overlay textures are incomplete.");
                GameObject factory = entityRoots.Find(root =>
                    root.GetComponent<PbMapEntity>().Table == "buildings" &&
                    root.GetComponent<PbMapEntity>().Kind != 0);
                if (factory != null && factory.transform.Find("Factory ownership ring") == null)
                    throw new InvalidOperationException("Factory ownership ring is absent.");
                Transform tile = overlayRoot.transform.Find("Top tile 0");
                Mesh tileMesh = tile == null ? null : tile.GetComponent<MeshFilter>().sharedMesh;
                Material tileMaterial = tile == null ? null : tile.GetComponent<MeshRenderer>().sharedMaterial;
                PbMapTile tileIdentity = tile == null ? null : tile.GetComponent<PbMapTile>();
                MeshCollider tileCollider = tile == null ? null : tile.GetComponent<MeshCollider>();
                Texture2D tileTexture = voLoader.LoadTexture(
                    storage.String("toptextures", "Name", 0));
                if (tileMesh == null || tileMaterial == null ||
                    tileIdentity == null || tileIdentity.Row != 0 || tileCollider == null ||
                    tileMaterial.shader.name != "PB/Overlay" ||
                    tileTexture == null || tileTexture.width <= 0 ||
                    tileMaterial.mainTextureScale.y != 1f ||
                    Mathf.Abs(tileMesh.bounds.size.x -
                        tileTexture.width / 64f * CellSize) > 0.01f)
                    throw new InvalidOperationException("Overlay scale or alpha shader is invalid.");
                Ray tileRay = new Ray(tileMesh.bounds.center + Vector3.up * 1000f,
                                      Vector3.down);
                if (!tileCollider.Raycast(tileRay, out RaycastHit tileHit, 2000f))
                    throw new InvalidOperationException("Top tile cannot be picked.");
                SelectTile(tileIdentity, false);
                if (primaryTile != tileIdentity || selectedTiles.Count != 1 ||
                    !tile.GetComponent<LineRenderer>().enabled)
                    throw new InvalidOperationException("Top tile selection failed.");
                SelectTile(tileIdentity, true);
                if (selectedTiles.Count != 0)
                    throw new InvalidOperationException("Top tile deselection failed.");
                for (int row = 0; row < overlayTiles; row++)
                {
                    Transform top = overlayRoot.transform.Find("Top tile " + row);
                    Mesh mesh = top == null ? null : top.GetComponent<MeshFilter>().sharedMesh;
                    if (mesh == null) throw new InvalidOperationException("Top tile mesh is absent.");
                    int flags = storage.Int("toptextures", "Flags", row, 1);
                    Vector2 expected = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f),
                        new Vector2(1f, 1f), new Vector2(1f, 0f) }[flags & 3];
                    if ((flags & 1024) != 0) expected.x = 1f - expected.x;
                    if ((flags & 2048) != 0) expected.y = 1f - expected.y;
                    if (Vector2.Distance(mesh.uv[0], expected) > 0.001f)
                        throw new InvalidOperationException("Top tile rotation is invalid: " + row);
                }
                if (tileRows.Count != overlayTiles + overlaySurfaces)
                    throw new InvalidOperationException("Decal selection list is incomplete.");
                Transform surface = overlayRoot.transform.Find("Surface 0");
                Mesh surfaceMesh = surface == null ? null : surface.GetComponent<MeshFilter>().sharedMesh;
                PbMapTile surfaceIdentity = surface == null ? null : surface.GetComponent<PbMapTile>();
                MeshCollider surfaceCollider = surface == null ? null : surface.GetComponent<MeshCollider>();
                Material surfaceMaterial = surface == null ? null :
                    surface.GetComponent<MeshRenderer>().sharedMaterial;
                if (surfaceMesh == null || surfaceIdentity == null || surfaceCollider == null ||
                    surfaceMaterial == null || surfaceMaterial.mainTextureScale.y != 1f ||
                    surfaceIdentity.Table != "surfaces" || surfaceIdentity.Row != 0)
                    throw new InvalidOperationException("Surface selection target is absent.");
                int[] surfaceTriangles = surfaceMesh.triangles;
                Vector3[] surfaceVertices = surfaceMesh.vertices;
                Vector3 surfaceCenter = (surfaceVertices[surfaceTriangles[0]] +
                    surfaceVertices[surfaceTriangles[1]] +
                    surfaceVertices[surfaceTriangles[2]]) / 3f;
                Ray surfaceRay = new Ray(surfaceCenter + Vector3.up * 1000f, Vector3.down);
                if (!surfaceCollider.Raycast(surfaceRay, out RaycastHit surfaceHit, 2000f))
                    throw new InvalidOperationException("Surface cannot be picked.");
                SelectTile(surfaceIdentity, false);
                if (primaryTile != surfaceIdentity || selectedTiles.Count != 1 ||
                    !TileRecord(surfaceIdentity).Contains("surfaces row 0"))
                    throw new InvalidOperationException("Surface selection failed.");
                ClearTileSelection();
                int segments = Mathf.RoundToInt(Mathf.Sqrt(surfaceMesh.vertexCount)) - 1;
                float x0 = storage.Float("surfaces", "X0", 0);
                float x1 = storage.Float("surfaces", "X1", 0);
                float expectedX = width * CellSize - Mathf.Lerp(x0, x1, 1f / segments);
                if (Mathf.Abs(surfaceMesh.vertices[1].x - expectedX) > 0.01f)
                    throw new InvalidOperationException("Surface corner orientation is invalid.");
                float texK = storage.Float("surfaces", "TexK", 0);
                if (Vector2.Distance(surfaceMesh.uv[0], Vector2.zero) > 0.001f ||
                    Vector2.Distance(surfaceMesh.uv[1], new Vector2(texK / segments, 0f)) > 0.001f)
                    throw new InvalidOperationException("Surface texture corner order is invalid.");
            }
            if (smokeMode == "entities")
            {
                foreach (string table in new[] { "buildings", "cannons", "robots" })
                {
                    GameObject entity = entityRoots.Find(root =>
                        root.GetComponent<PbMapEntity>().Table == table);
                    if (entity == null) throw new InvalidOperationException("Missing entity " + table);
                    entity.transform.position += Vector3.right * CellSize;
                }
            }
            if (smokeMode == "mirror")
            {
                if (Mathf.Abs(terrainMesh.vertices[0].x - width * CellSize) > 0.001f)
                    throw new InvalidOperationException("Map X direction is not mirrored.");
                GameObject cannon = entityRoots.Find(root =>
                    root.GetComponent<PbMapEntity>().Table == "cannons");
                GameObject robot = entityRoots.Find(root =>
                    root.GetComponent<PbMapEntity>().Table == "robots");
                if (cannon == null || cannon.transform.childCount < 3 ||
                    robot == null || robot.transform.childCount < 3)
                    throw new InvalidOperationException("Composite entity preview is incomplete.");
                objects[0].transform.position += Vector3.right * CellSize;
            }
            if (smokeMode == "geometry")
            {
                Vector3 forwardAt90 = PbCoordinates.Rotation(Mathf.PI / 2f, 0f, 0f) *
                    PbCoordinates.Direction(0f, 1f, 0f);
                if (Vector3.Distance(forwardAt90,
                    PbCoordinates.Direction(-1f, 0f, 0f)) > 0.001f)
                    throw new InvalidOperationException("Game and editor axes disagree at 90 degrees.");
                PbPlacedObject item = objects.Find(o => o.GetComponentInChildren<BoxCollider>() != null);
                if (item == null) throw new InvalidOperationException("No object pick collider.");
                item.Rotate(15f, 10f, -10f);
                item.SetScale(item.Scale * 1.1f);
                Select(item);
                objectHText = Number(item.transform.position.y - 7f);
                ApplyObjectInspector();
                BoxCollider pick = item.GetComponentInChildren<BoxCollider>();
                Ray ray = new Ray(pick.bounds.center + Vector3.up *
                    (pick.bounds.extents.y + 10f), Vector3.down);
                if (!pick.Raycast(ray, out _, pick.bounds.size.y + 20f))
                    throw new InvalidOperationException("Rotated object cannot be picked.");
                PbVoLoader.Asset plant = voLoader.Load("Matrix/Obj/plant/Grass01M.vo");
                if (plant == null || Array.Find(plant.Materials,
                    material => material.renderQueue == 2450) == null)
                    throw new InvalidOperationException("Vegetation alpha cutout is absent.");
                if (mapRoot.transform.Find("Sea surface") == null)
                    throw new InvalidOperationException("Sea surface is absent.");
                Vector3[] normals = terrainVisualMesh.normals;
                if (Vector3.Distance(normals[2], normals[4]) > 0.001f)
                    throw new InvalidOperationException("Terrain cell normals are discontinuous.");
                GameObject baseRoot = entityRoots.Find(root =>
                    root.GetComponent<PbMapEntity>().Table == "buildings" &&
                    root.GetComponent<PbMapEntity>().Kind == 0);
                GameObject factoryRoot = entityRoots.Find(root =>
                    root.GetComponent<PbMapEntity>().Table == "buildings" &&
                    root.GetComponent<PbMapEntity>().Kind != 0);
                if (baseRoot != null && baseRoot.transform.childCount < 7)
                    throw new InvalidOperationException("Base elevator group is incomplete.");
                if (factoryRoot != null && factoryRoot.transform.childCount < 3)
                    throw new InvalidOperationException("Factory capture platform is absent.");
                if (baseRoot != null) baseRoot.transform.Rotate(0f, 90f, 0f);
                GameObject cannonRoot = entityRoots.Find(root =>
                    root.GetComponent<PbMapEntity>().Table == "cannons");
                if (cannonRoot != null) cannonRoot.transform.Rotate(0f, 15f, 0f);
            }
            if (smokeMode == "camera")
            {
                ToggleCamera();
                if (!perspective || viewCamera.orthographic)
                    throw new InvalidOperationException("Perspective camera did not activate.");
            }
            int terrainIndex = (height / 2) * (width + 1) + width / 2;
            float originalHeight = storage.Float("points", "Z", terrainIndex);
            if (smokeMode == "terrain")
                SculptTerrain(new Vector3(width / 2 * CellSize, 0,
                                          height / 2 * CellSize), 1f);
            SaveMap();
            if (!File.Exists(outputPath))
                throw new InvalidOperationException("Smoke test could not save the map: " + message);
            var check = new PbStorage(File.ReadAllBytes(outputPath));
            if (check.Count("objects", "Index", 0) != expectedCount ||
                check.PropertyInt("ObjectsCount") != expectedCount)
                throw new InvalidOperationException("Smoke test object count mismatch.");
            if (smokeMode == "terrain" &&
                Mathf.Abs(check.Float("points", "Z", terrainIndex) - originalHeight - 1f) > 0.001f)
                throw new InvalidOperationException("Smoke test terrain height mismatch.");
            if (smokeMode == "entities")
                foreach (string table in new[] { "buildings", "cannons", "robots" })
                    if (check.Int(table, "X", 0) != storage.Int(table, "X", 0) - 1)
                        throw new InvalidOperationException("Smoke test entity position mismatch: " + table);
            if (smokeMode == "mirror" &&
                check.Int("objects", "X", 0) != storage.Int("objects", "X", 0) - 1)
                throw new InvalidOperationException("Mirrored object position was saved incorrectly.");
            if (smokeMode == "geometry")
            {
                PbPlacedObject item = objects.Find(o => o.GetComponentInChildren<BoxCollider>() != null);
                Quaternion saved = PbPlacedObject.Rotation(
                    check.Float("objects", "Angle", item.Row),
                    check.Float("objects", "AngleX", item.Row),
                    check.Float("objects", "AngleY", item.Row));
                if (Quaternion.Angle(saved, item.transform.rotation) > 0.1f)
                    throw new InvalidOperationException("Three-axis rotation did not round-trip.");
                if (Mathf.Abs(check.Float("objects", "Scale", item.Row) - item.Scale) > 0.0001f)
                    throw new InvalidOperationException("Object scale did not round-trip.");
                if (Mathf.Abs(check.Float("objects", "Height", item.Row) -
                    storage.Float("objects", "Height", item.Row) - 1f) > 0.001f)
                    throw new InvalidOperationException("Numeric transform inspector did not save height.");
                GameObject baseRoot = entityRoots.Find(root =>
                    root.GetComponent<PbMapEntity>().Table == "buildings" &&
                    root.GetComponent<PbMapEntity>().Kind == 0);
                if (baseRoot != null)
                {
                    int row = baseRoot.GetComponent<PbMapEntity>().Row;
                    if (check.Int("buildings", "Angle", row) !=
                        (storage.Int("buildings", "Angle", row) + 1) % 4)
                        throw new InvalidOperationException("Base angle did not round-trip.");
                }
                GameObject cannonRoot = entityRoots.Find(root =>
                    root.GetComponent<PbMapEntity>().Table == "cannons");
                if (cannonRoot != null)
                {
                    int row = cannonRoot.GetComponent<PbMapEntity>().Row;
                    float expected = storage.Float("cannons", "Angle", row) +
                        15f * Mathf.Deg2Rad;
                    if (Mathf.Abs(Mathf.DeltaAngle(expected * Mathf.Rad2Deg,
                        check.Float("cannons", "Angle", row) * Mathf.Rad2Deg)) > 0.1f)
                        throw new InvalidOperationException("Cannon angle did not round-trip.");
                }
            }
            if (smokeMode == "players")
            {
                Dictionary<string, string> props = check.Properties();
                if (!props["SideResInfo"].Contains("1,2501,") ||
                    !props["SideAIInfo"].Contains("Red:TBB/60000/SK/1/DK/1/WRK/1/BK/0.75/TC/3") ||
                    props["MaintenanceTime"] != "125")
                    throw new InvalidOperationException("Player settings were not saved.");
            }
            Debug.Log("PB standalone smoke succeeded: " + outputPath);
            Application.Quit();
        }
    }

    private static Material MakeMaterial(Color color)
    {
        Shader shader = Shader.Find("Standard") ?? Shader.Find("Diffuse");
        var material = new Material(shader);
        material.color = color;
        return material;
    }

    private void LoadMap()
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(mapPath);
            var parsed = new PbStorage(bytes);
            int newWidth = parsed.PropertyInt("SizeInUnitsX");
            int newHeight = parsed.PropertyInt("SizeInUnitsY");
            if (newWidth < 1 || newHeight < 1 || (long)newWidth * newHeight > 1000000 ||
                parsed.Count("points", "Z", 3) != (newWidth + 1) * (newHeight + 1))
                throw new InvalidDataException("Invalid terrain dimensions.");
            int count = parsed.Count("objects", "Index", 0);
            foreach (string name in new[] { "X", "Y", "Index" })
                if (parsed.Count("objects", name, 0) != count)
                    throw new InvalidDataException("Object columns have different lengths.");
            foreach (string name in new[] { "XF", "YF", "Height", "Angle" })
                if (parsed.Count("objects", name, 3) != count)
                    throw new InvalidDataException("Object columns have different lengths.");

            ClearTileSelection();
            if (mapRoot != null) Destroy(mapRoot);
            objects.Clear();
            entityRoots.Clear();
            typeNames.Clear();
            typeData.Clear();
            editedTerrain.Clear();
            terrainBrush = false;
            ClearHighlight();
            selected = null;
            selectedEntity = null;
            storage = parsed;
            players = new PbPlayers(parsed);
            width = newWidth;
            height = newHeight;
            sourceHash = Hash(bytes);
            mapPath = Path.GetFullPath(mapPath);
            outputPath = Path.Combine(Path.GetDirectoryName(mapPath),
                                      Path.GetFileNameWithoutExtension(mapPath) + "_edited.map");
            mapRoot = new GameObject("PB Map");
            voLoader = new PbVoLoader(modelRoot);
            BuildTerrain();
            BuildOverlays();
            int typeCount = parsed.Count("objtypes", "Name", 5);
            if (parsed.Count("objtypes", "Data", 5) != typeCount)
                throw new InvalidDataException("Object type catalog columns differ.");
            for (int type = 0; type < typeCount; type++)
            {
                typeNames.Add(parsed.String("objtypes", "Name", type));
                typeData.Add(parsed.String("objtypes", "Data", type));
            }
            activeType = 0;
            for (int row = 0; row < count; row++)
            {
                int type = parsed.Int("objects", "Index", row);
                Vector3 position = new Vector3(
                    MapX(parsed.Int("objects", "X", row) + parsed.Float("objects", "XF", row)),
                    parsed.Float("objects", "Height", row) + 8f,
                    (parsed.Int("objects", "Y", row) + parsed.Float("objects", "YF", row)) * CellSize);
                CreateMarker(row, type, position, parsed.Float("objects", "Angle", row),
                    parsed.Float("objects", "AngleX", row),
                    parsed.Float("objects", "AngleY", row));
            }
            BuildEntityPreviews();
            viewCamera.transform.position = new Vector3(width * CellSize / 2f, 5000f,
                                                        height * CellSize / 2f);
            viewCamera.transform.rotation = Quaternion.Euler(90, 0, 0);
            viewCamera.orthographicSize = Mathf.Max(width, height) * CellSize * 0.55f;
            viewCamera.farClipPlane = 10000f;
            cameraTarget = new Vector3(width * CellSize / 2f, 0f, height * CellSize / 2f);
            cameraDistance = Mathf.Max(width, height) * CellSize * 0.9f;
            if (perspective) PositionPerspectiveCamera();
            message = "Loaded " + width + " x " + height + ", objects: " + count +
                      ", VO: " + voLoader.LoadedModels + " loaded / " +
                      voLoader.MissingModels + " missing / " + voLoader.FailedModels +
                      " failed; textures: " + voLoader.LoadedTextures +
                      ", terrain: " + terrainTextureCount + ", overlays: " +
                      overlayTiles + "/" + overlaySurfaces + "/" + overlayMissing +
                      " missing, entities: " + entityCount;
            Debug.Log("PB standalone " + message);
        }
        catch (Exception error)
        {
            message = "Load failed: " + error.Message;
            Debug.LogException(error);
        }
    }

    private PbPlacedObject CreateMarker(int row, int type, Vector3 position,
                                        float angle, float angleX = 0f, float angleY = 0f)
    {
        GameObject marker = new GameObject();
        marker.name = "Object " + row + " (type " + type + ")";
        marker.transform.SetParent(mapRoot.transform);
        marker.transform.position = position;
        marker.transform.rotation = PbPlacedObject.Rotation(angle, angleX, angleY);
        var item = marker.AddComponent<PbPlacedObject>();
        item.Row = row;
        item.TypeIndex = type;
        item.AngleZ = angle;
        item.AngleX = angleX;
        item.AngleY = angleY;
        item.Scale = row >= 0 ? storage.Float("objects", "Scale", row) : 1f;
        item.ImportedScale = item.Scale;
        item.ImportedPosition = position;
        item.ImportedRotation = marker.transform.rotation;
        objects.Add(item);
        AttachVisual(item);
        return item;
    }

    private void AttachVisual(PbPlacedObject item)
    {
        Transform previous = item.transform.Find("Visual");
        if (previous != null)
        {
            previous.gameObject.SetActive(false);
            Destroy(previous.gameObject);
        }
        PbVoLoader.Asset asset = item.TypeIndex >= 0 && item.TypeIndex < typeData.Count
            ? voLoader.Load(typeData[item.TypeIndex]) : null;
        GameObject visual;
        if (asset == null)
        {
            visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.transform.SetParent(item.transform, false);
            visual.transform.localScale = Vector3.one * 16f;
            visual.GetComponent<Renderer>().sharedMaterial = markerMaterial;
        }
        else
        {
            visual = new GameObject("Visual");
            visual.transform.SetParent(item.transform, false);
            visual.transform.localPosition = new Vector3(0f, -8f, 0f);
            float scale = item.Scale;
            if (!float.IsNaN(scale) && !float.IsInfinity(scale) && scale > 0f)
                visual.transform.localScale = Vector3.one * scale;
            visual.AddComponent<MeshFilter>().sharedMesh = asset.Mesh;
            visual.AddComponent<MeshRenderer>().sharedMaterials = asset.Materials;
            AddPickColliders(visual, asset);
        }
        visual.name = "Visual";
    }

    private void ReloadModels()
    {
        if (storage == null) return;
        Select(null);
        voLoader = new PbVoLoader(modelRoot);
        Transform terrain = terrainCollider.transform;
        Transform oldTerrain = terrain.Find("Textured terrain");
        if (oldTerrain != null)
        {
            oldTerrain.gameObject.SetActive(false);
            Destroy(oldTerrain.gameObject);
        }
        BuildTexturedTerrain(terrainMesh.vertices, terrain);
        BuildOverlays();
        foreach (PbPlacedObject item in objects) AttachVisual(item);
        foreach (GameObject root in entityRoots)
        {
            foreach (Transform child in root.transform)
            {
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }
            PbMapEntity entity = root.GetComponent<PbMapEntity>();
            AttachEntityParts(root, entity);
        }
        message = "VO: " + voLoader.LoadedModels + " loaded / " +
                  voLoader.MissingModels + " missing / " + voLoader.FailedModels +
                  " failed; textures: " + voLoader.LoadedTextures +
                  ", terrain: " + terrainTextureCount;
    }

    private void BuildEntityPreviews()
    {
        foreach (GameObject old in entityRoots)
            if (old != null) { old.SetActive(false); Destroy(old); }
        entityRoots.Clear();
        Select(null);
        entityCount = 0;
        int buildings = storage.Count("buildings", "X", 0);
        for (int row = 0; row < buildings; row++)
        {
            int kind = storage.Int("buildings", "Kind", row);
            int side = storage.Int("buildings", "Side", row);
            float x = storage.Int("buildings", "X", row);
            float y = storage.Int("buildings", "Y", row);
            float angle = storage.Int("buildings", "Angle", row) * 90f;
            GameObject root = CreateEntity("buildings", row, kind, side, x, y, angle, 0f);
            AttachEntityParts(root, root.GetComponent<PbMapEntity>());
        }
        int cannons = storage.Count("cannons", "X", 0);
        for (int row = 0; row < cannons; row++)
        {
            int kind = storage.Int("cannons", "Kind", row);
            int side = storage.Int("cannons", "Side", row);
            float x = storage.Int("cannons", "X", row) + storage.Float("cannons", "XF", row);
            float y = storage.Int("cannons", "Y", row) + storage.Float("cannons", "YF", row);
            float angle = storage.Float("cannons", "Angle", row) * Mathf.Rad2Deg;
            float addHeight = storage.Float("cannons", "AddH", row);
            GameObject root = CreateEntity("cannons", row, kind, side, x, y, angle, addHeight);
            AttachEntityParts(root, root.GetComponent<PbMapEntity>());
        }
        int robots = storage.Count("robots", "X", 0);
        for (int row = 0; row < robots; row++)
        {
            int side = storage.Int("robots", "Side", row);
            float x = storage.Int("robots", "X", row) + storage.Float("robots", "XF", row);
            float y = storage.Int("robots", "Y", row) + storage.Float("robots", "YF", row);
            string units = storage.String("robots", "Units", row);
            int chassis = ChassisKind(units);
            float angle = ChassisAngle(units) * Mathf.Rad2Deg;
            GameObject root = CreateEntity("robots", row, chassis, side, x, y, angle, 0f);
            AttachEntityParts(root, root.GetComponent<PbMapEntity>());
        }
    }

    private GameObject CreateEntity(string table, int row, int kind, int side,
                                    float cellX, float cellY, float angle, float extraHeight)
    {
        var root = new GameObject(table + " " + row + " kind " + kind + " side " + side);
        root.transform.SetParent(mapRoot.transform);
        root.transform.position = new Vector3(MapX(cellX),
            SampleTerrainHeight(cellX, cellY) + extraHeight, cellY * CellSize);
        root.transform.rotation = Quaternion.Euler(0, angle, 0);
        var info = root.AddComponent<PbMapEntity>();
        info.Table = table;
        info.Row = row;
        info.Kind = kind;
        info.Side = side;
        info.ImportedPosition = root.transform.position;
        info.ImportedRotation = root.transform.rotation;
        info.ExtraHeight = extraHeight;
        info.ImportedExtraHeight = extraHeight;
        entityRoots.Add(root);
        entityCount++;
        return root;
    }

    private void AttachEntityParts(GameObject root, PbMapEntity entity)
    {
        if (entity.Table == "buildings")
        {
            AttachBuildingGroup(root, entity.Kind);
            if (entity.Kind != 0) AttachCaptureRing(root, entity.Side);
            return;
        }
        if (entity.Table == "cannons")
        {
            Vector3 position = Vector3.zero;
            string descriptor = "Matrix/Cannon/Basis.vo";
            for (int part = 0; part < 4; part++)
            {
                AttachEntityModel(root, descriptor, position);
                PbVoLoader.Asset asset = voLoader.Load(descriptor);
                if (asset == null || !asset.TryAttachment(20, out Matrix4x4 socket)) break;
                string name = asset.AttachmentName(20);
                if (string.IsNullOrEmpty(name) || (name[0] != 'T' && name[0] != 'S')) break;
                position += (Vector3)socket.GetColumn(3);
                descriptor = "Matrix/Cannon/" + name + entity.Kind + ".vo";
            }
            return;
        }
        if (entity.Table == "robots") AttachRobotParts(root, entity);
    }

    private void AttachBuildingGroup(GameObject root, int kind)
    {
        string directory = Path.Combine(modelRoot, "Matrix", "Building");
        string path = Path.Combine(directory, "b" + kind + ".cvo");
        if (!File.Exists(path))
        {
            AttachEntityModel(root, "Matrix/Building/b" + kind + ".vo", Vector3.zero);
            return;
        }
        string model = null, texture = null;
        int parts = 0;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Split(new[] { "//" }, StringSplitOptions.None)[0].Trim();
            if (line.Contains("{")) { model = null; texture = null; }
            if (line.StartsWith("Model=", StringComparison.OrdinalIgnoreCase))
                model = line.Substring(6).Trim().Split('?')[0];
            if (line.StartsWith("Texture=", StringComparison.OrdinalIgnoreCase))
                texture = line.Substring(8).Trim();
            if (!line.Contains("}") || string.IsNullOrEmpty(model)) continue;
            string descriptor = "Matrix/Building/" + model;
            if (!string.IsNullOrEmpty(texture)) descriptor += "?Tex=" + texture;
            AttachEntityModel(root, descriptor, Vector3.zero);
            parts++;
        }
        if (parts == 0)
            AttachEntityModel(root, "Matrix/Building/b" + kind + ".vo", Vector3.zero);
    }

    private void AttachRobotParts(GameObject root, PbMapEntity entity)
    {
        string units = storage.String("robots", "Units", entity.Row);
        string chassis = "Matrix/Robot/Chassis" + entity.Kind + ".vo";
        AttachEntityModel(root, chassis, Vector3.zero);
        Vector3 position = Vector3.zero;
        PbVoLoader.Asset chassisAsset = voLoader.Load(chassis);
        if (chassisAsset != null && chassisAsset.TryAttachment(1, out Matrix4x4 chassisSocket))
            position = (Vector3)chassisSocket.GetColumn(3);
        string armor = null;
        var usedSockets = new HashSet<int>();
        foreach (string part in units.Split('|'))
        {
            string[] fields = part.Split(',');
            if (fields.Length < 2 || !int.TryParse(fields[0].Trim(), out int type) ||
                !int.TryParse(fields[1].Trim(), out int kind) || kind <= 0) continue;
            if (type == 3)
            {
                armor = "Matrix/Robot/Armor" + kind + ".vo";
                AttachEntityModel(root, armor, position);
            }
            else if (type == 4)
            {
                Vector3 headPosition = position;
                PbVoLoader.Asset armorAsset = armor == null ? null : voLoader.Load(armor);
                if (armorAsset != null && armorAsset.TryAttachment(1, out Matrix4x4 headSocket))
                    headPosition += (Vector3)headSocket.GetColumn(3);
                AttachEntityModel(root, "Matrix/Robot/Head" + kind + ".vo", headPosition);
            }
            else if (type == 2)
            {
                Vector3 weaponPosition = position;
                Quaternion weaponRotation = Quaternion.identity;
                PbVoLoader.Asset armorAsset = armor == null ? null : voLoader.Load(armor);
                if (armorAsset != null)
                {
                    var ids = new List<int>(armorAsset.AttachmentNames.Keys);
                    ids.Sort();
                    foreach (int id in ids)
                    {
                        if (id < 20 || usedSockets.Contains(id)) continue;
                        string name = armorAsset.AttachmentName(id);
                        string[] allowed = name.Split(',');
                        if (allowed.Length < 2 || allowed[0].Trim() != "W") continue;
                        bool accepts = false;
                        foreach (string token in allowed)
                            if (int.TryParse(token.Trim(), out int permitted) && permitted == kind)
                                accepts = true;
                        if (!accepts) continue;
                        if (armorAsset.TryAttachment(id, out Matrix4x4 socket))
                        {
                            weaponPosition += (Vector3)socket.GetColumn(3);
                            weaponRotation = socket.rotation;
                            usedSockets.Add(id);
                            break;
                        }
                    }
                }
                GameObject weapon = AttachEntityModel(root,
                    "Matrix/Robot/Weapon" + kind + ".vo", weaponPosition);
                if (weapon != null) weapon.transform.localRotation = weaponRotation;
            }
        }
    }

    private GameObject AttachEntityModel(GameObject root, string descriptor, Vector3 position)
    {
        PbVoLoader.Asset asset = voLoader.Load(descriptor);
        if (asset == null)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Missing " + descriptor;
            cube.transform.SetParent(root.transform, false);
            cube.transform.localPosition = position + Vector3.up * 10f;
            cube.transform.localScale = Vector3.one * 20f;
            cube.GetComponent<Renderer>().sharedMaterial = markerMaterial;
            return cube;
        }
        var visual = new GameObject(Path.GetFileName(descriptor));
        visual.transform.SetParent(root.transform, false);
        visual.transform.localPosition = position;
        visual.AddComponent<MeshFilter>().sharedMesh = asset.Mesh;
        visual.AddComponent<MeshRenderer>().sharedMaterials = asset.Materials;
        AddPickColliders(visual, asset);
        return visual;
    }

    private static void AddPickColliders(GameObject visual, PbVoLoader.Asset asset)
    {
        foreach (Bounds bounds in asset.PickBounds)
        {
            var collider = visual.AddComponent<BoxCollider>();
            collider.center = bounds.center;
            Vector3 size = bounds.size;
            collider.size = new Vector3(Mathf.Max(size.x, 2f),
                                        Mathf.Max(size.y, 2f), Mathf.Max(size.z, 2f));
        }
    }

    private float SampleTerrainHeight(float cellX, float cellY)
    {
        float x = Mathf.Clamp(cellX, 0f, width);
        float y = Mathf.Clamp(cellY, 0f, height);
        int x0 = Mathf.Min(Mathf.FloorToInt(x), width - 1);
        int y0 = Mathf.Min(Mathf.FloorToInt(y), height - 1);
        float dx = x - x0;
        float dy = y - y0;
        Vector3[] grid = terrainMesh.vertices;
        int at = y0 * (width + 1) + x0;
        return Mathf.Lerp(Mathf.Lerp(grid[at].y, grid[at + 1].y, dx),
                          Mathf.Lerp(grid[at + width + 1].y,
                                     grid[at + width + 2].y, dx), dy);
    }

    private void BuildOverlays()
    {
        ClearTileSelection();
        tileRows.Clear();
        if (overlayRoot != null)
        {
            overlayRoot.SetActive(false);
            Destroy(overlayRoot);
        }
        overlayRoot = new GameObject("Map overlays");
        overlayRoot.transform.SetParent(mapRoot.transform, false);
        var overlays = new PbMapOverlays(storage, voLoader, terrainMesh,
                                         overlayRoot.transform, width, height, CellSize);
        overlays.Build();
        overlayTiles = overlays.Tiles;
        overlaySurfaces = overlays.Surfaces;
        overlayMissing = overlays.Missing;
        tileRows.AddRange(overlayRoot.GetComponentsInChildren<PbMapTile>());
        tileRows.Sort((a, b) => a.Row.CompareTo(b.Row));
    }

    private void AttachCaptureRing(GameObject building, int side)
    {
        Texture2D texture = voLoader.LoadTexture("Matrix/Textures/Billboard/zahspot");
        if (texture == null) return;
        Color color = side == 1 ? Color.yellow : side == 2 ? Color.red
            : side == 3 ? Color.blue : side == 4 ? Color.green
            : new Color(0.6f, 0.6f, 0.6f);
        Material material = PbMapOverlays.TransparentMaterial(texture, color);
        const int spots = 14;
        const float radius = 24f;
        const float half = 3f;
        var vertices = new Vector3[spots * 4];
        var uv = new Vector2[spots * 4];
        var triangles = new int[spots * 6];
        for (int i = 0; i < spots; i++)
        {
            float angle = Mathf.PI * 2f * i / spots;
            float x = Mathf.Cos(angle) * radius;
            float z = Mathf.Sin(angle) * radius;
            int v = i * 4, t = i * 6;
            vertices[v] = new Vector3(x - half, 0.9f, z - half);
            vertices[v + 1] = new Vector3(x + half, 0.9f, z - half);
            vertices[v + 2] = new Vector3(x - half, 0.9f, z + half);
            vertices[v + 3] = new Vector3(x + half, 0.9f, z + half);
            uv[v] = new Vector2(0, 0); uv[v + 1] = new Vector2(1, 0);
            uv[v + 2] = new Vector2(0, 1); uv[v + 3] = new Vector2(1, 1);
            triangles[t] = v; triangles[t + 1] = v + 2; triangles[t + 2] = v + 1;
            triangles[t + 3] = v + 1; triangles[t + 4] = v + 2;
            triangles[t + 5] = v + 3;
        }
        var mesh = new Mesh { name = "Factory ownership ring" };
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        var ring = new GameObject("Factory ownership ring");
        ring.transform.SetParent(building.transform, false);
        ring.transform.localPosition = new Vector3(0f, 0.8f, 2.7f);
        ring.AddComponent<MeshFilter>().sharedMesh = mesh;
        ring.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    private static int ChassisKind(string units)
    {
        foreach (string part in units.Split('|'))
        {
            string[] fields = part.Split(',');
            if (fields.Length >= 2 && fields[0].Trim() == "1" &&
                int.TryParse(fields[1], out int kind)) return kind;
        }
        return 1;
    }

    private static float ChassisAngle(string units)
    {
        foreach (string part in units.Split('|'))
        {
            string[] fields = part.Split(',');
            if (fields.Length >= 3 && fields[0].Trim() == "1" &&
                float.TryParse(fields[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float angle))
                return angle;
        }
        return 0f;
    }

    private void ChangeType(PbPlacedObject item, int type)
    {
        if (item == null || item.TypeIndex == type) return;
        Select(null);
        item.TypeIndex = type;
        AttachVisual(item);
        Select(item);
    }

    private void AddObject()
    {
        if (storage == null || activeType < 0 || activeType >= typeNames.Count) return;
        Vector3 center = perspective ? cameraTarget : viewCamera.transform.position;
        center.x = Mathf.Clamp(center.x, 0, width * CellSize);
        center.z = Mathf.Clamp(center.z, 0, height * CellSize);
        center.y = 8f;
        PbPlacedObject item = CreateMarker(-1, activeType, center, 0f);
        Select(item);
        message = "Added object type " + activeType + "; save as a new .map.";
    }

    private float MapX(float cellX) => PbCoordinates.MapX(cellX, width, CellSize);
    private float CellX(float worldX) => PbCoordinates.CellX(worldX, width, CellSize);

    private void DeleteSelected()
    {
        if (selected == null) return;
        PbPlacedObject item = selected;
        Select(null);
        objects.Remove(item);
        Destroy(item.gameObject);
        message = "Deleted object; save as a new .map.";
    }

    private void BuildTerrain()
    {
        int columns = width + 1;
        var vertices = new Vector3[columns * (height + 1)];
        for (int y = 0; y <= height; y++)
            for (int x = 0; x <= width; x++)
                vertices[y * columns + x] = new Vector3(MapX(x),
                    storage.Float("points", "Z", y * columns + x), y * CellSize);
        var triangles = new int[width * height * 6];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int vertex = y * columns + x;
                int index = (y * width + x) * 6;
                triangles[index] = vertex;
                triangles[index + 1] = vertex + 1;
                triangles[index + 2] = vertex + columns;
                triangles[index + 3] = vertex + 1;
                triangles[index + 4] = vertex + columns + 1;
                triangles[index + 5] = vertex + columns;
            }
        terrainMesh = new Mesh { indexFormat = IndexFormat.UInt32, vertices = vertices, triangles = triangles };
        terrainMesh.RecalculateNormals();
        var terrain = new GameObject("Terrain");
        terrain.transform.SetParent(mapRoot.transform);
        terrain.AddComponent<MeshFilter>().sharedMesh = terrainMesh;
        terrainCollider = terrain.AddComponent<MeshCollider>();
        terrainCollider.sharedMesh = terrainMesh;
        BuildTexturedTerrain(vertices, terrain.transform);
        BuildWater(vertices);
    }

    private void BuildWater(Vector3[] ground)
    {
        bool submerged = false;
        foreach (Vector3 point in ground)
            if (point.y < WaterLevel) { submerged = true; break; }
        if (!submerged) return;
        var water = new GameObject("Sea surface");
        water.transform.SetParent(mapRoot.transform, false);
        var mesh = new Mesh { name = "Sea at game water level" };
        mesh.vertices = new[] {
            new Vector3(0, WaterLevel, 0),
            new Vector3(width * CellSize, WaterLevel, 0),
            new Vector3(0, WaterLevel, height * CellSize),
            new Vector3(width * CellSize, WaterLevel, height * CellSize) };
        mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
        mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
        mesh.RecalculateNormals();
        water.AddComponent<MeshFilter>().sharedMesh = mesh;
        Shader shader = Resources.Load<Shader>("PBOverlay");
        Material material = shader == null ? MakeMaterial(new Color(0.16f, 0.33f, 0.48f, 0.78f))
            : new Material(shader);
        material.color = new Color(0.18f, 0.37f, 0.52f, 0.78f);
        water.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    private void BuildTexturedTerrain(Vector3[] grid, Transform parent)
    {
        int paletteCount = storage.Count("textures", "Name", 5);
        if (storage.Count("units", "Unit", 1) != width * height)
            throw new InvalidDataException("Terrain cell count does not match map size.");
        string catalogPath = Path.GetFullPath(Path.Combine(Application.dataPath,
                                                           "../Catalog/data.txt"));
        PbTerrainCatalog catalog = PbTerrainCatalog.Read(catalogPath);
        var materials = new Material[paletteCount + 1];
        Material baseMaterial = Resources.Load<Material>("PBModel");
        materials[0] = baseMaterial == null ? MakeMaterial(new Color(0.2f, 0.35f, 0.45f))
            : new Material(baseMaterial) { color = new Color(0.2f, 0.35f, 0.45f) };
        terrainTextureCount = 0;
        for (int i = 0; i < paletteCount; i++)
        {
            Texture2D texture = catalog.LoadTexture(storage.String("textures", "Name", i), modelRoot);
            Material material = baseMaterial == null
                ? MakeMaterial(Color.white) : new Material(baseMaterial);
            material.mainTexture = texture;
            material.color = texture == null ? new Color(0.36f, 0.49f, 0.34f) : Color.white;
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0f);
            materials[i + 1] = material;
            if (texture != null) terrainTextureCount++;
        }
        var vertices = new Vector3[width * height * 4];
        var uv = new Vector2[vertices.Length];
        var normals = new Vector3[vertices.Length];
        Vector3[] smoothNormals = terrainMesh.normals;
        var triangles = new List<int>[materials.Length];
        for (int i = 0; i < triangles.Length; i++) triangles[i] = new List<int>();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int gridIndex = y * (width + 1) + x;
                int cell = y * width + x;
                int at = cell * 4;
                vertices[at] = grid[gridIndex];
                vertices[at + 1] = grid[gridIndex + width + 1];
                vertices[at + 2] = grid[gridIndex + 1];
                vertices[at + 3] = grid[gridIndex + width + 2];
                normals[at] = smoothNormals[gridIndex];
                normals[at + 1] = smoothNormals[gridIndex + width + 1];
                normals[at + 2] = smoothNormals[gridIndex + 1];
                normals[at + 3] = smoothNormals[gridIndex + width + 2];
                uv[at] = new Vector2(0, 0);
                uv[at + 1] = new Vector2(0, 1);
                uv[at + 2] = new Vector2(1, 0);
                uv[at + 3] = new Vector2(1, 1);
                int unit = storage.Int("units", "Unit", cell, 1);
                int palette = ((unit >> 10) - 1) / 2;
                int material = unit == 0 || palette < 0 || palette >= paletteCount
                    ? 0 : palette + 1;
                triangles[material].Add(at);
                triangles[material].Add(at + 2);
                triangles[material].Add(at + 1);
                triangles[material].Add(at + 2);
                triangles[material].Add(at + 3);
                triangles[material].Add(at + 1);
            }
        terrainVisualMesh = new Mesh { name = "PB textured terrain" };
        terrainVisualMesh.indexFormat = IndexFormat.UInt32;
        terrainVisualMesh.vertices = vertices;
        terrainVisualMesh.normals = normals;
        terrainVisualMesh.uv = uv;
        terrainVisualMesh.subMeshCount = triangles.Length;
        for (int i = 0; i < triangles.Length; i++)
            terrainVisualMesh.SetTriangles(triangles[i], i);
        var visual = new GameObject("Textured terrain");
        visual.transform.SetParent(parent, false);
        visual.AddComponent<MeshFilter>().sharedMesh = terrainVisualMesh;
        visual.AddComponent<MeshRenderer>().sharedMaterials = materials;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Application.Quit();
            return;
        }
        if (storage == null) return;
        if (Input.GetKeyDown(KeyCode.C) && GUIUtility.keyboardControl == 0)
            ToggleCamera();
        bool viewport = Input.mousePosition.y < Screen.height - HeaderHeight &&
                        Input.mousePosition.y > StatusHeight &&
                        Input.mousePosition.x < Screen.width - ToolsWidth;
        float pan = (perspective ? cameraDistance : viewCamera.orthographicSize) *
                    0.8f * Time.deltaTime;
        if (Input.GetKey(KeyCode.LeftShift)) pan *= 3f;
        float horizontal = 0f;
        float forward = 0f;
        if (GUIUtility.keyboardControl == 0)
        {
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) horizontal -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) horizontal += 1f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) forward += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) forward -= 1f;
        }
        if (perspective)
        {
            if (Input.GetMouseButton(1) && viewport)
            {
                cameraYaw += Input.GetAxis("Mouse X") * 4f;
                cameraPitch = Mathf.Clamp(cameraPitch - Input.GetAxis("Mouse Y") * 4f,
                                          10f, 85f);
            }
            Quaternion heading = Quaternion.Euler(0, cameraYaw, 0);
            cameraTarget += (heading * Vector3.right * horizontal +
                             heading * Vector3.forward * forward) * pan;
            if (Input.GetKey(KeyCode.Q)) cameraTarget.y -= pan;
            if (Input.GetKey(KeyCode.E)) cameraTarget.y += pan;
        }
        else
            viewCamera.transform.position += new Vector3(horizontal * pan, 0, forward * pan);
        float scroll = Input.mouseScrollDelta.y;
        if (scroll != 0 && viewport)
        {
            if (perspective)
                cameraDistance = Mathf.Clamp(cameraDistance * Mathf.Pow(0.85f, scroll),
                                             10f, 20000f);
            else
                viewCamera.orthographicSize = Mathf.Clamp(viewCamera.orthographicSize *
                    Mathf.Pow(0.85f, scroll), 20f, 20000f);
        }
        if (perspective) PositionPerspectiveCamera();
        if (Input.GetMouseButtonDown(0) && viewport)
        {
            GUIUtility.keyboardControl = 0;
        }
        if (HandleGizmoInput(viewport)) return;
        if (Input.GetMouseButtonDown(0) && viewport)
        {
            Ray ray = viewCamera.ScreenPointToRay(Input.mousePosition);
            if (panelTab == 4)
            {
                RaycastHit[] hits = Physics.RaycastAll(ray, 10000f);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                var hovered = new List<PbMapTile>();
                foreach (RaycastHit tileHit in hits)
                {
                    PbMapTile tile = tileHit.collider.GetComponent<PbMapTile>();
                    if (tile != null && !hovered.Contains(tile)) hovered.Add(tile);
                }
                bool additive = Input.GetKey(KeyCode.LeftShift) ||
                                Input.GetKey(KeyCode.RightShift);
                PbMapTile chosen = hovered.Count == 0 ? null : hovered[0];
                if (additive)
                {
                    foreach (PbMapTile tile in hovered)
                        if (!selectedTiles.Contains(tile)) { chosen = tile; break; }
                }
                else if (hovered.Count > 1 && primaryTile != null &&
                         Vector3.Distance(lastDecalClickPosition, Input.mousePosition) < 6f)
                {
                    int previous = hovered.IndexOf(primaryTile);
                    if (previous >= 0) chosen = hovered[(previous + 1) % hovered.Count];
                }
                lastDecalClickPosition = Input.mousePosition;
                SelectTile(chosen, additive);
            }
            else if (terrainBrush && terrainCollider.Raycast(ray, out RaycastHit terrainHit, 10000f))
                SculptTerrain(terrainHit.point, Input.GetKey(KeyCode.LeftShift) ? -1f : 1f);
            else if (!terrainBrush && Physics.Raycast(ray, out RaycastHit hit, 10000f))
            {
                PbPlacedObject objectHit = hit.collider.GetComponentInParent<PbPlacedObject>();
                PbMapEntity entityHit = hit.collider.GetComponentInParent<PbMapEntity>();
                if (panelTab == 0 && objectHit != null) Select(objectHit);
                else if (panelTab == 2 && entityHit != null) SelectEntity(entityHit);
                else if (panelTab != 3) Select(null);
            }
        }
        if (terrainBrush) return;
        if (selected == null && selectedEntity == null) return;
        if (GUIUtility.keyboardControl != 0) return;
        Vector3 move = Vector3.zero;
        if (transformTool == 1)
        {
            if (Input.GetKeyDown(KeyCode.J)) move.x -= MoveStep;
            if (Input.GetKeyDown(KeyCode.L)) move.x += MoveStep;
            if (Input.GetKeyDown(KeyCode.I)) move.z += MoveStep;
            if (Input.GetKeyDown(KeyCode.K)) move.z -= MoveStep;
        }
        if (selected != null)
        {
            Vector3 oldPosition = selected.transform.position;
            Quaternion oldRotation = selected.transform.rotation;
            float oldScale = selected.Scale;
            if (transformTool == 1)
            {
                if (Input.GetKeyDown(KeyCode.PageUp)) move.y += 1f;
                if (Input.GetKeyDown(KeyCode.PageDown)) move.y -= 1f;
            }
            selected.transform.position += move;
            if (transformTool == 2)
            {
                if (Input.GetKeyDown(KeyCode.Comma)) selected.Rotate(-15f, 0f, 0f);
                if (Input.GetKeyDown(KeyCode.Period)) selected.Rotate(15f, 0f, 0f);
                if (Input.GetKeyDown(KeyCode.U)) selected.Rotate(0f, 15f, 0f);
                if (Input.GetKeyDown(KeyCode.O)) selected.Rotate(0f, -15f, 0f);
                if (Input.GetKeyDown(KeyCode.N)) selected.Rotate(0f, 0f, -15f);
                if (Input.GetKeyDown(KeyCode.M)) selected.Rotate(0f, 0f, 15f);
            }
            if (transformTool == 3)
            {
                if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus))
                    selected.SetScale(selected.Scale * 1.1f);
                if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))
                    selected.SetScale(selected.Scale / 1.1f);
            }
            if (oldPosition != selected.transform.position ||
                Quaternion.Angle(oldRotation, selected.transform.rotation) > 0.001f ||
                Mathf.Abs(oldScale - selected.Scale) > 0.0001f)
                SyncObjectInspector(selected);
        }
        else
        {
            selectedEntity.transform.position += move;
            if (transformTool == 1 && selectedEntity.Table == "cannons")
            {
                if (Input.GetKeyDown(KeyCode.PageUp)) selectedEntity.ExtraHeight += 1f;
                if (Input.GetKeyDown(KeyCode.PageDown)) selectedEntity.ExtraHeight -= 1f;
            }
            float x = CellX(selectedEntity.transform.position.x);
            float y = selectedEntity.transform.position.z / CellSize;
            Vector3 p = selectedEntity.transform.position;
            p.y = SampleTerrainHeight(x, y) + selectedEntity.ExtraHeight;
            selectedEntity.transform.position = p;
            float turn = selectedEntity.Table == "buildings" ? 90f : 15f;
            if (transformTool == 2 && selectedEntity.Table != "robots")
            {
                if (Input.GetKeyDown(KeyCode.Comma)) selectedEntity.transform.Rotate(0, -turn, 0);
                if (Input.GetKeyDown(KeyCode.Period)) selectedEntity.transform.Rotate(0, turn, 0);
            }
        }
    }

    private Vector3 GizmoCenter => selected != null ? selected.transform.position :
        selectedEntity != null ? selectedEntity.transform.position : Vector3.zero;

    private Vector2 GizmoScreen(Vector3 position)
    {
        Vector3 screen = viewCamera.WorldToScreenPoint(position);
        return new Vector2(screen.x, Screen.height - screen.y);
    }

    private float GizmoLength(Vector3 center) => Mathf.Clamp(
        (perspective ? Vector3.Distance(viewCamera.transform.position, center) :
            viewCamera.orthographicSize) * 0.16f, 8f, 400f);

    private Vector2 GizmoEndpoint(int axis)
    {
        Vector3 center = GizmoCenter;
        Vector2 origin = GizmoScreen(center);
        if (transformTool == 3) return origin + new Vector2(48f, -48f);
        Vector3 direction = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
        Vector2 endpoint = GizmoScreen(center + direction * GizmoLength(center));
        if (Vector2.Distance(origin, endpoint) < 22f)
            endpoint = origin + (axis == 0 ? new Vector2(58f, 0f) :
                axis == 1 ? new Vector2(-42f, -42f) : new Vector2(-42f, 42f));
        return endpoint;
    }

    private bool GizmoAxisAllowed(int axis)
    {
        if (selected != null) return true;
        if (selectedEntity == null || transformTool == 3) return false;
        if (transformTool == 1) return axis != 1 || selectedEntity.Table == "cannons";
        return axis == 1 && selectedEntity.Table != "robots";
    }

    private bool HandleGizmoInput(bool viewport)
    {
        if (gizmoAxis >= 0 && Input.GetMouseButtonUp(0))
        {
            gizmoAxis = -1;
            gizmoTurnAccumulated = 0f;
            return true;
        }
        if (gizmoAxis >= 0 && Input.GetMouseButton(0))
        {
            Vector2 mouse = new Vector2(Input.mousePosition.x,
                                        Screen.height - Input.mousePosition.y);
            Vector2 delta = mouse - gizmoLastMouse;
            gizmoLastMouse = mouse;
            if (transformTool == 3 && selected != null)
                selected.SetScale(selected.Scale * Mathf.Exp(-delta.y * 0.012f));
            else
            {
                Vector2 screenAxis = GizmoEndpoint(gizmoAxis) - GizmoScreen(GizmoCenter);
                float pixels = Vector2.Dot(delta, screenAxis.normalized);
                if (Input.GetKey(KeyCode.LeftShift)) pixels *= 0.2f;
                if (transformTool == 1)
                {
                    float amount = pixels * GizmoLength(GizmoCenter) /
                        Mathf.Max(22f, screenAxis.magnitude);
                    Vector3 axis = gizmoAxis == 0 ? Vector3.right :
                        gizmoAxis == 1 ? Vector3.up : Vector3.forward;
                    if (selected != null) selected.transform.position += axis * amount;
                    else if (selectedEntity != null)
                    {
                        selectedEntity.transform.position += axis * amount;
                        if (gizmoAxis == 1) selectedEntity.ExtraHeight += amount;
                        else
                        {
                            Vector3 p = selectedEntity.transform.position;
                            p.y = SampleTerrainHeight(CellX(p.x), p.z / CellSize) +
                                  selectedEntity.ExtraHeight;
                            selectedEntity.transform.position = p;
                        }
                    }
                }
                else if (transformTool == 2)
                {
                    float degrees = pixels * 0.55f;
                    if (selected != null)
                    {
                        if (gizmoAxis == 0) selected.Rotate(0f, -degrees, 0f);
                        else if (gizmoAxis == 1) selected.Rotate(degrees, 0f, 0f);
                        else selected.Rotate(0f, 0f, degrees);
                    }
                    else if (selectedEntity != null)
                    {
                        if (selectedEntity.Table == "buildings")
                        {
                            gizmoTurnAccumulated += degrees;
                            if (Mathf.Abs(gizmoTurnAccumulated) >= 45f)
                            {
                                float turn = Mathf.Sign(gizmoTurnAccumulated) * 90f;
                                selectedEntity.transform.Rotate(0f, turn, 0f);
                                gizmoTurnAccumulated -= turn;
                            }
                        }
                        else selectedEntity.transform.Rotate(0f, degrees, 0f);
                    }
                }
            }
            if (selected != null) SyncObjectInspector(selected);
            return true;
        }
        if (!viewport || transformTool == 0 ||
            (selected == null && selectedEntity == null) || !Input.GetMouseButtonDown(0))
            return false;
        Vector2 pointer = new Vector2(Input.mousePosition.x,
                                      Screen.height - Input.mousePosition.y);
        for (int axis = 0; axis < (transformTool == 3 ? 1 : 3); axis++)
            if (GizmoAxisAllowed(axis) &&
                Vector2.Distance(pointer, GizmoEndpoint(axis)) < 16f)
            {
                gizmoAxis = axis;
                gizmoLastMouse = pointer;
                gizmoTurnAccumulated = 0f;
                return true;
            }
        return false;
    }

    private void ToggleCamera()
    {
        perspective = !perspective;
        if (perspective)
        {
            cameraTarget = new Vector3(viewCamera.transform.position.x, 0f,
                                       viewCamera.transform.position.z);
            cameraDistance = Mathf.Max(width, height) * CellSize * 0.9f;
            PositionPerspectiveCamera();
        }
        else
        {
            viewCamera.orthographic = true;
            viewCamera.transform.position = cameraTarget + Vector3.up * 5000f;
            viewCamera.transform.rotation = Quaternion.Euler(90, 0, 0);
        }
    }

    private void PositionPerspectiveCamera()
    {
        viewCamera.orthographic = false;
        viewCamera.fieldOfView = 60f;
        Quaternion rotation = Quaternion.Euler(cameraPitch, cameraYaw, 0);
        viewCamera.transform.rotation = rotation;
        viewCamera.transform.position = cameraTarget - rotation * Vector3.forward * cameraDistance;
    }

    private void CapturePreview(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("Set PB_EDITOR_PREVIEW_OUTPUT.");
        var target = new RenderTexture(1280, 720, 24);
        RenderTexture previous = RenderTexture.active;
        try
        {
            viewCamera.targetTexture = target;
            viewCamera.Render();
            RenderTexture.active = target;
            var image = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            image.Apply();
            File.WriteAllBytes(path, image.EncodeToPNG());
            Destroy(image);
            Debug.Log("PB preview captured: " + path);
        }
        finally
        {
            viewCamera.targetTexture = null;
            RenderTexture.active = previous;
            target.Release();
            Destroy(target);
        }
    }

    private IEnumerator CaptureUiPreview(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("Set PB_EDITOR_PREVIEW_OUTPUT.");
        yield return new WaitForEndOfFrame();
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(path);
        yield return new WaitForSeconds(1f);
        Debug.Log("PB UI preview captured: " + path);
        Application.Quit();
    }

    private void SculptTerrain(Vector3 point, float amount)
    {
        int x = Mathf.Clamp(Mathf.RoundToInt(CellX(point.x)), 0, width);
        int y = Mathf.Clamp(Mathf.RoundToInt(point.z / CellSize), 0, height);
        int index = y * (width + 1) + x;
        Vector3[] vertices = terrainMesh.vertices;
        vertices[index].y += amount;
        terrainMesh.vertices = vertices;
        terrainMesh.RecalculateNormals();
        Vector3[] shown = terrainVisualMesh.vertices;
        for (int cellY = Mathf.Max(0, y - 1); cellY <= Mathf.Min(height - 1, y); cellY++)
            for (int cellX = Mathf.Max(0, x - 1); cellX <= Mathf.Min(width - 1, x); cellX++)
            {
                int grid = cellY * (width + 1) + cellX;
                int at = (cellY * width + cellX) * 4;
                shown[at].y = vertices[grid].y;
                shown[at + 1].y = vertices[grid + width + 1].y;
                shown[at + 2].y = vertices[grid + 1].y;
                shown[at + 3].y = vertices[grid + width + 2].y;
            }
        terrainVisualMesh.vertices = shown;
        Vector3[] smoothNormals = terrainMesh.normals;
        Vector3[] shownNormals = terrainVisualMesh.normals;
        for (int cellY = Mathf.Max(0, y - 1); cellY <= Mathf.Min(height - 1, y); cellY++)
            for (int cellX = Mathf.Max(0, x - 1); cellX <= Mathf.Min(width - 1, x); cellX++)
            {
                int grid = cellY * (width + 1) + cellX;
                int at = (cellY * width + cellX) * 4;
                shownNormals[at] = smoothNormals[grid];
                shownNormals[at + 1] = smoothNormals[grid + width + 1];
                shownNormals[at + 2] = smoothNormals[grid + 1];
                shownNormals[at + 3] = smoothNormals[grid + width + 2];
            }
        terrainVisualMesh.normals = shownNormals;
        terrainCollider.sharedMesh = null;
        terrainCollider.sharedMesh = terrainMesh;
        BuildOverlays();
        foreach (GameObject entityRoot in entityRoots)
        {
            PbMapEntity entity = entityRoot.GetComponent<PbMapEntity>();
            Vector3 p = entityRoot.transform.position;
            p.y = SampleTerrainHeight(CellX(p.x), p.z / CellSize) + entity.ExtraHeight;
            entityRoot.transform.position = p;
        }
        editedTerrain.Add(index);
        message = "Terrain vertex " + x + ", " + y + " height " + vertices[index].y;
    }

    private void Select(PbPlacedObject item)
    {
        ClearTileSelection();
        ClearHighlight();
        selected = item;
        selectedEntity = null;
        if (item != null)
        {
            SyncObjectInspector(item);
            if (!brushTypeCatalog)
            {
                int filteredRow = 0;
                foreach (PbPlacedObject listed in objects)
                {
                    if ((listed.Row + " " + BrushName(listed)).IndexOf(
                        brushSearch, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (listed == item) { catalogScroll.y = Mathf.Max(0, filteredRow * 28 - 50); break; }
                    filteredRow++;
                }
            }
        }
        if (item != null) Highlight(item.gameObject);
    }

    private static string Number(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private void SyncObjectInspector(PbPlacedObject item)
    {
        Vector3 p = item.transform.position;
        objectXText = Number(CellX(p.x));
        objectYText = Number(p.z / CellSize);
        objectHText = Number(p.y - 8f);
        angleZText = Number(item.AngleZ * Mathf.Rad2Deg);
        angleXText = Number(item.AngleX * Mathf.Rad2Deg);
        angleYText = Number(item.AngleY * Mathf.Rad2Deg);
        objectScaleText = Number(item.Scale);
    }

    private static bool ParseNumber(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
        float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private void ApplyObjectInspector()
    {
        if (selected == null) return;
        if (!ParseNumber(objectXText, out float x) ||
            !ParseNumber(objectYText, out float y) ||
            !ParseNumber(objectHText, out float h) ||
            !ParseNumber(angleZText, out float z) ||
            !ParseNumber(angleXText, out float ax) ||
            !ParseNumber(angleYText, out float ay) ||
            !ParseNumber(objectScaleText, out float scale) ||
            float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(h) ||
            float.IsNaN(z) || float.IsNaN(ax) || float.IsNaN(ay) ||
            float.IsNaN(scale) || float.IsInfinity(x) || float.IsInfinity(y) ||
            float.IsInfinity(h) || float.IsInfinity(z) || float.IsInfinity(ax) ||
            float.IsInfinity(ay) || float.IsInfinity(scale) || scale <= 0f)
        {
            message = "Invalid transform number or scale.";
            return;
        }
        selected.transform.position = new Vector3(MapX(x), h + 8f, y * CellSize);
        selected.AngleZ = z * Mathf.Deg2Rad;
        selected.AngleX = ax * Mathf.Deg2Rad;
        selected.AngleY = ay * Mathf.Deg2Rad;
        selected.transform.rotation = PbPlacedObject.Rotation(selected.AngleZ,
            selected.AngleX, selected.AngleY);
        selected.SetScale(scale);
        SyncObjectInspector(selected);
        GUIUtility.keyboardControl = 0;
        message = "Object " + selected.Row + " transform updated. Save copy to write the map.";
    }

    private void SelectEntity(PbMapEntity entity)
    {
        ClearTileSelection();
        ClearHighlight();
        selected = null;
        selectedEntity = entity;
        if (entity != null) Highlight(entity.gameObject);
    }

    private void SelectTile(PbMapTile tile, bool additive)
    {
        ClearHighlight();
        selected = null;
        selectedEntity = null;
        if (!additive) ClearTileSelection();
        if (tile == null) return;
        if (selectedTiles.Remove(tile))
        {
            LineRenderer old = tile.GetComponent<LineRenderer>();
            if (old != null) old.enabled = false;
            primaryTile = null;
            foreach (PbMapTile remaining in selectedTiles) primaryTile = remaining;
        }
        else
        {
            selectedTiles.Add(tile);
            primaryTile = tile;
            ShowTileOutline(tile);
        }
        message = selectedTiles.Count + " decal(s) selected. Shift+click adds/removes; Copy IDs sends rows to clipboard.";
    }

    private void ClearTileSelection()
    {
        foreach (PbMapTile tile in selectedTiles)
        {
            if (tile == null) continue;
            LineRenderer line = tile.GetComponent<LineRenderer>();
            if (line != null) line.enabled = false;
        }
        selectedTiles.Clear();
        primaryTile = null;
    }

    private void ShowTileOutline(PbMapTile tile)
    {
        LineRenderer line = tile.GetComponent<LineRenderer>();
        if (line == null)
        {
            line = tile.gameObject.AddComponent<LineRenderer>();
            line.sharedMaterial = selectedMaterial;
            line.startColor = Color.yellow;
            line.endColor = Color.yellow;
            line.widthMultiplier = 2f;
            line.loop = true;
            line.useWorldSpace = false;
            Vector3[] vertices = tile.GetComponent<MeshFilter>().sharedMesh.vertices;
            int stride = tile.CellsX + 1;
            line.positionCount = 2 * (tile.CellsX + tile.CellsY);
            int index = 0;
            for (int u = 0; u <= tile.CellsX; u++)
                line.SetPosition(index++, vertices[u] + Vector3.up * 2f);
            for (int v = 1; v <= tile.CellsY; v++)
                line.SetPosition(index++, vertices[v * stride + tile.CellsX] + Vector3.up * 2f);
            for (int u = tile.CellsX - 1; u >= 0; u--)
                line.SetPosition(index++, vertices[tile.CellsY * stride + u] + Vector3.up * 2f);
            for (int v = tile.CellsY - 1; v > 0; v--)
                line.SetPosition(index++, vertices[v * stride] + Vector3.up * 2f);
        }
        line.enabled = true;
    }

    private string TileRecord(PbMapTile tile)
    {
        string position = tile.Table == "toptextures"
            ? "X=" + tile.X + " Y=" + tile.Y
            : "X0=" + storage.Float("surfaces", "X0", tile.Row).ToString("0.##",
                System.Globalization.CultureInfo.InvariantCulture) +
              " Y0=" + storage.Float("surfaces", "Y0", tile.Row).ToString("0.##",
                System.Globalization.CultureInfo.InvariantCulture);
        return Path.GetFileName(mapPath) + " | " + tile.Table + " row " + tile.Row +
               " | " + position + " Flags=" + tile.Flags + " | " + tile.Descriptor;
    }

    private void Highlight(GameObject root)
    {
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
        {
            Material[] originals = renderer.sharedMaterials;
            highlighted[renderer] = originals;
            var overlay = new Material[originals.Length];
            for (int i = 0; i < overlay.Length; i++) overlay[i] = selectedMaterial;
            renderer.sharedMaterials = overlay;
        }
    }

    private void ClearHighlight()
    {
        foreach (KeyValuePair<Renderer, Material[]> entry in highlighted)
            if (entry.Key != null) entry.Key.sharedMaterials = entry.Value;
        highlighted.Clear();
    }

    private void SaveMap()
    {
        try
        {
            if (storage == null) throw new InvalidOperationException("Load a map first.");
            if (File.Exists(outputPath)) throw new IOException("Output already exists. Choose a new filename.");
            if (Hash(File.ReadAllBytes(mapPath)) != sourceHash)
                throw new IOException("Source map changed. Load it again.");
            PbStorage edited = PbObjectWriter.Save(storage, objects, CellSize, width, out int changed);
            int changedEntities = SaveEntityPositions(edited);
            edited = players.Save(edited);
            if (editedTerrain.Count > 0)
            {
                Vector3[] vertices = terrainMesh.vertices;
                foreach (int index in editedTerrain)
                    edited.SetFloat("points", "Z", index, vertices[index].y);
            }
            File.WriteAllBytes(outputPath, edited.Bytes());
            message = "Saved " + outputPath + " (objects: " + changed +
                      ", entities: " + changedEntities +
                      ", terrain vertices: " + editedTerrain.Count + ")";
        }
        catch (Exception error)
        {
            message = "Save failed: " + error.Message;
            Debug.LogException(error);
        }
    }

    private int SaveEntityPositions(PbStorage edited)
    {
        int changed = 0;
        foreach (GameObject root in entityRoots)
        {
            PbMapEntity item = root.GetComponent<PbMapEntity>();
            Vector3 p = root.transform.position;
            if (float.IsNaN(p.x) || float.IsNaN(p.z) ||
                float.IsInfinity(p.x) || float.IsInfinity(p.z))
                throw new InvalidDataException("Nonfinite entity position.");
            bool moved = (new Vector2(p.x, p.z) -
                          new Vector2(item.ImportedPosition.x,
                                      item.ImportedPosition.z)).sqrMagnitude > 0.0001f;
            bool turned = Quaternion.Angle(root.transform.rotation,
                                           item.ImportedRotation) > 0.01f;
            bool raised = Mathf.Abs(item.ExtraHeight - item.ImportedExtraHeight) > 0.0001f;
            if (!moved && !turned && !raised) continue;
            float x = CellX(p.x);
            float y = p.z / CellSize;
            int cellX = Mathf.FloorToInt(x);
            int cellY = Mathf.FloorToInt(y);
            if (item.Table == "buildings")
            {
                if (moved)
                {
                    edited.SetInt("buildings", "X", item.Row, Mathf.RoundToInt(x));
                    edited.SetInt("buildings", "Y", item.Row, Mathf.RoundToInt(y));
                }
                if (turned)
                    edited.SetInt("buildings", "Angle", item.Row,
                                  ((Mathf.RoundToInt(root.transform.eulerAngles.y / 90f) % 4) + 4) % 4);
            }
            else
            {
                if (moved)
                {
                    edited.SetInt(item.Table, "X", item.Row, cellX);
                    edited.SetInt(item.Table, "Y", item.Row, cellY);
                    edited.SetFloat(item.Table, "XF", item.Row, x - cellX);
                    edited.SetFloat(item.Table, "YF", item.Row, y - cellY);
                }
                if (item.Table == "cannons")
                {
                    if (turned)
                        edited.SetFloat("cannons", "Angle", item.Row,
                            Mathf.DeltaAngle(0f, root.transform.eulerAngles.y) * Mathf.Deg2Rad);
                    if (raised) edited.SetFloat("cannons", "AddH", item.Row, item.ExtraHeight);
                }
            }
            changed++;
        }
        return changed;
    }

    private static string Hash(byte[] data)
    {
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
    }

    private void OnGUI()
    {
        EnsureSkin();
        GUI.DrawTexture(new Rect(0, 0, Screen.width, HeaderHeight), chromeTexture);
        GUI.Label(new Rect(18, 10, 190, 30), "PB MAP EDITOR", titleStyle);
        string fileName = string.IsNullOrEmpty(mapPath) ? "No map" : Path.GetFileName(mapPath);
        string[] modes = { "Select", "Move", "Rotate", "Scale" };
        for (int i = 0; i < modes.Length; i++)
        {
            bool enabled = i != 3 || panelTab == 0;
            GUI.enabled = enabled;
            if (GUI.Button(new Rect(205 + i * 67, 10, 65, 30), modes[i],
                           transformTool == i ? activeTabStyle : tabStyle))
            {
                transformTool = i;
                gizmoAxis = -1;
            }
        }
        GUI.enabled = true;
        GUI.Label(new Rect(485, 11, Mathf.Max(50, Screen.width - 875), 26),
                  fileName + (storage == null ? "" : "  |  " + width + " x " + height),
                  mutedStyle);
        float actions = Screen.width - 382;
        if (GUI.Button(new Rect(actions, 10, 88, 30), showPaths ? "Hide paths" : "Files"))
            showPaths = !showPaths;
        if (GUI.Button(new Rect(actions + 94, 10, 104, 30), "Save copy")) SaveMap();
        if (GUI.Button(new Rect(actions + 204, 10, 94, 30),
                       perspective ? "Top (C)" : "3D (C)"))
            ToggleCamera();
        if (GUI.Button(new Rect(actions + 304, 10, 68, 30), "Exit"))
            Application.Quit();
        if (showPaths)
        {
            GUI.Label(new Rect(18, 55, 92, 24), "OPEN MAP", mutedStyle);
            mapPath = GUI.TextField(new Rect(116, 53, Screen.width - 220, 26), mapPath);
            if (GUI.Button(new Rect(Screen.width - 94, 53, 76, 26), "Load")) LoadMap();
            GUI.Label(new Rect(18, 87, 92, 24), "SAVE AS", mutedStyle);
            outputPath = GUI.TextField(new Rect(116, 85, Screen.width - 220, 26), outputPath);
            if (GUI.Button(new Rect(Screen.width - 94, 85, 76, 26), "Save")) SaveMap();
            GUI.Label(new Rect(18, 119, 92, 24), "MODELS", mutedStyle);
            modelRoot = GUI.TextField(new Rect(116, 117, Screen.width - 220, 26), modelRoot);
            if (GUI.Button(new Rect(Screen.width - 94, 117, 76, 26), "Reload")) ReloadModels();
        }
        string hint = panelTab == 0 ?
            transformTool == 0 ? "SELECT  ·  Click a model or a row in the object list" :
            transformTool == 1 ? "MOVE  ·  Gizmo uses Unity X/Y/Z (map Y = Z)  ·  IJKL  ·  PgUp/Dn height  ·  Shift = fine" :
            transformTool == 2 ? "ROTATE  ·  Drag Unity X/Y/Z handles  ·  ,/. U/O N/M rotate  ·  Shift = fine" :
            "SCALE  ·  Drag S handle  ·  +/- changes uniform scale  ·  Shift = fine"
            : panelTab == 1 ? "TERRAIN  ·  Enable height brush  ·  Click to raise  ·  Shift+click to lower"
            : panelTab == 2 ? "ENTITIES  ·  Select model or list row  ·  IJKL move  ·  ,/. rotate"
            : panelTab == 3 ? "PLAYERS  ·  Select a side and edit values  ·  Save copy to write changes"
            : "DECALS  ·  Click to select  ·  Shift+click to add/remove  ·  Click again to cycle";
        GUI.Label(new Rect(18, HeaderHeight - 31, Screen.width - 36, 22), hint, mutedStyle);
        DrawSidebar();
        DrawGizmo();
        GUI.DrawTexture(new Rect(0, Screen.height - StatusHeight, Screen.width, StatusHeight),
                        chromeTexture);
        GUI.Label(new Rect(14, Screen.height - StatusHeight + 4,
                           Screen.width - 28, 22), message, mutedStyle);
    }

    private static void DrawGizmoLine(Vector2 start, Vector2 end, Color color)
    {
        Vector2 delta = end - start;
        Matrix4x4 old = GUI.matrix;
        Color oldColor = GUI.color;
        GUI.color = color;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, start);
        GUI.DrawTexture(new Rect(start.x, start.y - 2f, delta.magnitude, 4f),
                        Texture2D.whiteTexture);
        GUI.matrix = old;
        GUI.color = oldColor;
    }

    private void DrawGizmo()
    {
        if (transformTool == 0 || (selected == null && selectedEntity == null)) return;
        Vector3 projected = viewCamera.WorldToScreenPoint(GizmoCenter);
        if (projected.z <= 0f) return;
        Vector2 center = GizmoScreen(GizmoCenter);
        int count = transformTool == 3 ? 1 : 3;
        for (int axis = 0; axis < count; axis++)
        {
            if (!GizmoAxisAllowed(axis)) continue;
            Vector2 end = GizmoEndpoint(axis);
            Color color = transformTool == 3 ? new Color(0.35f, 0.85f, 0.92f) :
                axis == 0 ? new Color(0.95f, 0.38f, 0.36f) :
                axis == 1 ? new Color(0.48f, 0.88f, 0.49f) :
                new Color(0.43f, 0.64f, 1f);
            DrawGizmoLine(center, end, color);
            Color oldColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(end.x - 11f, end.y - 11f, 22f, 22f),
                            Texture2D.whiteTexture);
            GUI.color = oldColor;
            GUI.Label(new Rect(end.x - 11f, end.y - 11f, 22f, 22f),
                      transformTool == 3 ? "S" : axis == 0 ? "X" : axis == 1 ? "Y" : "Z",
                      axisLabelStyle);
        }
    }

    private static Texture2D SolidTexture(Color color)
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }

    private void EnsureSkin()
    {
        if (titleStyle == null)
        {
            chromeTexture = SolidTexture(new Color(0.105f, 0.13f, 0.17f));
            panelTexture = SolidTexture(new Color(0.14f, 0.17f, 0.215f));
            accentTexture = SolidTexture(new Color(0.18f, 0.42f, 0.58f));
            fieldTexture = SolidTexture(new Color(0.19f, 0.225f, 0.27f));
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 18,
                fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            titleStyle.normal.textColor = Color.white;
            mutedStyle = new GUIStyle(GUI.skin.label) { fontSize = 13,
                alignment = TextAnchor.MiddleLeft };
            mutedStyle.normal.textColor = new Color(0.76f, 0.83f, 0.89f);
            tabStyle = new GUIStyle(GUI.skin.button) { fontSize = 12,
                alignment = TextAnchor.MiddleCenter };
            tabStyle.normal.background = panelTexture;
            tabStyle.hover.background = fieldTexture;
            tabStyle.active.background = accentTexture;
            tabStyle.normal.textColor = Color.white;
            tabStyle.hover.textColor = Color.white;
            activeTabStyle = new GUIStyle(tabStyle);
            activeTabStyle.normal.background = accentTexture;
            activeTabStyle.hover.background = accentTexture;
            axisLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12,
                fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            axisLabelStyle.normal.textColor = Color.black;
        }
        GUI.skin.label.normal.textColor = Color.white;
        GUI.skin.label.fontSize = 13;
        GUI.skin.button.normal.background = panelTexture;
        GUI.skin.button.hover.background = accentTexture;
        GUI.skin.button.active.background = accentTexture;
        GUI.skin.button.normal.textColor = Color.white;
        GUI.skin.button.hover.textColor = Color.white;
        GUI.skin.button.fontSize = 13;
        GUI.skin.box.normal.background = panelTexture;
        GUI.skin.box.normal.textColor = Color.white;
        GUI.skin.textField.normal.background = fieldTexture;
        GUI.skin.textField.focused.background = fieldTexture;
        GUI.skin.textField.normal.textColor = Color.white;
        GUI.skin.textField.focused.textColor = Color.white;
        GUI.skin.scrollView.normal.background = panelTexture;
    }

    private void DrawSidebar()
    {
        float left = Screen.width - ToolsWidth;
        float top = HeaderHeight + 6f;
        float panelHeight = Screen.height - top - StatusHeight - 6f;
        GUI.Box(new Rect(left, top, ToolsWidth, panelHeight), "");
        GUI.Label(new Rect(left + 12, top + 8, 300, 22), "WORKSPACE", mutedStyle);
        string[] tabs = { "Objects", "Terrain", "Entities", "Players", "Decals" };
        for (int i = 0; i < tabs.Length; i++)
            if (GUI.Button(new Rect(left + 10 + i * 62, top + 34, 61, 30),
                           tabs[i], panelTab == i ? activeTabStyle : tabStyle))
            {
                if (panelTab != i) Select(null);
                panelTab = i;
                terrainBrush = false;
                if (i == 2 && transformTool == 3) transformTool = 0;
            }
        if (panelTab == 0) DrawBrushPanel(left, top + 76);
        else if (panelTab == 1) DrawTerrainPanel(left, top + 76);
        else if (panelTab == 2) DrawEntityPanel(left, top + 76);
        else if (panelTab == 3) DrawPlayersPanel(left, top + 76);
        else DrawTilePanel(left, top + 76);
    }

    private void DrawTerrainPanel(float left, float top)
    {
        GUI.Label(new Rect(left + 12, top, 290, 24), "HEIGHT BRUSH", titleStyle);
        GUI.Label(new Rect(left + 12, top + 30, 300, 42),
                  "Raise or lower existing terrain vertices.\nEach click changes height by one unit.");
        if (GUI.Button(new Rect(left + 12, top + 84, 306, 34),
                       terrainBrush ? "Height brush  ON" : "Height brush  OFF"))
            terrainBrush = !terrainBrush;
        GUI.Label(new Rect(left + 12, top + 132, 300, 24),
                  "Left click  +1     Shift + click  -1", mutedStyle);
        GUI.Label(new Rect(left + 12, top + 168, 300, 24),
                  "Edited vertices: " + editedTerrain.Count, mutedStyle);
        GUI.Label(new Rect(left + 12, top + 202, 300, 54),
                  "Use Save copy to write height changes\nto a new .map file.", mutedStyle);
    }

    private void DrawTilePanel(float left, float top)
    {
        GUI.Label(new Rect(left + 10, top, 290, 22),
                  "Selected " + selectedTiles.Count + " / " + tileRows.Count + " decals");
        if (primaryTile != null)
        {
            GUI.Label(new Rect(left + 10, top + 25, 290, 22),
                      primaryTile.Table + " row " + primaryTile.Row +
                      " | Flags " + primaryTile.Flags);
            GUI.Label(new Rect(left + 10, top + 49, 290, 32), primaryTile.Descriptor);
        }
        else GUI.Label(new Rect(left + 10, top + 25, 290, 45),
                       "Click a decal on the map or select a row below.");
        if (GUI.Button(new Rect(left + 10, top + 82, 94, 25), "Copy IDs"))
        {
            var chosen = new List<PbMapTile>(selectedTiles);
            chosen.Sort((a, b) => a.Row.CompareTo(b.Row));
            var lines = new List<string>();
            foreach (PbMapTile tile in chosen) lines.Add(TileRecord(tile));
            GUIUtility.systemCopyBuffer = string.Join(Environment.NewLine, lines.ToArray());
            message = "Copied " + chosen.Count + " decal ID(s) to clipboard.";
        }
        if (GUI.Button(new Rect(left + 107, top + 82, 94, 25), "Focus") && primaryTile != null)
        {
            Vector3 center = primaryTile.GetComponent<MeshFilter>().sharedMesh.bounds.center;
            if (perspective)
            {
                cameraTarget = center;
                PositionPerspectiveCamera();
            }
            else viewCamera.transform.position = new Vector3(center.x,
                viewCamera.transform.position.y, center.z);
        }
        if (GUI.Button(new Rect(left + 204, top + 82, 96, 25), "Clear"))
            ClearTileSelection();
        GUI.Label(new Rect(left + 10, top + 115, 60, 22), "Search:");
        tileSearch = GUI.TextField(new Rect(left + 70, top + 115, 248, 26), tileSearch);
        float listTop = top + 143;
        float listHeight = Mathf.Max(0, Screen.height - StatusHeight - listTop - 12);
        int visible = 0;
        foreach (PbMapTile tile in tileRows)
            if (TileRecord(tile).IndexOf(tileSearch, StringComparison.OrdinalIgnoreCase) >= 0)
                visible++;
        tileScroll = GUI.BeginScrollView(new Rect(left + 10, listTop, 308, listHeight),
                                         tileScroll, new Rect(0, 0, 286, visible * 28));
        int row = 0;
        foreach (PbMapTile tile in tileRows)
        {
            if (TileRecord(tile).IndexOf(tileSearch, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            float y = row * 28;
            if (y + 28 >= tileScroll.y && y <= tileScroll.y + listHeight)
            {
                string name = tile.Descriptor.Split('?')[0].Replace('\\', '/');
                string label = (tile.Table == "toptextures" ? "T" : "S") +
                    tile.Row + " | F" + tile.Flags + " | " +
                    Path.GetFileName(name);
                if (GUI.Button(new Rect(0, y, 286, 27), label,
                               selectedTiles.Contains(tile) ? activeTabStyle : tabStyle))
                    SelectTile(tile, Event.current.shift);
            }
            row++;
        }
        GUI.EndScrollView();
    }

    private void DrawBrushPanel(float left, float top)
    {
        if (GUI.Button(new Rect(left + 10, top, 150, 28), "Placed objects",
                       brushTypeCatalog ? tabStyle : activeTabStyle))
            brushTypeCatalog = false;
        if (GUI.Button(new Rect(left + 164, top, 154, 28), "Object catalog",
                       brushTypeCatalog ? activeTabStyle : tabStyle))
            brushTypeCatalog = true;
        if (selected != null)
        {
            string name = selected.TypeIndex >= 0 && selected.TypeIndex < typeNames.Count
                ? typeNames[selected.TypeIndex] : "unknown";
            GUI.Label(new Rect(left + 12, top + 36, 300, 22),
                      "OBJECT " + selected.Row + "  /  " + name);
            GUI.Label(new Rect(left + 12, top + 62, 24, 22), "X", mutedStyle);
            objectXText = GUI.TextField(new Rect(left + 31, top + 60, 70, 26), objectXText);
            GUI.Label(new Rect(left + 111, top + 62, 24, 22), "Y", mutedStyle);
            objectYText = GUI.TextField(new Rect(left + 130, top + 60, 70, 26), objectYText);
            GUI.Label(new Rect(left + 210, top + 62, 24, 22), "H", mutedStyle);
            objectHText = GUI.TextField(new Rect(left + 229, top + 60, 87, 26), objectHText);
            GUI.Label(new Rect(left + 12, top + 91, 24, 22), "Z°", mutedStyle);
            angleZText = GUI.TextField(new Rect(left + 36, top + 89, 65, 26), angleZText);
            GUI.Label(new Rect(left + 111, top + 91, 24, 22), "X°", mutedStyle);
            angleXText = GUI.TextField(new Rect(left + 135, top + 89, 65, 26), angleXText);
            GUI.Label(new Rect(left + 210, top + 91, 24, 22), "Y°", mutedStyle);
            angleYText = GUI.TextField(new Rect(left + 234, top + 89, 82, 26), angleYText);
            GUI.Label(new Rect(left + 12, top + 122, 50, 22), "Scale", mutedStyle);
            objectScaleText = GUI.TextField(new Rect(left + 60, top + 119, 78, 26),
                                            objectScaleText);
            if (GUI.Button(new Rect(left + 144, top + 119, 32, 26), "−"))
            {
                selected.SetScale(selected.Scale / 1.1f);
                SyncObjectInspector(selected);
            }
            if (GUI.Button(new Rect(left + 181, top + 119, 32, 26), "+"))
            {
                selected.SetScale(selected.Scale * 1.1f);
                SyncObjectInspector(selected);
            }
            if (GUI.Button(new Rect(left + 220, top + 119, 96, 26), "Apply"))
                ApplyObjectInspector();
            if (GUI.Button(new Rect(left + 166, top + 145, 152, 28), "Delete selected"))
                DeleteSelected();
        }
        else GUI.Label(new Rect(left + 12, top + 48, 300, 50),
                       "Select an object on the map or in the list.", mutedStyle);
        if (GUI.Button(new Rect(left + 10, top + 145, 152, 28), "Add at center"))
            AddObject();
        GUI.Label(new Rect(left + 10, top + 182, 60, 22), "Search", mutedStyle);
        if (brushTypeCatalog)
            typeSearch = GUI.TextField(new Rect(left + 70, top + 181, 248, 26), typeSearch);
        else
            brushSearch = GUI.TextField(new Rect(left + 70, top + 181, 248, 26), brushSearch);
        float listTop = top + 215;
        float listHeight = Mathf.Max(0, Screen.height - StatusHeight - listTop - 12);
        int visible = 0;
        if (brushTypeCatalog)
            for (int i = 0; i < typeNames.Count; i++)
                if (typeNames[i].IndexOf(typeSearch, StringComparison.OrdinalIgnoreCase) >= 0)
                    visible++;
        else
            foreach (PbPlacedObject item in objects)
                if ((item.Row + " " + BrushName(item)).IndexOf(
                    brushSearch, StringComparison.OrdinalIgnoreCase) >= 0) visible++;
        catalogScroll = GUI.BeginScrollView(new Rect(left + 10, listTop, 308, listHeight),
                                            catalogScroll, new Rect(0, 0, 286, visible * 28));
        int row = 0;
        if (brushTypeCatalog)
        {
            for (int i = 0; i < typeNames.Count; i++)
            {
                if (typeNames[i].IndexOf(typeSearch, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                float y = row * 28;
                if (y + 28 >= catalogScroll.y && y <= catalogScroll.y + listHeight &&
                    GUI.Button(new Rect(0, y, 286, 27), i + ": " + typeNames[i],
                               i == activeType ? activeTabStyle : tabStyle))
                {
                    activeType = i;
                    if (selected != null) ChangeType(selected, i);
                }
                row++;
            }
        }
        else foreach (PbPlacedObject item in objects)
        {
            string label = item.Row + ": " + BrushName(item);
            if (label.IndexOf(brushSearch, StringComparison.OrdinalIgnoreCase) < 0) continue;
            float y = row * 28;
            if (y + 28 >= catalogScroll.y && y <= catalogScroll.y + listHeight &&
                GUI.Button(new Rect(0, y, 286, 27), label,
                           item == selected ? activeTabStyle : tabStyle)) Select(item);
            row++;
        }
        GUI.EndScrollView();
    }

    private string BrushName(PbPlacedObject item) =>
        item.TypeIndex >= 0 && item.TypeIndex < typeNames.Count
            ? typeNames[item.TypeIndex] : "unknown type " + item.TypeIndex;

    private void DrawEntityPanel(float left, float top)
    {
        GUI.Label(new Rect(left + 12, top, 300, 25), "SCENE ENTITIES", titleStyle);
        GUI.Label(new Rect(left + 12, top + 30, 300, 24),
                  selectedEntity == null ? "Select a model or row below"
                  : selectedEntity.Table + "  " + selectedEntity.Row +
                    "   /   kind " + selectedEntity.Kind + "   /   side " + selectedEntity.Side);
        if (selectedEntity != null)
        {
            Vector3 p = selectedEntity.transform.position;
            GUI.Label(new Rect(left + 12, top + 54, 300, 22),
                      "Map X " + CellX(p.x).ToString("0.##") +
                      "   Y " + (p.z / CellSize).ToString("0.##") +
                      "   H " + p.y.ToString("0.##"), mutedStyle);
        }
        GUI.Label(new Rect(left + 12, top + 78, 300, 22),
                  "IJKL move  ·  ,/. rotate  ·  PgUp/Dn cannon", mutedStyle);
        if (voLoader != null && voLoader.MissingModelPaths.Count > 0)
            GUI.Label(new Rect(left + 12, top + 102, 300, 22),
                      "Missing model: " + Path.GetFileName(voLoader.MissingModelPaths[0]));
        float listTop = top + 134;
        float listHeight = Mathf.Max(0, Screen.height - StatusHeight - listTop - 12);
        entityScroll = GUI.BeginScrollView(new Rect(left + 10, listTop, 308, listHeight),
                                           entityScroll,
                                           new Rect(0, 0, 286, entityRoots.Count * 28));
        for (int i = 0; i < entityRoots.Count; i++)
        {
            PbMapEntity entity = entityRoots[i].GetComponent<PbMapEntity>();
            string label = entity.Table + " " + entity.Row + " | kind " + entity.Kind +
                           " | side " + entity.Side;
            if (GUI.Button(new Rect(0, i * 28, 286, 27), label,
                           entity == selectedEntity ? activeTabStyle : tabStyle))
                SelectEntity(entity);
        }
        GUI.EndScrollView();
    }

    private void DrawPlayersPanel(float left, float top)
    {
        if (players == null) return;
        for (int i = 0; i < 4; i++)
            if (GUI.Button(new Rect(left + 10 + i * 77, top, 76, 28),
                           PbPlayers.Sides[i], i == playerSide ? activeTabStyle : tabStyle))
                playerSide = i;
        float listTop = top + 36;
        float listHeight = Mathf.Max(0, Screen.height - StatusHeight - listTop - 12);
        playerScroll = GUI.BeginScrollView(new Rect(left + 10, listTop, 308, listHeight),
                                           playerScroll, new Rect(0, 0, 286, 450));
        GUI.Label(new Rect(0, 0, 270, 23), "Starting resources — " + PbPlayers.Sides[playerSide]);
        for (int i = 0; i < 5; i++)
        {
            GUI.Label(new Rect(0, 25 + i * 26, 150, 24), PbPlayers.ResourceLabels[i]);
            players.Resources[playerSide, i] = GUI.TextField(new Rect(152, 25 + i * 26, 115, 24),
                                                              players.Resources[playerSide, i]);
        }
        GUI.Label(new Rect(0, 158, 270, 23), "AI settings");
        for (int i = 0; i < 6; i++)
        {
            GUI.Label(new Rect(0, 183 + i * 26, 150, 24), PbPlayers.AiLabels[i]);
            players.Ai[playerSide, i] = GUI.TextField(new Rect(152, 183 + i * 26, 115, 24),
                                                       players.Ai[playerSide, i]);
        }
        GUI.Label(new Rect(0, 351, 150, 24), "Reinforcements %");
        players.Maintenance = GUI.TextField(new Rect(152, 351, 115, 24), players.Maintenance);
        GUI.Label(new Rect(0, 385, 270, 46), "Use Save as to write player settings\nto a new .map file.");
        GUI.EndScrollView();
    }
}
