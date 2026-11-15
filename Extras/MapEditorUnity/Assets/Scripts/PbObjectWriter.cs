using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public static class PbObjectWriter
{
    private static readonly string[] IntFields = { "Index", "X", "Y" };
    private static readonly string[] FloatFields =
        { "XF", "YF", "Height", "Angle", "AngleX", "AngleY", "Scale" };

    public static PbStorage Save(PbStorage source, IList<PbPlacedObject> objects,
                                 float cellSize, int mapWidth, out int changed)
    {
        int originalCount = source.Count("objects", "Index", 0);
        bool structural = objects.Count != originalCount;
        var seen = new HashSet<int>();
        foreach (PbPlacedObject item in objects)
        {
            if (item == null || item.Row < -1 || item.Row >= originalCount ||
                (item.Row >= 0 && !seen.Add(item.Row)))
                throw new InvalidDataException("Missing, duplicate, or invalid object.");
            if (item.Row == -1) structural = true;
        }
        if (!structural && seen.Count != originalCount)
            throw new InvalidDataException("Missing original objects.");

        PbStorage result = new PbStorage(source.Bytes());
        changed = 0;
        if (!structural)
        {
            foreach (PbPlacedObject item in objects)
            {
                bool moved = Moved(item);
                bool rotated = Rotated(item);
                bool retyped = item.TypeIndex != source.Int("objects", "Index", item.Row);
                if (!moved && !rotated && !retyped) continue;
                if (retyped) result.SetInt("objects", "Index", item.Row, item.TypeIndex);
                if (moved) SetPosition(result, item, cellSize, mapWidth);
                if (rotated)
                    result.SetFloat("objects", "Angle", item.Row,
                                    -Mathf.DeltaAngle(0f, item.transform.eulerAngles.y) * Mathf.Deg2Rad);
                changed++;
            }
            return result;
        }

        foreach (string field in IntFields)
        {
            var bytes = new byte[objects.Count * 4];
            for (int row = 0; row < objects.Count; row++)
                Write(bytes, row, GetInt(source, objects[row], field, cellSize, mapWidth));
            result = result.ReplaceArray("objects", field, 0, 0, bytes);
        }
        foreach (string field in FloatFields)
        {
            var bytes = new byte[objects.Count * 4];
            for (int row = 0; row < objects.Count; row++)
                Write(bytes, row, GetFloat(source, objects[row], field, cellSize, mapWidth));
            result = result.ReplaceArray("objects", field, 3, 0, bytes);
        }
        result = result.ReplaceProperty("ObjectsCount", objects.Count.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        changed = objects.Count;
        return result;
    }

    private static bool Moved(PbPlacedObject item) => item.Row < 0 ||
        (item.transform.position - item.ImportedPosition).sqrMagnitude > 0.0001f;

    private static bool Rotated(PbPlacedObject item) => item.Row < 0 ||
        Quaternion.Angle(item.transform.rotation, item.ImportedRotation) > 0.01f;

    private static void Split(PbPlacedObject item, float cellSize, int mapWidth,
                              out int x, out int y, out float xf, out float yf)
    {
        Vector3 p = item.transform.position;
        if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z))
            throw new InvalidDataException("Nonfinite object position.");
        float xx = mapWidth - p.x / cellSize;
        float yy = p.z / cellSize;
        x = Mathf.FloorToInt(xx);
        y = Mathf.FloorToInt(yy);
        xf = xx - x;
        yf = yy - y;
    }

    private static int GetInt(PbStorage source, PbPlacedObject item,
                              string field, float cellSize, int mapWidth)
    {
        if (field == "Index") return item.TypeIndex;
        if (item.Row >= 0 && !Moved(item))
            return source.Int("objects", field, item.Row);
        Split(item, cellSize, mapWidth, out int x, out int y, out _, out _);
        return field == "X" ? x : y;
    }

    private static float GetFloat(PbStorage source, PbPlacedObject item,
                                  string field, float cellSize, int mapWidth)
    {
        if (item.Row >= 0 && field != "Angle" && field != "XF" &&
            field != "YF" && field != "Height")
            return source.Float("objects", field, item.Row);
        if (item.Row >= 0 && !Moved(item) && field != "Angle")
            return source.Float("objects", field, item.Row);
        if (field == "Angle")
            return item.Row >= 0 && !Rotated(item)
                ? source.Float("objects", "Angle", item.Row)
                : -Mathf.DeltaAngle(0f, item.transform.eulerAngles.y) * Mathf.Deg2Rad;
        if (field == "AngleX" || field == "AngleY") return 0f;
        if (field == "Scale") return 1f;
        Split(item, cellSize, mapWidth, out _, out _, out float xf, out float yf);
        if (field == "XF") return xf;
        if (field == "YF") return yf;
        return item.transform.position.y - 8f;
    }

    private static void SetPosition(PbStorage storage, PbPlacedObject item, float cellSize, int mapWidth)
    {
        Split(item, cellSize, mapWidth, out int x, out int y, out float xf, out float yf);
        storage.SetInt("objects", "X", item.Row, x);
        storage.SetInt("objects", "Y", item.Row, y);
        storage.SetFloat("objects", "XF", item.Row, xf);
        storage.SetFloat("objects", "YF", item.Row, yf);
        storage.SetFloat("objects", "Height", item.Row, item.transform.position.y - 8f);
    }

    private static void Write(byte[] bytes, int row, int value) =>
        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, row * 4, 4);

    private static void Write(byte[] bytes, int row, float value) =>
        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, row * 4, 4);

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
