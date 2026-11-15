using UnityEngine;

// Game: X/Y ground, Z up, Direct3D row vectors. Editor: X/Z ground, Y up.
// The map is mirrored on X: (x, y, z) -> (mapWidth - x, z, y).
// Direction vectors omit the map translation: (x, y, z) -> (-x, z, y).
// This basis has determinant +1, so model triangle winding is preserved.
public static class PbCoordinates
{
    public static Vector3 Direction(float x, float y, float z) =>
        new Vector3(-x, z, y);

    public static float MapX(float cellX, int mapWidth, float cellSize) =>
        (mapWidth - cellX) * cellSize;

    public static float CellX(float worldX, int mapWidth, float cellSize) =>
        mapWidth - worldX / cellSize;

    // D3DX Rx * Ry * Rz (row vectors) maps to Unity Ry * Rz * Rx.
    public static Quaternion Rotation(float z, float x, float y) =>
        Quaternion.AngleAxis(z * Mathf.Rad2Deg, Vector3.up) *
        Quaternion.AngleAxis(y * Mathf.Rad2Deg, Vector3.forward) *
        Quaternion.AngleAxis(-x * Mathf.Rad2Deg, Vector3.right);
}
