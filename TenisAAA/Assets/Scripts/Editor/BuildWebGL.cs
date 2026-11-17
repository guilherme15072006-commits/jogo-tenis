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
        if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded) PatchForPhones(Path.Combine(outDir, "index.html"));
        Debug.Log($"[BuildWebGL] resultado={report.summary.result} tamanho={report.summary.totalSize / (1024 * 1024)} MB erros={report.summary.totalErrors} → {outDir}");
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) EditorApplication.Exit(1);
    }

    /// <summary>Phone tweaks on Unity's default page: lower render resolution (big FPS gain), no browser
    /// scroll/zoom stealing the thumbs, full screen + landscape lock on the first touch, "turn the phone" overlay.</summary>
    static void PatchForPhones(string indexPath)
    {
        string html = File.ReadAllText(indexPath);
        html = html.Replace("// config.devicePixelRatio = 1;", "config.devicePixelRatio = Math.min(window.devicePixelRatio || 1, 1.25);");
        const string css = @"<style>
      html,body{margin:0;height:100%;overflow:hidden;background:#000;overscroll-behavior:none}
      #unity-canvas{touch-action:none;-webkit-user-select:none;user-select:none;-webkit-touch-callout:none}
      #girar{display:none;position:fixed;inset:0;z-index:9;background:#0b1a12;color:#fff;font:600 22px system-ui,sans-serif;
             align-items:center;justify-content:center;text-align:center;padding:24px}
      @media (orientation:portrait){ body.mobile #girar{display:flex} }
      body.mobile #unity-footer{display:none}
    </style>";
        const string overlay = "<div id=\"girar\">Gire o celular para jogar deitado 📱↻</div>";
        const string js = @"
      if (/iPhone|iPad|iPod|Android/i.test(navigator.userAgent)) {
        document.body.classList.add('mobile');
        const goFull = () => {
          const el = document.documentElement;
          const req = el.requestFullscreen || el.webkitRequestFullscreen;
          if (req && !document.fullscreenElement) { try { const p = req.call(el); if (p && p.then) p.then(() => screen.orientation && screen.orientation.lock && screen.orientation.lock('landscape').catch(() => {})).catch(() => {}); } catch (e) {} }
        };
        document.addEventListener('touchend', goFull, { passive: true });
        document.addEventListener('gesturestart', e => e.preventDefault());
      }
    </script>";
        if (!html.Contains("id=\"girar\""))
        {
            html = html.Replace("</head>", css + "\n  </head>");
            html = html.Replace("<body>", "<body>\n    " + overlay);
            int last = html.LastIndexOf("</script>");
            if (last >= 0) html = html.Substring(0, last) + js + html.Substring(last + "</script>".Length);
        }
        File.WriteAllText(indexPath, html);
    }
}
