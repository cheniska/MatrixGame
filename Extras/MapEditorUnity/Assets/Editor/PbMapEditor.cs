using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class PbMapEditor
{
    private const float CellSize = 20.0f;

    [MenuItem("PB Map/Import editor .map...")]
    public static void ImportFromMenu()
    {
        string path = EditorUtility.OpenFilePanel("Open MapEditor map", "", "map");
        if (string.IsNullOrEmpty(path)) return;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        try
        {
            Import(path);
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            EditorUtility.DisplayDialog("PB Map", error.Message, "OK");
        }
    }

    [MenuItem("PB Map/Save editor .map as...")]
    public static void SaveAsFromMenu()
    {
        PbMapRoot root = UnityEngine.Object.FindFirstObjectByType<PbMapRoot>();
        if (root == null)
        {
            EditorUtility.DisplayDialog("PB Map", "Import a .map first.", "OK");
            return;
        }
        string output = EditorUtility.SaveFilePanel("Save MapEditor map", "",
                          Path.GetFileNameWithoutExtension(root.SourcePath) + "_edited.map", "map");
        if (string.IsNullOrEmpty(output)) return;
        try
        {
            SaveCopy(root, output);
            Debug.Log("Saved MapEditor map: " + output);
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            EditorUtility.DisplayDialog("PB Map", error.Message, "OK");
        }
    }

    public static PbMapRoot Import(string sourcePath)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Map not found", sourcePath);
        byte[] raw = File.ReadAllBytes(sourcePath);
        var storage = new PbStorage(raw);
        int width = storage.PropertyInt("SizeInUnitsX");
        int height = storage.PropertyInt("SizeInUnitsY");
        if (width < 1 || height < 1 || (long)width * height > 1000000)
            throw new InvalidDataException("Invalid map dimensions.");
        if (storage.Count("points", "Z", 3) != (width + 1) * (height + 1))
            throw new InvalidDataException("Invalid terrain height count.");

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        GameObject mapObject = new GameObject("PB Map: " + Path.GetFileName(sourcePath));
        PbMapRoot root = mapObject.AddComponent<PbMapRoot>();
        root.SourcePath = Path.GetFullPath(sourcePath);
        root.SourceSha256 = Hash(raw);
        root.Width = width;
        root.Height = height;

        GameObject terrain = new GameObject("Terrain (height grid)");
        terrain.transform.SetParent(mapObject.transform, false);
        var filter = terrain.AddComponent<MeshFilter>();
        terrain.AddComponent<MeshRenderer>();
        Mesh mesh = BuildTerrain(storage, width, height);
        Directory.CreateDirectory("Assets/Generated");
        string meshPath = AssetDatabase.GenerateUniqueAssetPath("Assets/Generated/" +
                          Path.GetFileNameWithoutExtension(sourcePath) + "_terrain.asset");
        AssetDatabase.CreateAsset(mesh, meshPath);
        filter.sharedMesh = mesh;

        GameObject objectsRoot = new GameObject("Objects");
        objectsRoot.transform.SetParent(mapObject.transform, false);
        int objectCount = storage.Count("objects", "Index", 0);
        foreach (string name in new[] { "X", "Y", "Index" })
            if (storage.Count("objects", name, 0) != objectCount)
                throw new InvalidDataException("Object columns have different lengths.");
        foreach (string name in new[] { "XF", "YF", "Height", "Angle" })
            if (storage.Count("objects", name, 3) != objectCount)
                throw new InvalidDataException("Object columns have different lengths.");
        for (int row = 0; row < objectCount; row++)
        {
            int type = storage.Int("objects", "Index", row);
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = "Object " + row.ToString("D4") + " (type " + type + ")";
            marker.transform.SetParent(objectsRoot.transform, false);
            marker.transform.localScale = new Vector3(5, 5, 5);
            marker.transform.position = new Vector3(
                (storage.Int("objects", "X", row) + storage.Float("objects", "XF", row)) * CellSize,
                storage.Float("objects", "Height", row),
                (storage.Int("objects", "Y", row) + storage.Float("objects", "YF", row)) * CellSize);
            marker.transform.rotation = Quaternion.Euler(0,
                storage.Float("objects", "Angle", row) * Mathf.Rad2Deg, 0);
            UnityEngine.Object.DestroyImmediate(marker.GetComponent<BoxCollider>());
            PbPlacedObject component = marker.AddComponent<PbPlacedObject>();
            component.Row = row;
            component.TypeIndex = type;
            component.ImportedPosition = marker.transform.position;
            component.ImportedRotation = marker.transform.rotation;
        }

        Selection.activeGameObject = mapObject;
        Debug.Log($"Imported {sourcePath}: {width}x{height}, {objectCount} object proxies.");
        return root;
    }

    private static Mesh BuildTerrain(PbStorage storage, int width, int height)
    {
        int columns = width + 1;
        var vertices = new Vector3[columns * (height + 1)];
        var uv = new Vector2[vertices.Length];
        for (int y = 0; y <= height; y++)
            for (int x = 0; x <= width; x++)
            {
                int i = y * columns + x;
                vertices[i] = new Vector3(x * CellSize, storage.Float("points", "Z", i), y * CellSize);
                uv[i] = new Vector2((float)x / width, (float)y / height);
            }
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
        var mesh = new Mesh { name = "PB terrain" };
        mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    public static int SaveCopy(PbMapRoot root, string outputPath)
    {
        if (File.Exists(outputPath)) throw new IOException("Output already exists: " + outputPath);
        byte[] raw = File.ReadAllBytes(root.SourcePath);
        if (Hash(raw) != root.SourceSha256)
            throw new IOException("Source .map changed since import; import it again.");
        var storage = new PbStorage(raw);
        int count = storage.Count("objects", "Index", 0);
        var seen = new HashSet<int>();
        int changed = 0;
        foreach (PbPlacedObject item in root.GetComponentsInChildren<PbPlacedObject>(true))
        {
            if (item.Row < 0 || item.Row >= count || !seen.Add(item.Row))
                throw new InvalidDataException("Missing, duplicate, or invalid object row.");
            bool moved = (item.transform.position - item.ImportedPosition).sqrMagnitude > 0.0001f;
            bool rotated = Quaternion.Angle(item.transform.rotation, item.ImportedRotation) > 0.01f;
            if (!moved && !rotated) continue;
            if (moved)
            {
                Vector3 position = item.transform.position;
                if (!Finite(position.x) || !Finite(position.y) || !Finite(position.z))
                    throw new InvalidDataException("Nonfinite object position.");
                float x = position.x / CellSize;
                float y = position.z / CellSize;
                int cellX = Mathf.FloorToInt(x);
                int cellY = Mathf.FloorToInt(y);
                storage.SetInt("objects", "X", item.Row, cellX);
                storage.SetInt("objects", "Y", item.Row, cellY);
                storage.SetFloat("objects", "XF", item.Row, x - cellX);
                storage.SetFloat("objects", "YF", item.Row, y - cellY);
                storage.SetFloat("objects", "Height", item.Row, position.y);
            }
            if (rotated)
                storage.SetFloat("objects", "Angle", item.Row,
                    item.transform.eulerAngles.y * Mathf.Deg2Rad);
            changed++;
        }
        if (seen.Count != count)
            throw new InvalidDataException("Scene has missing objects; refusing lossy save.");
        File.WriteAllBytes(outputPath, storage.Bytes());
        return changed;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static string Hash(byte[] data)
    {
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
    }

    // Invoke in Unity batch mode with PB_MAP_SOURCE and PB_MAP_OUTPUT_PREFIX.
    public static void BuildValidationScene()
    {
        string source = Environment.GetEnvironmentVariable("PB_MAP_SOURCE");
        string prefix = Environment.GetEnvironmentVariable("PB_MAP_OUTPUT_PREFIX");
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(prefix))
            throw new ArgumentException("Set PB_MAP_SOURCE and PB_MAP_OUTPUT_PREFIX.");
        PbMapRoot root = Import(source);
        Directory.CreateDirectory("Assets/Generated");
        EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),
                                     "Assets/Generated/TrainingPreview.unity");
        string unchanged = prefix + "_unchanged.map";
        string moved = prefix + "_moved.map";
        int unchangedCount = SaveCopy(root, unchanged);
        PbPlacedObject first = root.GetComponentsInChildren<PbPlacedObject>(true)[0];
        first.transform.position += new Vector3(CellSize, 0, 0);
        int movedCount = SaveCopy(root, moved);
        AssetDatabase.SaveAssets();
        Debug.Log($"PB validation: unchanged={unchangedCount}, moved={movedCount}, " +
                  $"scene=Assets/Generated/TrainingPreview.unity");
    }
}
