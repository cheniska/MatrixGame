using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

// Preview of the first animation frame of MatrixGame .vo models.
// The game owns the files; no model or texture is embedded in the .map output.
public sealed class PbVoLoader
{
    public sealed class Asset
    {
        public Mesh Mesh;
        public Material[] Materials;
    }

    private readonly string root;
    private readonly Dictionary<string, Asset> models =
        new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> textures =
        new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    public int LoadedModels { get; private set; }
    public int MissingModels { get; private set; }
    public int FailedModels { get; private set; }
    public int LoadedTextures { get; private set; }

    public PbVoLoader(string resourceRoot)
    {
        root = Path.GetFullPath(resourceRoot);
    }

    public Asset Load(string descriptor)
    {
        if (string.IsNullOrEmpty(descriptor)) return null;
        if (models.TryGetValue(descriptor, out Asset cached)) return cached;
        Asset result = null;
        try
        {
            string relative = descriptor.Split('?')[0];
            string path = Resolve(relative);
            if (!File.Exists(path)) MissingModels++;
            else
            {
                result = ReadVo(path, descriptor);
                LoadedModels++;
            }
        }
        catch (Exception error)
        {
            FailedModels++;
            Debug.LogWarning("VO preview failed: " + descriptor + " — " + error.Message);
        }
        models[descriptor] = result;
        return result;
    }

