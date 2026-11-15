using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class PbStandaloneApp : MonoBehaviour
{
    private const float CellSize = 20f;
    private const float MoveStep = 20f;
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
    private string typeSearch = "";
    private string brushSearch = "";
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
        if (smokeMode == "preview" || smokeMode == "preview_target")
        {
            if (storage == null || voLoader.LoadedModels < 1)
            {
                Debug.LogError("Preview test could not load models: " + message);
                Application.Quit(1);
                return;
            }
            if (smokeMode == "preview_target")
            {
                PbPlacedObject target = objects.Find(item => item.TypeIndex >= 0 &&
                    item.TypeIndex < typeData.Count &&
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
            smokeMode == "mirror" || smokeMode == "players" || smokeMode == "overlays")
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
                Texture2D tileTexture = voLoader.LoadTexture(
                    storage.String("toptextures", "Name", 0));
                if (tileMesh == null || tileMaterial == null ||
                    tileMaterial.shader.name != "PB/Overlay" ||
                    tileTexture == null || tileTexture.width <= 0 ||
                    Mathf.Abs(tileMesh.bounds.size.x -
                        tileTexture.width / 64f * CellSize) > 0.01f)
                    throw new InvalidOperationException("Overlay scale or alpha shader is invalid.");
                Transform surface = overlayRoot.transform.Find("Surface 0");
                Mesh surfaceMesh = surface == null ? null : surface.GetComponent<MeshFilter>().sharedMesh;
                if (surfaceMesh == null) throw new InvalidOperationException("Surface mesh is absent.");
                int segments = Mathf.RoundToInt(Mathf.Sqrt(surfaceMesh.vertexCount)) - 1;
                float x0 = storage.Float("surfaces", "X0", 0);
                float x3 = storage.Float("surfaces", "X3", 0);
                float expectedX = width * CellSize - Mathf.Lerp(x0, x3, 1f / segments);
                if (Mathf.Abs(surfaceMesh.vertices[1].x - expectedX) > 0.01f)
                    throw new InvalidOperationException("Surface corner orientation is invalid.");
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
                CreateMarker(row, type, position, parsed.Float("objects", "Angle", row));
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

    private PbPlacedObject CreateMarker(int row, int type, Vector3 position, float angle)
    {
        GameObject marker = new GameObject();
        marker.name = "Object " + row + " (type " + type + ")";
        marker.transform.SetParent(mapRoot.transform);
        marker.transform.position = position;
        marker.transform.rotation = Quaternion.Euler(0, -angle * Mathf.Rad2Deg, 0);
        var item = marker.AddComponent<PbPlacedObject>();
        item.Row = row;
        item.TypeIndex = type;
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
            float scale = item.Row >= 0 ? storage.Float("objects", "Scale", item.Row) : 1f;
            if (!float.IsNaN(scale) && !float.IsInfinity(scale) && scale > 0f)
                visual.transform.localScale = Vector3.one * scale;
            visual.AddComponent<MeshFilter>().sharedMesh = asset.Mesh;
            visual.AddComponent<MeshRenderer>().sharedMaterials = asset.Materials;
            var collider = visual.AddComponent<BoxCollider>();
            collider.center = asset.Mesh.bounds.center;
            Vector3 size = asset.Mesh.bounds.size;
            collider.size = new Vector3(Mathf.Max(size.x, 1f),
                                        Mathf.Max(size.y, 1f), Mathf.Max(size.z, 1f));
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
        root.transform.rotation = Quaternion.Euler(0, -angle, 0);
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
            AttachEntityModel(root, "Matrix/Building/b" + entity.Kind + ".vo", Vector3.zero);
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
        var collider = visual.AddComponent<BoxCollider>();
        collider.center = asset.Mesh.bounds.center;
        Vector3 size = asset.Mesh.bounds.size;
        collider.size = new Vector3(Mathf.Max(size.x, 1f),
                                    Mathf.Max(size.y, 1f), Mathf.Max(size.z, 1f));
        return visual;
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

    private float MapX(float cellX) => (width - cellX) * CellSize;
    private float CellX(float worldX) => width - worldX / CellSize;

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
        terrainVisualMesh.uv = uv;
        terrainVisualMesh.subMeshCount = triangles.Length;
        for (int i = 0; i < triangles.Length; i++)
            terrainVisualMesh.SetTriangles(triangles[i], i);
        terrainVisualMesh.RecalculateNormals();
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
        bool viewport = Input.mousePosition.y < Screen.height - 150 &&
                        Input.mousePosition.x < Screen.width - 310;
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
            Ray ray = viewCamera.ScreenPointToRay(Input.mousePosition);
            if (terrainBrush && terrainCollider.Raycast(ray, out RaycastHit terrainHit, 10000f))
                SculptTerrain(terrainHit.point, Input.GetKey(KeyCode.LeftShift) ? -1f : 1f);
            else if (!terrainBrush && Physics.Raycast(ray, out RaycastHit hit, 10000f))
            {
                PbPlacedObject objectHit = hit.collider.GetComponentInParent<PbPlacedObject>();
                PbMapEntity entityHit = hit.collider.GetComponentInParent<PbMapEntity>();
                if (panelTab == 0 && objectHit != null) Select(objectHit);
                else if (panelTab == 1 && entityHit != null) SelectEntity(entityHit);
                else if (panelTab != 2) Select(null);
            }
        }
        if (terrainBrush) return;
        if (selected == null && selectedEntity == null) return;
        Vector3 move = Vector3.zero;
        if (Input.GetKeyDown(KeyCode.J)) move.x -= MoveStep;
        if (Input.GetKeyDown(KeyCode.L)) move.x += MoveStep;
        if (Input.GetKeyDown(KeyCode.I)) move.z += MoveStep;
        if (Input.GetKeyDown(KeyCode.K)) move.z -= MoveStep;
        if (selected != null)
        {
            if (Input.GetKeyDown(KeyCode.PageUp)) move.y += 1f;
            if (Input.GetKeyDown(KeyCode.PageDown)) move.y -= 1f;
            selected.transform.position += move;
            if (Input.GetKeyDown(KeyCode.Comma)) selected.transform.Rotate(0, -15f, 0);
            if (Input.GetKeyDown(KeyCode.Period)) selected.transform.Rotate(0, 15f, 0);
        }
        else
        {
            selectedEntity.transform.position += move;
            if (selectedEntity.Table == "cannons")
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
            if (selectedEntity.Table != "robots")
            {
                if (Input.GetKeyDown(KeyCode.Comma)) selectedEntity.transform.Rotate(0, -turn, 0);
                if (Input.GetKeyDown(KeyCode.Period)) selectedEntity.transform.Rotate(0, turn, 0);
            }
        }
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
        terrainVisualMesh.RecalculateNormals();
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
        ClearHighlight();
        selected = item;
        selectedEntity = null;
        if (item != null) Highlight(item.gameObject);
    }

    private void SelectEntity(PbMapEntity entity)
    {
        ClearHighlight();
        selected = null;
        selectedEntity = entity;
        if (entity != null) Highlight(entity.gameObject);
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
                                  ((Mathf.RoundToInt(-root.transform.eulerAngles.y / 90f) % 4) + 4) % 4);
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
                            -Mathf.DeltaAngle(0f, root.transform.eulerAngles.y) * Mathf.Deg2Rad);
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
        GUI.Box(new Rect(0, 0, Screen.width, 150), "PB Map Editor");
        if (GUI.Button(new Rect(Screen.width - 200, 1, 110, 20),
                       perspective ? "Top view (C)" : "3D view (C)"))
            ToggleCamera();
        if (GUI.Button(new Rect(Screen.width - 85, 1, 75, 20), "Exit"))
            Application.Quit();
        GUI.Label(new Rect(10, 24, 85, 22), "Source .map:");
        mapPath = GUI.TextField(new Rect(100, 23, Screen.width - 190, 23), mapPath);
        if (GUI.Button(new Rect(Screen.width - 85, 23, 75, 23), "Load")) LoadMap();
        GUI.Label(new Rect(10, 52, 85, 22), "Save as:");
        outputPath = GUI.TextField(new Rect(100, 51, Screen.width - 190, 23), outputPath);
        if (GUI.Button(new Rect(Screen.width - 85, 51, 75, 23), "Save")) SaveMap();
        GUI.Label(new Rect(10, 80, 85, 22), "Model root:");
        modelRoot = GUI.TextField(new Rect(100, 79, Screen.width - 190, 23), modelRoot);
        if (GUI.Button(new Rect(Screen.width - 85, 79, 75, 23), "Reload")) ReloadModels();
        GUI.Label(new Rect(10, 106, Screen.width - 20, 22), message);
        GUI.Label(new Rect(10, 128, Screen.width - 20, 20), perspective
            ? "3D: right drag orbit | WASD pan | Q/E height | wheel zoom | C top | click model select"
            : "Top: WASD/arrows pan | wheel zoom | C 3D | click model select | IJKL move | PgUp/PgDn height | ,/. rotate");
        DrawSidebar();
    }

    private void DrawSidebar()
    {
        float left = Screen.width - 310;
        float top = 155;
        float panelHeight = Screen.height - top;
        GUI.Box(new Rect(left, top, 310, panelHeight), "Map tools");
        string[] tabs = { "Brushes", "Entities", "Players" };
        for (int i = 0; i < tabs.Length; i++)
            if (GUI.Button(new Rect(left + 10 + i * 97, top + 24, 96, 26), tabs[i]))
            {
                if (panelTab != i) Select(null);
                panelTab = i;
                terrainBrush = false;
            }
        if (panelTab == 0) DrawBrushPanel(left, top + 55);
        else if (panelTab == 1) DrawEntityPanel(left, top + 55);
        else DrawPlayersPanel(left, top + 55);
    }

    private void DrawBrushPanel(float left, float top)
    {
        if (GUI.Button(new Rect(left + 10, top, 140, 25), "Placed brushes"))
            brushTypeCatalog = false;
        if (GUI.Button(new Rect(left + 160, top, 140, 25), "Object types"))
            brushTypeCatalog = true;
        if (GUI.Button(new Rect(left + 10, top + 30, 290, 25),
                       terrainBrush ? "Height brush ON (Shift lowers)" : "Height brush OFF"))
            terrainBrush = !terrainBrush;
        if (selected != null)
        {
            string name = selected.TypeIndex >= 0 && selected.TypeIndex < typeNames.Count
                ? typeNames[selected.TypeIndex] : "unknown";
            GUI.Label(new Rect(left + 10, top + 61, 290, 22),
                      "Selected brush " + selected.Row + ": " + name);
            if (GUI.Button(new Rect(left + 160, top + 87, 140, 25), "Delete selected"))
                DeleteSelected();
        }
        if (GUI.Button(new Rect(left + 10, top + 87, 140, 25), "Add at center"))
            AddObject();
        GUI.Label(new Rect(left + 10, top + 118, 60, 22), "Search:");
        if (brushTypeCatalog)
            typeSearch = GUI.TextField(new Rect(left + 70, top + 118, 230, 22), typeSearch);
        else
            brushSearch = GUI.TextField(new Rect(left + 70, top + 118, 230, 22), brushSearch);
        float listTop = top + 145;
        float listHeight = Mathf.Max(0, Screen.height - listTop - 10);
        int visible = 0;
        if (brushTypeCatalog)
            for (int i = 0; i < typeNames.Count; i++)
                if (typeNames[i].IndexOf(typeSearch, StringComparison.OrdinalIgnoreCase) >= 0)
                    visible++;
        else
            foreach (PbPlacedObject item in objects)
                if ((item.Row + " " + BrushName(item)).IndexOf(
                    brushSearch, StringComparison.OrdinalIgnoreCase) >= 0) visible++;
        catalogScroll = GUI.BeginScrollView(new Rect(left + 10, listTop, 290, listHeight),
                                            catalogScroll, new Rect(0, 0, 270, visible * 25));
        int row = 0;
        if (brushTypeCatalog)
        {
            for (int i = 0; i < typeNames.Count; i++)
            {
                if (typeNames[i].IndexOf(typeSearch, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (GUI.Button(new Rect(0, row * 25, 270, 24), i + ": " + typeNames[i]))
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
            float y = row * 25;
            if (y + 25 >= catalogScroll.y && y <= catalogScroll.y + listHeight &&
                GUI.Button(new Rect(0, y, 270, 24), label)) Select(item);
            row++;
        }
        GUI.EndScrollView();
    }

    private string BrushName(PbPlacedObject item) =>
        item.TypeIndex >= 0 && item.TypeIndex < typeNames.Count
            ? typeNames[item.TypeIndex] : "unknown type " + item.TypeIndex;

    private void DrawEntityPanel(float left, float top)
    {
        GUI.Label(new Rect(left + 10, top, 290, 22),
                  selectedEntity == null ? "Select a model or a row below"
                  : selectedEntity.Table + " " + selectedEntity.Row +
                    " | kind " + selectedEntity.Kind + " | side " + selectedEntity.Side);
        GUI.Label(new Rect(left + 10, top + 26, 290, 40),
                  "IJKL move | ,/. rotate | PgUp/PgDn cannon height");
        if (voLoader != null && voLoader.MissingModelPaths.Count > 0)
            GUI.Label(new Rect(left + 10, top + 61, 290, 22),
                      "Missing model: " + Path.GetFileName(voLoader.MissingModelPaths[0]));
        float listTop = top + 88;
        float listHeight = Mathf.Max(0, Screen.height - listTop - 10);
        entityScroll = GUI.BeginScrollView(new Rect(left + 10, listTop, 290, listHeight),
                                           entityScroll,
                                           new Rect(0, 0, 270, entityRoots.Count * 26));
        for (int i = 0; i < entityRoots.Count; i++)
        {
            PbMapEntity entity = entityRoots[i].GetComponent<PbMapEntity>();
            string label = entity.Table + " " + entity.Row + " | kind " + entity.Kind +
                           " | side " + entity.Side;
            if (GUI.Button(new Rect(0, i * 26, 270, 25), label)) SelectEntity(entity);
        }
        GUI.EndScrollView();
    }

    private void DrawPlayersPanel(float left, float top)
    {
        if (players == null) return;
        for (int i = 0; i < 4; i++)
            if (GUI.Button(new Rect(left + 10 + i * 73, top, 72, 24), PbPlayers.Sides[i]))
                playerSide = i;
        float listTop = top + 30;
        float listHeight = Mathf.Max(0, Screen.height - listTop - 10);
        playerScroll = GUI.BeginScrollView(new Rect(left + 10, listTop, 290, listHeight),
                                           playerScroll, new Rect(0, 0, 270, 450));
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
