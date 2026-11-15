using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Editor previews for the two overlay tables of a legacy .map.
public sealed class PbMapOverlays
{
    private readonly PbStorage storage;
    private readonly PbVoLoader loader;
    private readonly Vector3[] terrain;
    private readonly int width;
    private readonly int height;
    private readonly float cellSize;
    private readonly Transform parent;
    private readonly Dictionary<string, Material> materials =
        new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
    public int Tiles { get; private set; }
    public int Surfaces { get; private set; }
    public int Missing { get; private set; }

    public PbMapOverlays(PbStorage map, PbVoLoader resources, Mesh terrainMesh,
                         Transform root, int mapWidth, int mapHeight, float gridCell)
    {
        storage = map;
        loader = resources;
        terrain = terrainMesh.vertices;
        parent = root;
        width = mapWidth;
        height = mapHeight;
        cellSize = gridCell;
    }

    public void Build()
    {
        int count = storage.Count("toptextures", "Name", 5);
        for (int row = 0; row < count; row++)
        {
            string descriptor = storage.String("toptextures", "Name", row);
            Material material = MaterialFor(descriptor);
            if (material == null) { Missing++; continue; }
            int x = storage.Int("toptextures", "X", row);
            int y = storage.Int("toptextures", "Y", row);
            int flags = storage.Int("toptextures", "Flags", row, 1);
            BuildTile(row, x, y, flags, material);
            Tiles++;
        }
        count = storage.Count("surfaces", "Name", 5);
        for (int row = 0; row < count; row++)
        {
            string descriptor = storage.String("surfaces", "Name", row);
            Material material = MaterialFor(descriptor);
            if (material == null) { Missing++; continue; }
            BuildSurface(row, material);
            Surfaces++;
        }
    }

    private Material MaterialFor(string descriptor)
    {
        if (materials.TryGetValue(descriptor, out Material cached)) return cached;
        Texture2D texture = loader.LoadTexture(descriptor);
        if (texture == null) return null;
        Material material = TransparentMaterial(texture, Color.white);
        materials[descriptor] = material;
        return material;
    }

    public static Material TransparentMaterial(Texture2D texture, Color color)
    {
        Material baseMaterial = Resources.Load<Material>("PBModel");
        var material = baseMaterial != null ? new Material(baseMaterial)
            : new Material(Shader.Find("Standard"));
        material.mainTexture = texture;
        material.color = color;
        material.SetFloat("_Mode", 3f);
        material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.SetInt("_Cull", 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.renderQueue = 3000;
        if (texture.name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            material.mainTextureScale = new Vector2(1f, -1f);
            material.mainTextureOffset = new Vector2(0f, 1f);
        }
        return material;
    }

    private void BuildTile(int row, int x, int y, int flags, Material material)
    {
        const int subdivisions = 2;
        var vertices = new Vector3[9];
        var uv = new Vector2[9];
        var triangles = new int[24];
        for (int v = 0; v <= subdivisions; v++)
            for (int u = 0; u <= subdivisions; u++)
            {
                float cellX = x + u;
                float cellY = y + v;
                int index = v * 3 + u;
                vertices[index] = World(cellX, cellY, 0.5f);
                uv[index] = RotateUv(new Vector2(u / 2f, v / 2f), flags & 3);
            }
        int t = 0;
        for (int v = 0; v < subdivisions; v++)
            for (int u = 0; u < subdivisions; u++)
            {
                int p = v * 3 + u;
                triangles[t++] = p; triangles[t++] = p + 1; triangles[t++] = p + 3;
                triangles[t++] = p + 1; triangles[t++] = p + 4; triangles[t++] = p + 3;
            }
        AddMesh("Top tile " + row, vertices, uv, triangles, material);
    }

    private void BuildSurface(int row, Material baseMaterial)
    {
        var corners = new Vector2[4];
        for (int i = 0; i < 4; i++)
            corners[i] = new Vector2(storage.Float("surfaces", "X" + i, row),
                                     storage.Float("surfaces", "Y" + i, row));
        float length = Mathf.Max(Vector2.Distance(corners[0], corners[1]),
            Vector2.Distance(corners[1], corners[2]),
            Vector2.Distance(corners[2], corners[3]),
            Vector2.Distance(corners[3], corners[0]));
        int segments = Mathf.Clamp(Mathf.CeilToInt(length / cellSize), 2, 16);
        int stride = segments + 1;
        var vertices = new Vector3[stride * stride];
        var uv = new Vector2[vertices.Length];
        var triangles = new int[segments * segments * 6];
        for (int v = 0; v <= segments; v++)
            for (int u = 0; u <= segments; u++)
            {
                float fu = (float)u / segments, fv = (float)v / segments;
                Vector2 a = Vector2.Lerp(corners[0], corners[1], fu);
                Vector2 b = Vector2.Lerp(corners[3], corners[2], fu);
                Vector2 point = Vector2.Lerp(a, b, fv);
                int index = v * stride + u;
                vertices[index] = World(point.x / cellSize, point.y / cellSize, 0.75f);
                uv[index] = new Vector2(fu, fv);
            }
        int t = 0;
        for (int v = 0; v < segments; v++)
            for (int u = 0; u < segments; u++)
            {
                int p = v * stride + u;
                triangles[t++] = p; triangles[t++] = p + 1; triangles[t++] = p + stride;
                triangles[t++] = p + 1; triangles[t++] = p + stride + 1;
                triangles[t++] = p + stride;
            }
        uint tint = unchecked((uint)storage.Int("surfaces", "Color", row, 1));
        Material material = baseMaterial;
        if (tint != 0xffffffffu)
            material = TransparentMaterial(baseMaterial.mainTexture as Texture2D,
                new Color32((byte)(tint >> 16), (byte)(tint >> 8),
                            (byte)tint, (byte)(tint >> 24)));
        AddMesh("Surface " + row, vertices, uv, triangles, material);
    }

    private void AddMesh(string name, Vector3[] vertices, Vector2[] uv,
                         int[] triangles, Material material)
    {
        var mesh = new Mesh { name = name };
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        var visual = new GameObject(name);
        visual.transform.SetParent(parent, false);
        visual.AddComponent<MeshFilter>().sharedMesh = mesh;
        visual.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    private Vector3 World(float cellX, float cellY, float raise) =>
        new Vector3((width - cellX) * cellSize,
                    Height(cellX, cellY) + raise, cellY * cellSize);

    private float Height(float cellX, float cellY)
    {
        float x = Mathf.Clamp(cellX, 0f, width);
        float y = Mathf.Clamp(cellY, 0f, height);
        int x0 = Mathf.Min(Mathf.FloorToInt(x), width - 1);
        int y0 = Mathf.Min(Mathf.FloorToInt(y), height - 1);
        float dx = x - x0, dy = y - y0;
        int at = y0 * (width + 1) + x0;
        return Mathf.Lerp(Mathf.Lerp(terrain[at].y, terrain[at + 1].y, dx),
            Mathf.Lerp(terrain[at + width + 1].y, terrain[at + width + 2].y, dx), dy);
    }

    private static Vector2 RotateUv(Vector2 uv, int turns)
    {
        for (int i = 0; i < turns; i++) uv = new Vector2(uv.y, 1f - uv.x);
        return uv;
    }
}
