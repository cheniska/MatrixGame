using UnityEngine;

public sealed class PbMapEntity : MonoBehaviour
{
    public string Table;
    public int Row;
    public int Kind;
    public int Side;
    public Vector3 ImportedPosition;
    public Quaternion ImportedRotation;
    public float ExtraHeight;
    public float ImportedExtraHeight;
}
