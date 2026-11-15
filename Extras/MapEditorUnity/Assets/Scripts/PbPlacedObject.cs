using UnityEngine;

public sealed class PbPlacedObject : MonoBehaviour
{
    public int Row;
    public int TypeIndex;
    public Vector3 ImportedPosition;
    public Quaternion ImportedRotation;
    public float AngleZ;
    public float AngleX;
    public float AngleY;

    public static Quaternion Rotation(float z, float x, float y) =>
        Quaternion.AngleAxis(-z * Mathf.Rad2Deg, Vector3.up) *
        Quaternion.AngleAxis(y * Mathf.Rad2Deg, Vector3.forward) *
        Quaternion.AngleAxis(-x * Mathf.Rad2Deg, Vector3.right);

    public void Rotate(float z, float x, float y)
    {
        AngleZ += z * Mathf.Deg2Rad;
        AngleX += x * Mathf.Deg2Rad;
        AngleY += y * Mathf.Deg2Rad;
        transform.rotation = Rotation(AngleZ, AngleX, AngleY);
    }
}

