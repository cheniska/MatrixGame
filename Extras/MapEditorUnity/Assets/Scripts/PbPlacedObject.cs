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
    public float Scale = 1f;
    public float ImportedScale = 1f;

    public static Quaternion Rotation(float z, float x, float y) =>
        PbCoordinates.Rotation(z, x, y);

    public void Rotate(float z, float x, float y)
    {
        AngleZ += z * Mathf.Deg2Rad;
        AngleX += x * Mathf.Deg2Rad;
        AngleY += y * Mathf.Deg2Rad;
        transform.rotation = Rotation(AngleZ, AngleX, AngleY);
    }

    public void SetScale(float value)
    {
        Scale = Mathf.Clamp(value, 0.05f, 20f);
        Transform visual = transform.Find("Visual");
        if (visual != null) visual.localScale = Vector3.one * Scale;
    }
}

