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
    private string message = "Enter an editor .map path, then press Load.";
    private PbStorage storage;
    private string sourceHash;
    private GameObject mapRoot;
    private Camera viewCamera;
    private readonly List<PbPlacedObject> objects = new List<PbPlacedObject>();
    private readonly List<string> typeNames = new List<string>();
    private PbPlacedObject selected;
    private Vector2 catalogScroll;
    private string typeSearch = "";
    private int activeType;
    private Material markerMaterial;
    private Material selectedMaterial;
    private Mesh terrainMesh;
    private MeshCollider terrainCollider;
    private readonly HashSet<int> editedTerrain = new HashSet<int>();
    private bool terrainBrush;
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
        if (smokeMode == "1" || smokeMode == "add" || smokeMode == "delete" ||
            smokeMode == "terrain")
        {
            if (storage == null) throw new InvalidOperationException("Smoke test could not load the map.");
            string smokeOutput = Environment.GetEnvironmentVariable("PB_EDITOR_SMOKE_OUTPUT");
            if (string.IsNullOrEmpty(smokeOutput))
                throw new InvalidOperationException("Set PB_EDITOR_SMOKE_OUTPUT.");
            outputPath = smokeOutput;
            int expectedCount = objects.Count;
            if (smokeMode == "add") { AddObject(); expectedCount++; }
            if (smokeMode == "delete") { Select(objects[0]); DeleteSelected(); expectedCount--; }
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
            editedTerrain.Clear();
            terrainBrush = false;
            selected = null;
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
            for (int type = 0; type < typeCount; type++)
                typeNames.Add(parsed.String("objtypes", "Name", type));
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
            message = "Loaded " + width + " x " + height + ", objects: " + count;
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
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
        marker.name = "Object " + row + " (type " + type + ")";
        marker.transform.SetParent(mapRoot.transform);
        marker.transform.localScale = Vector3.one * 16f;
        marker.transform.position = position;
        marker.transform.rotation = Quaternion.Euler(0, angle * Mathf.Rad2Deg, 0);
        marker.GetComponent<Renderer>().sharedMaterial = markerMaterial;
        var item = marker.AddComponent<PbPlacedObject>();
        item.Row = row;
        item.TypeIndex = type;
        item.ImportedPosition = position;
        item.ImportedRotation = marker.transform.rotation;
        objects.Add(item);
        return item;
    }

    private void AddObject()
    {
        if (storage == null || activeType < 0 || activeType >= typeNames.Count) return;
        Vector3 center = viewCamera.transform.position;
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
        float pan = viewCamera.orthographicSize * 0.8f * Time.deltaTime;
        Vector3 offset = Vector3.zero;
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) offset.x -= pan;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) offset.x += pan;
        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) offset.z += pan;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) offset.z -= pan;
        viewCamera.transform.position += offset;
        float scroll = Input.mouseScrollDelta.y;
        if (scroll != 0 && Input.mousePosition.y < Screen.height - 125)
            viewCamera.orthographicSize = Mathf.Clamp(viewCamera.orthographicSize *
                Mathf.Pow(0.85f, scroll), 20f, 20000f);
        if (Input.GetMouseButtonDown(0) && Input.mousePosition.y < Screen.height - 125 &&
            Input.mousePosition.x < Screen.width - 310)
        {
            Ray ray = viewCamera.ScreenPointToRay(Input.mousePosition);
            if (terrainBrush && terrainCollider.Raycast(ray, out RaycastHit terrainHit, 10000f))
                SculptTerrain(terrainHit.point, Input.GetKey(KeyCode.LeftShift) ? -1f : 1f);
            else if (!terrainBrush && Physics.Raycast(ray, out RaycastHit hit, 10000f))
                Select(hit.collider.GetComponent<PbPlacedObject>());
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
        if (selected != null) selected.GetComponent<Renderer>().sharedMaterial = markerMaterial;
        selected = item;
        if (selected != null) selected.GetComponent<Renderer>().sharedMaterial = selectedMaterial;
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
        GUI.Box(new Rect(0, 0, Screen.width, 125), "PB Map Editor - standalone prototype");
        if (GUI.Button(new Rect(Screen.width - 85, 1, 75, 20), "Exit"))
            Application.Quit();
        GUI.Label(new Rect(10, 24, 85, 22), "Source .map:");
        mapPath = GUI.TextField(new Rect(100, 23, Screen.width - 190, 23), mapPath);
        if (GUI.Button(new Rect(Screen.width - 85, 23, 75, 23), "Load")) LoadMap();
        GUI.Label(new Rect(10, 52, 85, 22), "Save as:");
        outputPath = GUI.TextField(new Rect(100, 51, Screen.width - 190, 23), outputPath);
        if (GUI.Button(new Rect(Screen.width - 85, 51, 75, 23), "Save")) SaveMap();
        GUI.Label(new Rect(10, 79, Screen.width - 20, 22), message);
        GUI.Label(new Rect(10, 101, Screen.width - 20, 20),
            "WASD/arrows pan | wheel zoom | click cube select | IJKL move | PgUp/PgDn height | ,/. rotate"
            + (selected == null ? "" : " | Selected row " + selected.Row + " type " + selected.TypeIndex));
        DrawCatalog();
    }

    private void DrawCatalog()
    {
        float left = Screen.width - 310;
        float top = 130;
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
                if (selected != null) selected.TypeIndex = i;
            }
            row++;
        }
        GUI.EndScrollView();
    }
}
