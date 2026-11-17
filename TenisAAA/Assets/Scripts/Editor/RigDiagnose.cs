using Tenis.Core;
using Tenis.Game;
using UnityEditor;
using UnityEngine;

public static class RigDiagnose
{
    public static void Run()
    {
        var prefab = Resources.Load<GameObject>(RiggedAthlete.ResourceName);
        foreach (var a in prefab.GetComponentsInChildren<Animator>(true))
            Debug.Log($"[RigDiag] prefab animator on '{a.name}' avatar={(a.avatar ? a.avatar.name : "NULL")} human={(a.avatar && a.avatar.isHuman)}");
        var root = prefab.transform; string path = root.name;
        for (int i = 0; i < Mathf.Min(4, root.childCount); i++) Debug.Log($"[RigDiag] child {i}: {root.GetChild(i).name}");
        var r = RiggedAthlete.TryCreate("D", new Material(Shader.Find("Standard")), Color.blue);
        r.DtOverride = 0.5f;
        var anim = r.GetComponentInChildren<Animator>();
        Debug.Log($"[RigDiag] instance animator '{anim.name}' avatar={(anim.avatar ? anim.avatar.name : "NULL")} human={anim.isHuman} valid={anim.avatar && anim.avatar.isValid}");
        var arm = anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.RightUpperArm) : null;
        Debug.Log($"[RigDiag] right upper arm = {(arm ? arm.name : "NULL")}");
        var cfg = new TennisConfig(); var motor = new PlayerMotor(cfg.movement, new AthleteProfile(), new Vec3(0, 0, 0), CourtSide.Near);
        var aa = new AthleteAnimation(); aa.BeginServe(0, 1.25);
        Quaternion before = arm ? arm.rotation : Quaternion.identity;
        for (double t = 0; t < 1.3; t += 0.1) { aa.Tick(t, 0.1, motor, new Vec3(10, 0, 1)); r.Now = t; r.Apply(motor.position, aa); }
        Debug.Log($"[RigDiag] arm rotation changed by {(arm ? Quaternion.Angle(before, arm.rotation) : -1):0.0} deg, action={aa.action}");
    }
}
