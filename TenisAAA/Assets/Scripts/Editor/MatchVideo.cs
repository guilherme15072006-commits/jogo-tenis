using System.IO;
using Tenis.Core;
using Tenis.Game;
using UnityEditor;
using UnityEngine;

/// <summary>Records a self-playing match (bot vs AI, real engine, real animations, broadcast camera) to PNG frames.
/// Run: Unity -batchmode -projectPath TenisAAA -executeMethod MatchVideo.Run -quit   (frames → ../video_partida/)</summary>
public static class MatchVideo
{
    static readonly System.Collections.Generic.Dictionary<SkinnedMeshRenderer, MeshFilter> _baked = new System.Collections.Generic.Dictionary<SkinnedMeshRenderer, MeshFilter>();
    static void BakeSkins()
    {
        foreach (var smr in Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (!_baked.TryGetValue(smr, out var mf))
            {
                var g = new GameObject(smr.name + "_baked"); mf = g.AddComponent<MeshFilter>();
                g.AddComponent<MeshRenderer>().sharedMaterials = smr.sharedMaterials; mf.sharedMesh = new Mesh();
                _baked[smr] = mf;
            }
            smr.BakeMesh(mf.sharedMesh, true);
            mf.transform.SetPositionAndRotation(smr.transform.position, smr.transform.rotation);
            smr.enabled = false;
        }
    }

    public static void Run()
    {
        string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../video_partida"));
        if (Directory.Exists(outDir)) foreach (var f in Directory.GetFiles(outDir, "*.png")) File.Delete(f);
        Directory.CreateDirectory(outDir);
        var go = new GameObject("[Tenis] MatchVideo");
        var mp = go.AddComponent<MatchPrototype>();
        mp.InitManual(424242);
        var eng = mp.Engine; var cam = mp.BroadcastCamera;
        var rt = new RenderTexture(1280, 720, 24) { antiAliasing = 4 }; cam.targetTexture = rt;
        var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        // second camera close to the local player, only to review the animation
        string closeDir = outDir + "_perto"; Directory.CreateDirectory(closeDir);
        foreach (var f in Directory.GetFiles(closeDir, "*.png")) File.Delete(f);
        var close = new GameObject("close").AddComponent<Camera>(); close.fieldOfView = 32;
        close.clearFlags = CameraClearFlags.Skybox;
        var rt2 = new RenderTexture(640, 640, 24) { antiAliasing = 4 }; close.targetTexture = rt2;
        var tex2 = new Texture2D(640, 640, TextureFormat.RGB24, false);
        var bot = new DetRandom(11); double planTime = -1; bool swung = false;
        var log = new System.Text.StringBuilder("t;fase;vel0;acao0;rali;plano0;msg" + System.Environment.NewLine); int lastMsg = 0;
        const float dt = 1f / 30f; int seconds = 40, frames = (int)(seconds / dt), written = 0;
        for (int i = 0; i < frames; i++)
        {
            // bot: swings at the right time (movement comes from the engine's assist), serves when it is its turn
            if (eng.HasPlan(0))
            {
                var c = eng.PlanFor(0);
                if (System.Math.Abs(c.contactTime - planTime) > 1e-6) { planTime = c.contactTime; swung = false; }
                if (!swung && eng.now >= c.contactTime - 0.32) eng.OnLocalGestureStarted();
                if (!swung && eng.now >= c.contactTime - 0.01 + bot.NextGaussian() * 0.02)
                {
                    swung = true;
                    var shape = bot.NextDouble() < 0.7 ? GestureShape.DiagonalUp : GestureShape.DiagonalDown;
                    eng.InjectLocalGesture(GestureFactory.Make(shape, bot.NextDouble() * 1.6 - 0.8, 0.6, eng.now), eng.now - 0.35);
                }
            }
            if (eng.phase == MatchEngine.Phase.PreServe && eng.LocalServing && bot.NextDouble() < 0.03) eng.InjectLocalGesture(default, eng.now);
            if (eng.phase == MatchEngine.Phase.Toss && eng.LocalServing && eng.ball.position.z > 2.95)
                eng.InjectLocalGesture(GestureFactory.Make(GestureShape.VerticalDown, bot.NextDouble() * 2 - 1, 0.7, eng.now), eng.now - 0.15);
            mp.StepManual(dt, 0, 0);
            string msg = eng.messages.Count > lastMsg ? eng.messages[eng.messages.Count - 1].text : ""; lastMsg = eng.messages.Count;
            log.Append($"{eng.now:0.00};{eng.phase};{eng.players[0].Speed:0.00};{eng.anims[0].action};{eng.rallyLength};{eng.HasPlan(0)};{msg}" + System.Environment.NewLine);
            BakeSkins();   // outside Play mode Unity does not run skinning: bake every skinned part for this frame
            cam.Render();
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); tex.Apply(); RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(outDir, $"f_{written:00000}.png"), tex.EncodeToPNG());
            var pp = CourtSpace.ToUnity(eng.players[0].position);
            close.transform.position = pp + new Vector3(4.2f, 2.0f, -4.6f); close.transform.LookAt(pp + Vector3.up * 1.0f);
            close.Render();
            RenderTexture.active = rt2; tex2.ReadPixels(new Rect(0, 0, 640, 640), 0, 0); tex2.Apply(); RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(closeDir, $"f_{written++:00000}.png"), tex2.EncodeToPNG());
        }
        File.WriteAllText(Path.Combine(outDir, "../video_partida_log.csv"), log.ToString());
        Debug.Log($"[MatchVideo] {written} quadros, placar {eng.score.Describe()}, último: {(eng.messages.Count > 0 ? eng.messages[eng.messages.Count - 1].text : "")}");
    }
}
