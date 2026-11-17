using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Builds the browser version (plays on phones with touch: left thumb joystick, right thumb swings).
/// Run: Unity -batchmode -projectPath TenisAAA -executeMethod BuildWebGL.Run -quit
/// Output: ../docs  (served by GitHub Pages from the /docs folder).</summary>
public static class BuildWebGL
{
    const string ScenePath = "Assets/Scenes/Partida.unity";

    public static void Run()
    {
        // a scene is required for a player build; the match builds itself at runtime (MatchPrototype)
        if (!File.Exists(ScenePath))
        {
            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

        PlayerSettings.companyName = "JogoTenis";
        PlayerSettings.productName = "Jogo de Tênis";
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        PlayerSettings.runInBackground = false;
        // GitHub Pages does not send Content-Encoding headers: compressed build + JS decompression fallback
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
        PlayerSettings.WebGL.decompressionFallback = true;
        PlayerSettings.WebGL.dataCaching = true;
        PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
        PlayerSettings.WebGL.memorySize = 512;
        PlayerSettings.WebGL.template = "APPLICATION:Default";
        // lighter shadows for phones
        QualitySettings.shadowResolution = ShadowResolution.Medium;
        QualitySettings.shadowCascades = 2;

        string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs"));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = outDir,
            target = BuildTarget.WebGL,
            options = BuildOptions.None
        });
        Debug.Log($"[BuildWebGL] resultado={report.summary.result} tamanho={report.summary.totalSize / (1024 * 1024)} MB erros={report.summary.totalErrors} → {outDir}");
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) EditorApplication.Exit(1);
    }
}
