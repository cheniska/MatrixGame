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
    private PbPlacedObject selected;
    private Material markerMaterial;
    private Material selectedMaterial;
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
        if (Environment.GetEnvironmentVariable("PB_EDITOR_SMOKE") == "1")
        {
            if (storage == null) throw new InvalidOperationException("Smoke test could not load the map.");
            string smokeOutput = Environment.GetEnvironmentVariable("PB_EDITOR_SMOKE_OUTPUT");
            if (string.IsNullOrEmpty(smokeOutput))
                throw new InvalidOperationException("Set PB_EDITOR_SMOKE_OUTPUT.");
            outputPath = smokeOutput;
            SaveMap();
            if (!File.Exists(outputPath))
                throw new InvalidOperationException("Smoke test could not save the map: " + message);
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
            for (int row = 0; row < count; row++)
            {
                GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                marker.name = "Object " + row + " (type " + parsed.Int("objects", "Index", row) + ")";
                marker.transform.SetParent(mapRoot.transform);
                marker.transform.localScale = Vector3.one * 16f;
                marker.transform.position = new Vector3(
                    (parsed.Int("objects", "X", row) + parsed.Float("objects", "XF", row)) * CellSize,
                    parsed.Float("objects", "Height", row) + 8f,
                    (parsed.Int("objects", "Y", row) + parsed.Float("objects", "YF", row)) * CellSize);
                marker.transform.rotation = Quaternion.Euler(0,
                    parsed.Float("objects", "Angle", row) * Mathf.Rad2Deg, 0);
                marker.GetComponent<Renderer>().sharedMaterial = markerMaterial;
                var item = marker.AddComponent<PbPlacedObject>();
                item.Row = row;
                item.TypeIndex = parsed.Int("objects", "Index", row);
                item.ImportedPosition = marker.transform.position;
                item.ImportedRotation = marker.transform.rotation;
                objects.Add(item);
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
        var mesh = new Mesh { indexFormat = IndexFormat.UInt32, vertices = vertices, triangles = triangles };
        mesh.RecalculateNormals();
        var terrain = new GameObject("Terrain");
        terrain.transform.SetParent(mapRoot.transform);
        terrain.AddComponent<MeshFilter>().sharedMesh = mesh;
        terrain.AddComponent<MeshRenderer>().sharedMaterial =
            Resources.Load<Material>("PBTerrain") ??
            MakeMaterial(new Color(0.36f, 0.49f, 0.34f));
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
        if (Input.GetMouseButtonDown(0) && Input.mousePosition.y < Screen.height - 125)
        {
            Ray ray = viewCamera.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit, 10000f))
                Select(hit.collider.GetComponent<PbPlacedObject>());
        }
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
            if (objects.Count != storage.Count("objects", "Index", 0))
                throw new InvalidDataException("Object count changed.");
            var edited = new PbStorage(File.ReadAllBytes(mapPath));
            int changed = 0;
            foreach (PbPlacedObject item in objects)
            {
                if (item == null) throw new InvalidDataException("An object was deleted.");
                bool moved = (item.transform.position - item.ImportedPosition).sqrMagnitude > 0.0001f;
                bool rotated = Quaternion.Angle(item.transform.rotation, item.ImportedRotation) > 0.01f;
                if (!moved && !rotated) continue;
                if (moved)
                {
                    Vector3 p = item.transform.position;
                    float x = p.x / CellSize;
                    float y = p.z / CellSize;
                    int cellX = Mathf.FloorToInt(x);
                    int cellY = Mathf.FloorToInt(y);
                    edited.SetInt("objects", "X", item.Row, cellX);
                    edited.SetInt("objects", "Y", item.Row, cellY);
                    edited.SetFloat("objects", "XF", item.Row, x - cellX);
                    edited.SetFloat("objects", "YF", item.Row, y - cellY);
                    edited.SetFloat("objects", "Height", item.Row, p.y - 8f);
                }
                if (rotated)
                    edited.SetFloat("objects", "Angle", item.Row,
                        item.transform.eulerAngles.y * Mathf.Deg2Rad);
                changed++;
            }
            File.WriteAllBytes(outputPath, edited.Bytes());
            message = "Saved " + outputPath + " (changed objects: " + changed + ")";
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
    }
}