    private string Resolve(string relative)
    {
        string normalized = relative.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized.Substring(2);
        if (Path.IsPathRooted(normalized))
            throw new InvalidDataException("Absolute resource path is not allowed.");
        string path = Path.GetFullPath(Path.Combine(root, normalized));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) +
                             Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Resource path leaves the model root.");
        return path;
    }

    private Asset ReadVo(string path, string descriptor)
    {
        var vo = new PbStorage(File.ReadAllBytes(path));
        byte[] vertexBytes = vo.RawArray("verts", "data", 2);
        byte[] indexBytes = vo.RawArray("idxs", "data", 2);
        byte[] frameBytes = vo.RawArray("frames", "data", 2);
        byte[] frameUnionBytes = vo.RawArray("frames", "unions", 2);
        byte[] unionBytes = vo.RawArray("unions", "data", 2);
        if (vertexBytes.Length % 32 != 0 || indexBytes.Length % 2 != 0 ||
            frameBytes.Length < 64 || frameBytes.Length % 64 != 0 ||
            frameUnionBytes.Length % 4 != 0 || unionBytes.Length % 24 != 0)
            throw new InvalidDataException("Unsupported VO geometry layout.");
        int surfaceCount = vo.Count("surfs", "texs", 5);
        if (surfaceCount < 1 || surfaceCount > 1000)
            throw new InvalidDataException("Invalid VO surface count.");
        int vertexCount = vertexBytes.Length / 32;
        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var uvs = new Vector2[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            int offset = i * 32;
            vertices[i] = new Vector3(F(vertexBytes, offset), F(vertexBytes, offset + 8),
                                      F(vertexBytes, offset + 4));
            normals[i] = new Vector3(F(vertexBytes, offset + 12), F(vertexBytes, offset + 20),
                                     F(vertexBytes, offset + 16));
            uvs[i] = new Vector2(F(vertexBytes, offset + 24), 1f - F(vertexBytes, offset + 28));
        }
        var groups = new List<int>[surfaceCount];
        for (int i = 0; i < surfaceCount; i++) groups[i] = new List<int>();
        int unionStart = I(frameBytes, 40);
        int unionCount = I(frameBytes, 44);
        if (unionStart < 0 || unionCount < 0 ||
            (long)unionStart + unionCount > frameUnionBytes.Length / 4)
            throw new InvalidDataException("Invalid VO first frame.");
        for (int i = 0; i < unionCount; i++)
        {
            int unionIndex = I(frameUnionBytes, (unionStart + i) * 4);
            if (unionIndex < 0 || unionIndex >= unionBytes.Length / 24)
                throw new InvalidDataException("Invalid VO union index.");
            int start = unionIndex * 24;
            int surface = I(unionBytes, start);
            int vertexBase = I(unionBytes, start + 4);
            int triangles = I(unionBytes, start + 16);
            int triangleStart = I(unionBytes, start + 20);
            if (surface < 0 || surface >= surfaceCount || vertexBase < 0 ||
                triangles < 0 || triangleStart < 0 ||
                (long)(triangleStart + triangles) * 6 > indexBytes.Length)
                throw new InvalidDataException("Invalid VO union data.");
            for (int triangle = 0; triangle < triangles; triangle++)
            {
                int at = (triangleStart + triangle) * 6;
                int a = vertexBase + U16(indexBytes, at);
                int b = vertexBase + U16(indexBytes, at + 2);
                int c = vertexBase + U16(indexBytes, at + 4);
                if (a >= vertexCount || b >= vertexCount || c >= vertexCount)
                    throw new InvalidDataException("VO triangle exceeds vertex array.");
                // MatrixGame uses Z-up. Swapping Y and Z reverses winding.
                groups[surface].Add(a);
                groups[surface].Add(c);
                groups[surface].Add(b);
            }
        }
        var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(path) };
        mesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.subMeshCount = surfaceCount;
        for (int i = 0; i < surfaceCount; i++) mesh.SetTriangles(groups[i], i);
        mesh.RecalculateBounds();

        var materials = new Material[surfaceCount];
        string mapTexture = Parameter(descriptor, "Tex");
        for (int i = 0; i < surfaceCount; i++)
        {
            string name = vo.String("surfs", "texs", i);
            Texture2D texture = FindTexture(path, name, mapTexture);
            Material baseMaterial = Resources.Load<Material>("PBModel");
            Material material = baseMaterial != null
                ? new Material(baseMaterial)
                : new Material(Shader.Find("Standard"));
            material.name = mesh.name + " surface " + i;
            material.color = texture == null ? new Color(0.72f, 0.72f, 0.68f) : Color.white;
            material.mainTexture = texture;
            materials[i] = material;
        }
        return new Asset { Mesh = mesh, Materials = materials };
    }

    private Texture2D FindTexture(string modelPath, string voName, string mapName)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(mapName))
        {
            string baseName = Path.GetFileNameWithoutExtension(mapName.Replace('\\', '/'));
            candidates.Add(Path.Combine(Path.GetDirectoryName(modelPath), baseName));
        }
        if (!string.IsNullOrEmpty(voName))
        {
            try { candidates.Add(Resolve(voName)); }
            catch (InvalidDataException) { }
        }
        foreach (string candidate in candidates)
            foreach (string extension in new[] { ".dds", ".png" })
            {
                string path = candidate.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                    ? candidate : candidate + extension;
                if (!File.Exists(path)) continue;
                if (textures.TryGetValue(path, out Texture2D cached)) return cached;
                try
                {
                    Texture2D texture = extension == ".dds"
                        ? ReadDds(path) : ReadPng(path);
                    if (texture != null)
                    {
                        textures[path] = texture;
                        LoadedTextures++;
                        return texture;
                    }
                }
                catch (Exception error)
                {
                    Debug.LogWarning("Texture preview failed: " + path + " — " + error.Message);
                }
            }
        return null;
    }

    private static Texture2D ReadPng(string path)
    {
        var texture = new Texture2D(2, 2);
        if (!texture.LoadImage(File.ReadAllBytes(path)))
            throw new InvalidDataException("PNG decoder rejected texture.");
        texture.name = Path.GetFileName(path);
        return texture;
    }

    private static Texture2D ReadDds(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 128 || bytes[0] != 'D' || bytes[1] != 'D' ||
            bytes[2] != 'S' || bytes[3] != ' ' || I(bytes, 4) != 124)
            throw new InvalidDataException("Invalid DDS header.");
        int height = I(bytes, 12);
        int width = I(bytes, 16);
        string fourcc = System.Text.Encoding.ASCII.GetString(bytes, 84, 4);
        TextureFormat format;
        int blockSize;
        if (fourcc == "DXT1") { format = TextureFormat.DXT1; blockSize = 8; }
        else if (fourcc == "DXT5") { format = TextureFormat.DXT5; blockSize = 16; }
        else return null;
        if (width < 1 || height < 1 || width > 8192 || height > 8192)
            throw new InvalidDataException("Invalid DDS dimensions.");
        int dataSize = checked(((width + 3) / 4) * ((height + 3) / 4) * blockSize);
        if (128L + dataSize > bytes.Length)
            throw new InvalidDataException("Truncated DDS texture.");
        var payload = new byte[dataSize];
        Buffer.BlockCopy(bytes, 128, payload, 0, dataSize);
        var texture = new Texture2D(width, height, format, false);
        texture.LoadRawTextureData(payload);
        texture.Apply(false, true);
        texture.name = Path.GetFileName(path);
        return texture;
    }

    private static string Parameter(string descriptor, string key)
    {
        foreach (string part in descriptor.Split('?'))
            if (part.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                return part.Substring(key.Length + 1);
        return "";
    }

    private static int I(byte[] bytes, int offset) => BitConverter.ToInt32(bytes, offset);
    private static float F(byte[] bytes, int offset) => BitConverter.ToSingle(bytes, offset);
    private static int U16(byte[] bytes, int offset) => BitConverter.ToUInt16(bytes, offset);
}
