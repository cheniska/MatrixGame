using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class PbStandaloneBuild
{
    public static void BuildWindows()
    {
        string destination = Environment.GetEnvironmentVariable("PB_STANDALONE_BUILD");
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("Set PB_STANDALONE_BUILD to an output folder.");
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(destination);

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var app = new GameObject("PB Map Editor");
        app.AddComponent<PbStandaloneApp>();
        Directory.CreateDirectory("Assets/Generated");
        Directory.CreateDirectory("Assets/Generated/Resources");
        CreateMaterial("PBMarker", new Color(0.85f, 0.30f, 0.16f));
        CreateMaterial("PBSelected", Color.yellow);
        CreateMaterial("PBTerrain", new Color(0.36f, 0.49f, 0.34f));
        CreateMaterial("PBModel", Color.white);
        AssetDatabase.SaveAssets();
        const string scenePath = "Assets/Generated/PBStandalone.unity";
        EditorSceneManager.SaveScene(scene, scenePath);

        var options = new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = Path.Combine(destination, "PBMapEditor.exe"),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
            throw new Exception("Windows build failed: " + report.summary.result +
                                ", errors: " + report.summary.totalErrors);
        Debug.Log("PB standalone build succeeded: " + options.locationPathName);
    }

    private static void CreateMaterial(string name, Color color)
    {
        string path = "Assets/Generated/Resources/" + name + ".mat";
        if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;
        Shader shader = Shader.Find("Standard");
        if (shader == null) throw new Exception("Unity Standard shader is missing.");
        var material = new Material(shader) { color = color };
        AssetDatabase.CreateAsset(material, path);
    }
}
