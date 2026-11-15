using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public sealed class PbTerrainCatalog
{
    private readonly Dictionary<string, string> primaryImages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static PbTerrainCatalog Read(string path)
    {
        var result = new PbTerrainCatalog();
        if (!File.Exists(path)) return result;
        // Terrain IDs and resource paths in the legacy CP1251 catalog are ASCII.
        // Reading those bytes as ASCII also works in Unity player builds where
        // optional code-page providers are not bundled.
        string[] lines = File.ReadAllLines(path, Encoding.ASCII);
        int depth = 0;
        bool inTerrain = false;
        string name = null;
        bool images = false;
        var bestWeight = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (!inTerrain)
            {
                if (line.StartsWith("terrain ", StringComparison.OrdinalIgnoreCase) &&
                    line.EndsWith("{", StringComparison.Ordinal))
                {
                    inTerrain = true;
                    depth = 1;
                }
                continue;
            }
            if (depth == 1 && line.EndsWith("{", StringComparison.Ordinal))
                name = line.Substring(0, line.Length - 1).Trim().Split(new[] { " -sort" },
                    StringSplitOptions.None)[0];
            else if (depth == 2 && line.StartsWith("Images", StringComparison.OrdinalIgnoreCase) &&
                     line.EndsWith("{", StringComparison.Ordinal))
                images = true;
            else if (images && depth == 3 && name != null && line.Contains("="))
            {
                string[] parts = line.Split(new[] { '=' }, 2);
                if (int.TryParse(parts[0].Trim(), out int weight) &&
                    parts[1].Trim().StartsWith("Matrix\\Ter\\", StringComparison.OrdinalIgnoreCase) &&
                    (!bestWeight.TryGetValue(name, out int best) || weight > best))
                {
                    bestWeight[name] = weight;
                    result.primaryImages[name] = parts[1].Trim();
                }
            }
            foreach (char ch in line)
            {
                if (ch == '{') depth++;
                if (ch == '}') depth--;
            }
            if (depth < 3) images = false;
            if (depth < 2) name = null;
            if (depth == 0) break;
        }
        return result;
    }

    public Texture2D LoadTexture(string terrainName, string resourceRoot)
    {
        if (!primaryImages.TryGetValue(terrainName, out string relative)) return null;
        string root = Path.GetFullPath(resourceRoot);
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', '/')));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) +
                             Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path)) return null;
        var texture = new Texture2D(2, 2);
        if (!texture.LoadImage(File.ReadAllBytes(path)))
        {
            UnityEngine.Object.Destroy(texture);
            return null;
        }
        texture.name = terrainName;
        return texture;
    }
}
