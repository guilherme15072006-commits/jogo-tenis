using System.IO;
using Tenis.Core;
using Tenis.Game;
using UnityEditor;
using UnityEngine;

/// <summary>Batch tool: renders the procedural athlete in key poses to PNGs so the animation can be reviewed
/// without opening the editor UI. Run: Unity -batchmode -projectPath TenisAAA -executeMethod AthleteCapture.Run -quit</summary>
public static class AthleteCapture
{
    public static void Run() => Capture(false);
    public static void RunRigged() => Capture(true);

    static readonly System.Collections.Generic.Dictionary<SkinnedMeshRenderer, MeshFilter> _baked = new System.Collections.Generic.Dictionary<SkinnedMeshRenderer, MeshFilter>();
    static void BakeSkins(Transform root)
    {
        foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!_baked.TryGetValue(smr, out var mf))
            {
                var go = new GameObject(smr.name + "_baked"); mf = go.AddComponent<MeshFilter>();
                go.AddComponent<MeshRenderer>().sharedMaterials = smr.sharedMaterials; mf.sharedMesh = new Mesh();
                _baked[smr] = mf;
                foreach (var m in smr.sharedMaterials) Debug.Log($"[AthleteCapture] material {m.name} shader={m.shader.name} tex={(m.mainTexture ? m.mainTexture.name : "NONE")} color={m.color}");
            }
            smr.BakeMesh(mf.sharedMesh, true);
            mf.transform.SetPositionAndRotation(smr.transform.position, smr.transform.rotation);
            smr.enabled = false;
        }
    }

    static double _t;
    static void Capture(bool rigged)
    {
        string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, rigged ? "../../captures_tripo" : "../../captures"));
        Directory.CreateDirectory(outDir);
        var sh = Shader.Find("Standard");
        var mat = new Material(sh);
        var light = new GameObject("L").AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.3f;
        light.transform.rotation = Quaternion.Euler(45, -40, 0);
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane); floor.transform.localScale = Vector3.one * 2;
        floor.GetComponent<Renderer>().sharedMaterial = new Material(mat) { color = new Color(0.72f, 0.3f, 0.12f) };
        var cam = new GameObject("C").AddComponent<Camera>(); cam.fieldOfView = 35;
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.12f, 0.14f, 0.16f);
        var rt = new RenderTexture(480, 480, 24); cam.targetTexture = rt;

        System.Action<Vec3, AthleteAnimation> apply; Transform anchor;
        if (rigged) { var r = RiggedAthlete.TryCreate("A", mat, Color.blue); r.DtOverride = 0.5f; apply = (pos, an) => { r.Now = _t; r.Apply(pos, an); }; anchor = r.transform; }
        else { var pa = ProceduralAthlete.Create("A", new Color(0.2f, 0.45f, 0.95f), Color.white, mat); apply = pa.Apply; anchor = pa.transform; }
        var cfg = new TennisConfig();
        var motor = new PlayerMotor(cfg.movement, new AthleteProfile(), new Vec3(0, 0, 0), CourtSide.Near);
        var anim = new AthleteAnimation();
        Vec3 lookAt = new Vec3(10, 0, 1);

        void Shot(string name, Vector3 camOffset)
        {
            apply(motor.position, anim);
            Vector3 p = anchor.position;
            BakeSkins(anchor);   // outside Play mode Unity does not run skinning, so bake it for the capture
            cam.transform.position = p + camOffset; cam.transform.LookAt(p + Vector3.up * 1.0f);
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply();
            File.WriteAllBytes(Path.Combine(outDir, name + ".png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }
        Vector3 side = new Vector3(4.2f, 1.2f, 0), front = new Vector3(0, 1.2f, 4.2f), back34 = new Vector3(3, 1.4f, 3);

        // 1. idle
        double t = 0; anim.Tick(t, 0.016, motor, lookAt); _t = t; Shot("01_parado", back34);
        // 2. running (several stride moments)
        for (int i = 0; i < 50; i++) { motor.Tick(1 / 60.0, 1, 0); t += 1 / 60.0; anim.Tick(t, 1 / 60.0, motor, lookAt); }
        Shot("02_correndo_a", side);
        for (int i = 0; i < 7; i++) { motor.Tick(1 / 60.0, 1, 0); t += 1 / 60.0; anim.Tick(t, 1 / 60.0, motor, lookAt); }
        Shot("03_correndo_b", side);
        // 3. hard reversal → balance lost
        for (int i = 0; i < 10; i++) { motor.Tick(1 / 60.0, -1, 0); t += 1 / 60.0; anim.Tick(t, 1 / 60.0, motor, lookAt); }
        Shot("04_virada_equilibrio", front);
        // stop
        for (int i = 0; i < 90; i++) { motor.Tick(1 / 60.0, 0, 0); t += 1 / 60.0; anim.Tick(t, 1 / 60.0, motor, lookAt); }
        // 4. forehand phases
        double c = t + 0.6; anim.BeginPreparation(t, c, Stroke.Forehand, false, 1.0);
        foreach (var (dtp, n) in new[] { (0.30, "05_fh_preparacao"), (0.55, "06_fh_batida"), (0.6, "07_fh_contato"), (0.85, "08_fh_finalizacao") })
        { anim.Tick(t + dtp, 0.016, motor, lookAt); _t = t + dtp; Shot(n, back34); }
        t += 2;
        // 5. two-handed backhand
        c = t + 0.6; anim.BeginPreparation(t, c, Stroke.Backhand, true, 1.0);
        foreach (var (dtp, n) in new[] { (0.35, "09_bh2_preparacao"), (0.6, "10_bh2_contato"), (0.85, "11_bh2_finalizacao") })
        { anim.Tick(t + dtp, 0.016, motor, lookAt); _t = t + dtp; Shot(n, back34); }
        t += 2;
        // 6. smash phases
        c = t + 1.0; anim.BeginPreparation(t, c, Stroke.Smash, false, 2.8);
        var seen = new System.Collections.Generic.HashSet<SmashPhase>(); int k = 12;
        for (double x = 0; x < 1.8; x += 0.01)
        {
            anim.Tick(t + x, 0.01, motor, lookAt); _t = t + x;
            if (anim.smash != SmashPhase.None && seen.Add(anim.smash)) Shot($"{k++:00}_smash_{anim.smash}", side);
        }
        t += 3;
        // 7. serve phases
        anim.BeginServe(t, t + 0.5);
        var seenS = new System.Collections.Generic.HashSet<ServeBodyPhase>();
        for (double x = 0; x < 1.4; x += 0.01)
        {
            anim.Tick(t + x, 0.01, motor, lookAt); _t = t + x;
            if (anim.serve != ServeBodyPhase.None && seenS.Add(anim.serve)) Shot($"{k++:00}_saque_{anim.serve}", side);
        }
        RenderTexture.active = null;
        Debug.Log("[AthleteCapture] wrote " + outDir);
    }
}
