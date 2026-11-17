using UnityEditor;
using UnityEngine;

public static class AnimDiagnose
{
    public static void Run()
    {
        foreach (var path in new[] { "Assets/Resources/JogadorTripo.fbx", "Assets/Resources/Animacoes/saque.fbx" })
        {
            var imp = (ModelImporter)AssetImporter.GetAtPath(path);
            Debug.Log($"[Diag] {path} type={imp.animationType}");
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (o is Avatar av) Debug.Log($"[Diag]   avatar {av.name} valid={av.isValid} human={av.isHuman}");
                if (o is AnimationClip c && !c.name.StartsWith("__preview"))
                    Debug.Log($"[Diag]   clip {c.name} len={c.length:0.00} human={c.humanMotion} bindings={AnimationUtility.GetCurveBindings(c).Length}");
            }
            if (imp.animationType == ModelImporterAnimationType.Human)
                foreach (var hb in imp.humanDescription.human) if (hb.humanName.Contains("Hips") || hb.humanName.Contains("UpperArm")) Debug.Log($"[Diag]   map {hb.humanName} -> {hb.boneName}");
        }
    }
}
