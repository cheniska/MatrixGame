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
    private readonly List<string> typeNames = new List<string>();
    private readonly List<string> typeData = new List<string>();
    private PbVoLoader voLoader;
    private PbPlacedObject selected;
    private Renderer selectedRenderer;
    private Material[] selectedOriginalMaterials;
    private Vector2 catalogScroll;
    private string typeSearch = "";
    private int activeType;
    private Material markerMaterial;
    private Material selectedMaterial;
    private Mesh terrainMesh;
    private MeshCollider terrainCollider;
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
        if (smokeMode == "preview")
        {
            if (storage == null || voLoader.LoadedModels < 1)
                throw new InvalidOperationException("Preview test could not load models.");
            ToggleCamera();
            CapturePreview(Environment.GetEnvironmentVariable("PB_EDITOR_PREVIEW_OUTPUT"));
            Application.Quit();
        }
        if (smokeMode == "1" || smokeMode == "add" || smokeMode == "delete" ||
            smokeMode == "terrain" || smokeMode == "models" || smokeMode == "camera")
        {
            if (storage == null) throw new InvalidOperationException("Smoke test could not load the map.");
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
            typeNames.Clear();
            typeData.Clear();
            editedTerrain.Clear();
            terrainBrush = false;
            selected = null;
            selectedRenderer = null;
            selectedOriginalMaterials = null;
            storage = parsed;
            width = newWidth;
            height = newHeight;
            sourceHash = Hash(bytes);
            mapPath = Path.GetFullPath(mapPath);
            outputPath = Path.Combine(Path.GetDirectoryName(mapPath),
                                      Path.GetFileNameWithoutExtension(mapPath) + "_edited.map");
            mapRoot = new GameObject("PB Map");
            BuildTerrain();
            int typeCount = parsed.Count("objtypes", "Name", 5);
            if (parsed.Count("objtypes", "Data", 5) != typeCount)
                throw new InvalidDataException("Object type catalog columns differ.");
            for (int type = 0; type < typeCount; type++)
            {
                typeNames.Add(parsed.String("objtypes", "Name", type));
                typeData.Add(parsed.String("objtypes", "Data", type));
            }
            voLoader = new PbVoLoader(modelRoot);
            activeType = 0;
            for (int row = 0; row < count; row++)
            {
                int type = parsed.Int("objects", "Index", row);
                Vector3 position = new Vector3(
                    (parsed.Int("objects", "X", row) + parsed.Float("objects", "XF", row)) * CellSize,
                    parsed.Float("objects", "Height", row) + 8f,
                    (parsed.Int("objects", "Y", row) + parsed.Float("objects", "YF", row)) * CellSize);
                CreateMarker(row, type, position, parsed.Float("objects", "Angle", row));
            }
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
                      " failed; textures: " + voLoader.LoadedTextures;
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
        marker.transform.rotation = Quaternion.Euler(0, angle * Mathf.Rad2Deg, 0);
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
        foreach (PbPlacedObject item in objects) AttachVisual(item);
        message = "VO: " + voLoader.LoadedModels + " loaded / " +
                  voLoader.MissingModels + " missing / " + voLoader.FailedModels +
                  " failed; textures: " + voLoader.LoadedTextures;
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
                vertices[y * columns + x] = new Vector3(x * CellSize,
                    storage.Float("points", "Z", y * columns + x), y * CellSize);
        var triangles = new int[width * height * 6];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int vertex = y * columns + x;
                int index = (y * width + x) * 6;
                triangles[index] = vertex;
                triangles[index + 1] = vertex + columns;
                triangles[index + 2] = vertex + 1;
                triangles[index + 3] = vertex + 1;
                triangles[index + 4] = vertex + columns;
                triangles[index + 5] = vertex + columns + 1;
            }
        terrainMesh = new Mesh { indexFormat = IndexFormat.UInt32, vertices = vertices, triangles = triangles };
        terrainMesh.RecalculateNormals();
        var terrain = new GameObject("Terrain");
        terrain.transform.SetParent(mapRoot.transform);
        terrain.AddComponent<MeshFilter>().sharedMesh = terrainMesh;
        terrain.AddComponent<MeshRenderer>().sharedMaterial =
            Resources.Load<Material>("PBTerrain") ??
            MakeMaterial(new Color(0.36f, 0.49f, 0.34f));
        terrainCollider = terrain.AddComponent<MeshCollider>();
        terrainCollider.sharedMesh = terrainMesh;
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
                Select(hit.collider.GetComponentInParent<PbPlacedObject>());
        }
        if (terrainBrush) return;
        if (selected == null) return;
        Vector3 move = Vector3.zero;
        if (Input.GetKeyDown(KeyCode.J)) move.x -= MoveStep;
        if (Input.GetKeyDown(KeyCode.L)) move.x += MoveStep;
        if (Input.GetKeyDown(KeyCode.I)) move.z += MoveStep;
        if (Input.GetKeyDown(KeyCode.K)) move.z -= MoveStep;
        if (Input.GetKeyDown(KeyCode.PageUp)) move.y += 1f;
        if (Input.GetKeyDown(KeyCode.PageDown)) move.y -= 1f;
        selected.transform.position += move;
        if (Input.GetKeyDown(KeyCode.Comma)) selected.transform.Rotate(0, -15f, 0);
        if (Input.GetKeyDown(KeyCode.Period)) selected.transform.Rotate(0, 15f, 0);
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
        int x = Mathf.Clamp(Mathf.RoundToInt(point.x / CellSize), 0, width);
        int y = Mathf.Clamp(Mathf.RoundToInt(point.z / CellSize), 0, height);
        int index = y * (width + 1) + x;
        Vector3[] vertices = terrainMesh.vertices;
        vertices[index].y += amount;
        terrainMesh.vertices = vertices;
        terrainMesh.RecalculateNormals();
        terrainCollider.sharedMesh = null;
        terrainCollider.sharedMesh = terrainMesh;
        editedTerrain.Add(index);
        message = "Terrain vertex " + x + ", " + y + " height " + vertices[index].y;
    }

    private void Select(PbPlacedObject item)
    {
        if (selectedRenderer != null && selectedOriginalMaterials != null)
            selectedRenderer.sharedMaterials = selectedOriginalMaterials;
        selected = item;
        selectedRenderer = item == null ? null : item.GetComponentInChildren<Renderer>();
        selectedOriginalMaterials = selectedRenderer == null ? null : selectedRenderer.sharedMaterials;
        if (selectedRenderer != null)
        {
            var highlight = new Material[selectedOriginalMaterials.Length];
            for (int i = 0; i < highlight.Length; i++) highlight[i] = selectedMaterial;
            selectedRenderer.sharedMaterials = highlight;
        }
    }

    private void SaveMap()
    {
        try
        {
            if (storage == null) throw new InvalidOperationException("Load a map first.");
            if (File.Exists(outputPath)) throw new IOException("Output already exists. Choose a new filename.");
            if (Hash(File.ReadAllBytes(mapPath)) != sourceHash)
                throw new IOException("Source map changed. Load it again.");
            PbStorage edited = PbObjectWriter.Save(storage, objects, CellSize, out int changed);
            if (editedTerrain.Count > 0)
            {
                Vector3[] vertices = terrainMesh.vertices;
                foreach (int index in editedTerrain)
                    edited.SetFloat("points", "Z", index, vertices[index].y);
            }
            File.WriteAllBytes(outputPath, edited.Bytes());
            message = "Saved " + outputPath + " (objects: " + changed +
                      ", terrain vertices: " + editedTerrain.Count + ")";
        }
        catch (Exception error)
        {
            message = "Save failed: " + error.Message;
            Debug.LogException(error);
        }
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
        DrawCatalog();
    }

    private void DrawCatalog()
    {
        float left = Screen.width - 310;
        float top = 155;
        float panelHeight = Screen.height - top;
        GUI.Box(new Rect(left, top, 310, panelHeight), "Object catalog");
        GUI.Label(new Rect(left + 10, top + 25, 55, 22), "Search:");
        typeSearch = GUI.TextField(new Rect(left + 65, top + 25, 235, 22), typeSearch);
        if (selected != null)
        {
            string name = selected.TypeIndex >= 0 && selected.TypeIndex < typeNames.Count
                ? typeNames[selected.TypeIndex] : "unknown";
            GUI.Label(new Rect(left + 10, top + 53, 290, 22),
                      "Selected: " + selected.Row + " / " + name);
            if (GUI.Button(new Rect(left + 160, top + 78, 140, 25), "Delete selected"))
                DeleteSelected();
        }
        if (GUI.Button(new Rect(left + 10, top + 78, 140, 25), "Add at center"))
            AddObject();
        if (GUI.Button(new Rect(left + 10, top + 107, 290, 25),
                       terrainBrush ? "Terrain brush ON (Shift lowers)" : "Terrain brush OFF"))
            terrainBrush = !terrainBrush;
        GUI.Label(new Rect(left + 10, top + 137, 290, 22),
                  "Type " + activeType + " / " + typeNames.Count +
                  " | Objects " + objects.Count);
        float listTop = top + 162;
        float listHeight = Mathf.Max(0, Screen.height - listTop - 10);
        int visible = 0;
        for (int i = 0; i < typeNames.Count; i++)
            if (typeNames[i].IndexOf(typeSearch, StringComparison.OrdinalIgnoreCase) >= 0)
                visible++;
        catalogScroll = GUI.BeginScrollView(new Rect(left + 10, listTop, 290, listHeight),
                                            catalogScroll, new Rect(0, 0, 270, visible * 25));
        int row = 0;
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
        GUI.EndScrollView();
    }
}
