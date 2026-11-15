using System;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class PbMapSceneCheck
{
    public static void CheckSavedScene()
    {
        EditorSceneManager.OpenScene("Assets/Generated/TrainingPreview.unity");
        PbMapRoot root = UnityEngine.Object.FindFirstObjectByType<PbMapRoot>();
        if (root == null)
            throw new Exception("Saved scene has no map root.");
        MeshFilter mesh = root.GetComponentInChildren<MeshFilter>();
        if (mesh == null || mesh.sharedMesh == null ||
            mesh.sharedMesh.vertexCount != (root.Width + 1) * (root.Height + 1))
            throw new Exception("Saved terrain mesh is missing or has wrong dimensions.");
        PbPlacedObject[] objects = root.GetComponentsInChildren<PbPlacedObject>(true);
        if (objects.Length != 957)
            throw new Exception("Saved Training scene has wrong object count: " + objects.Length);
        Debug.Log($"PB saved scene check: {root.Width}x{root.Height}, " +
                  $"vertices={mesh.sharedMesh.vertexCount}, objects={objects.Length}");
    }
}
