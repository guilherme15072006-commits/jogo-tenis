using System.Collections.Generic;
using Tenis.Core;
using UnityEngine;

namespace Tenis.Game
{
    /// <summary>
    /// Shows the user's Tripo character (rigged in Blender, Assets/Resources/JogadorTripo.fbx) and animates it by
    /// retargeting the procedural pose every frame: an invisible <see cref="ProceduralAthlete"/> computes the pose
    /// from <see cref="AthleteAnimation"/>, and its body yaw, hip height, torso rotation and limb directions are
    /// copied onto the real bones. Works for any rig that uses the same bone names (Hips, Spine, Chest,
    /// RightUpperArm, RightLowerArm, RightUpperLeg, ...), so a Mixamo/mocap character can replace it later.
    /// </summary>
    public class RiggedAthlete : MonoBehaviour
    {
        public const string ResourceName = "JogadorTripo";
        ProceduralAthlete _src;
        Transform _model, _hips, _spine, _chest, _uaR, _laR, _uaL, _laL, _ulR, _llR, _ulL, _llL, _footR, _footL;
        readonly Dictionary<Transform, Quaternion> _bindLocal = new Dictionary<Transform, Quaternion>();
        Vector3 _hipsBindLocal; Quaternion _modelBindRot;
        ClipAthlete _clips;
        // smoothed pose (removes the snapping between procedural phases)
        Vector3 _sArmR, _sArmL, _sLegR, _sLegL; Quaternion _sTorso = Quaternion.identity; float _sHip; bool _sInit;
        double _now;
        public double Now { set => _now = value; }
        /// <summary>Fixed step for tools that pose the character outside Play mode (where Time.deltaTime is 0).</summary>
        public float DtOverride = -1;

        public static RiggedAthlete TryCreate(string name, Material baseMat, Color fallbackShirt)
        {
            var prefab = Resources.Load<GameObject>(ResourceName);
            if (prefab == null) return null;
            var go = new GameObject(name);
            var r = go.AddComponent<RiggedAthlete>();
            r._src = ProceduralAthlete.Create(name + "_pose", fallbackShirt, Color.white, baseMat);
            r._src.SetVisible(false);
            r._model = Instantiate(prefab, go.transform).transform;
            r._model.name = "modelo";
            ApplyTripoTextures(r._model);
            if (!r.Bind()) { Destroy(go); Destroy(r._src.gameObject); return null; }
            var anim = r._model.GetComponentInChildren<Animator>();
            if (anim == null) anim = r._model.gameObject.AddComponent<Animator>();
            anim.applyRootMotion = false; anim.runtimeAnimatorController = null;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;   // never pause the athlete (33 skinned parts confuse visibility culling)
            r._clips = new ClipAthlete(anim);
            return r;
        }

        /// <summary>Tripo exports one material per part ("tripo_part_N_material"); its base-colour texture is
        /// Resources/JogadorTripo_tex/part_N.png. Unity does not pick the embedded FBX textures up, so link them here.</summary>
        static void ApplyTripoTextures(Transform model)
        {
            var cache = new Dictionary<Material, Material>();
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i]; if (m == null) continue;
                    if (!cache.TryGetValue(m, out var fixedMat))
                    {
                        fixedMat = m;
                        var match = System.Text.RegularExpressions.Regex.Match(m.name, @"part_(\d+)");
                        var tex = match.Success ? Resources.Load<Texture2D>("JogadorTripo_tex/part_" + match.Groups[1].Value) : null;
                        var nrm = match.Success ? Resources.Load<Texture2D>("JogadorTripo_tex/part_" + match.Groups[1].Value + "_n") : null;
                        if (tex != null)
                        {
                            fixedMat = new Material(m) { mainTexture = tex, color = Color.white };
                            if (nrm != null && fixedMat.HasProperty("_BumpMap"))
                            {
                                fixedMat.SetTexture("_BumpMap", nrm); fixedMat.SetFloat("_BumpScale", 1f); fixedMat.EnableKeyword("_NORMALMAP");
                            }
                            if (fixedMat.HasProperty("_Glossiness")) fixedMat.SetFloat("_Glossiness", 0.25f);
                            if (fixedMat.HasProperty("_Metallic")) fixedMat.SetFloat("_Metallic", 0f);
                        }
                        cache[m] = fixedMat;
                    }
                    mats[i] = fixedMat;
                }
                r.sharedMaterials = mats;
            }
        }

        Transform Find(string bone)
        {
            // tolerant match: "RightUpperArm", "Right_UpperArm", "mixamorig:RightUpperArm"...
            string want = bone.Replace("_", "").ToLowerInvariant();
            foreach (var t in _model.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.Replace("_", "").ToLowerInvariant();
                int c = n.LastIndexOf(':'); if (c >= 0) n = n.Substring(c + 1);
                if (n == want) return t;
            }
            return null;
        }

        bool Bind()
        {
            _hips = Find("Hips"); _spine = Find("Spine"); _chest = Find("Chest");
            _uaR = Find("RightUpperArm"); _laR = Find("RightLowerArm"); _uaL = Find("LeftUpperArm"); _laL = Find("LeftLowerArm");
            _ulR = Find("RightUpperLeg"); _llR = Find("RightLowerLeg"); _ulL = Find("LeftUpperLeg"); _llL = Find("LeftLowerLeg");
            _footR = Find("RightFoot"); _footL = Find("LeftFoot");
            var all = new[] { _hips, _spine, _chest, _uaR, _laR, _uaL, _laL, _ulR, _llR, _ulL, _llL, _footR, _footL };
            foreach (var t in all) if (t == null) { Debug.LogWarning("[Tenis] bone missing in " + ResourceName); return false; }
            foreach (var t in _model.GetComponentsInChildren<Transform>(true)) _bindLocal[t] = t.localRotation;
            _hipsBindLocal = _hips.localPosition; _modelBindRot = _model.localRotation;
            return true;
        }

        void ResetToBind()
        {
            foreach (var kv in _bindLocal) kv.Key.localRotation = kv.Value;
            _hips.localPosition = _hipsBindLocal;
        }

        /// <summary>Points <paramref name="bone"/> (whose child is <paramref name="child"/>) along a world direction.</summary>
        static void Aim(Transform bone, Transform child, Vector3 worldDir)
        {
            Vector3 cur = child.position - bone.position;
            if (cur.sqrMagnitude < 1e-8 || worldDir.sqrMagnitude < 1e-8) return;
            bone.rotation = Quaternion.FromToRotation(cur, worldDir) * bone.rotation;
        }

        public void Apply(Vec3 courtPos, AthleteAnimation a)
        {
            _src.Apply(courtPos, a);                                  // computes the pose on the hidden rig
            transform.position = _src.transform.position;
            float dt = DtOverride > 0 ? DtOverride : Mathf.Max(1e-4f, Time.deltaTime);
            if (_clips != null && _clips.ClipCount > 0 && _clips.Covers(a))
            {   // captured animation drives the body; the game still owns position and facing
                _model.rotation = _src.Root.rotation * _modelBindRot;
                _clips.Evaluate(a, _now, dt);
                // additive lean: forward when accelerating, back when braking, into the turns (not during strokes)
                bool stroke = a.action == BodyAction.Preparation || a.action == BodyAction.Swing || a.action == BodyAction.Contact
                           || a.action == BodyAction.FollowThrough || a.action == BodyAction.Serve || a.action == BodyAction.Smash;
                float leanK = stroke ? 0.3f : 1f;
                Vector3 right = _src.Root.right, fwd = _src.Root.forward;
                Quaternion lean = Quaternion.AngleAxis((float)(a.AccelLean * Mathf.Rad2Deg) * leanK, right)
                                * Quaternion.AngleAxis((float)(-a.TurnLean * Mathf.Rad2Deg) * leanK, fwd);
                _spine.rotation = lean * _spine.rotation;
                _sInit = false;
                return;
            }
            ResetToBind();
            // 1) body yaw: the character model faces +Z in its bind pose; follow the pose root
            _model.rotation = _src.Root.rotation * _modelBindRot;
            // 2) hips height (crouch, split-step, jumps)
            float dy = _src.Hips.localPosition.y - 0.95f;
            // 3) torso: split the procedural torso rotation between Spine and Chest
            Quaternion torsoRel = Quaternion.Inverse(_src.Root.rotation) * _src.Torso.rotation;
            Vector3 armR = _src.ArmR.rotation * Vector3.down, armL = _src.ArmL.rotation * Vector3.down;
            Vector3 legR = _src.LegR.rotation * Vector3.down, legL = _src.LegL.rotation * Vector3.down;
            // critically-damped smoothing (~50 ms) so phase changes flow instead of snapping
            float k = 1 - Mathf.Exp(-dt / 0.05f);
            if (!_sInit) { _sArmR = armR; _sArmL = armL; _sLegR = legR; _sLegL = legL; _sTorso = torsoRel; _sHip = dy; _sInit = true; }
            _sArmR = Vector3.Slerp(_sArmR, armR, k); _sArmL = Vector3.Slerp(_sArmL, armL, k);
            _sLegR = Vector3.Slerp(_sLegR, legR, k); _sLegL = Vector3.Slerp(_sLegL, legL, k);
            _sTorso = Quaternion.Slerp(_sTorso, torsoRel, k); _sHip = Mathf.Lerp(_sHip, dy, k);
            armR = _sArmR; armL = _sArmL; legR = _sLegR; legL = _sLegL; torsoRel = _sTorso;
            _hips.position += Vector3.up * _sHip;
            Quaternion half = Quaternion.Slerp(Quaternion.identity, torsoRel, 0.5f);
            Quaternion rootRot = _src.Root.rotation;
            _spine.rotation = rootRot * half * Quaternion.Inverse(rootRot) * _spine.rotation;
            _chest.rotation = rootRot * half * Quaternion.Inverse(rootRot) * _chest.rotation;
            // 4) limbs follow the pose directions (arm/leg pivots hang along -Y in the procedural rig)
            Aim(_uaR, _laR, armR); Aim(_laR, _laR.GetChild(0), Vector3.Slerp(armR, Vector3.up, 0.08f));
            Aim(_uaL, _laL, armL); Aim(_laL, _laL.GetChild(0), Vector3.Slerp(armL, Vector3.up, 0.08f));
            Aim(_ulR, _llR, legR); Aim(_llR, _footR, Vector3.Slerp(legR, Vector3.down, 0.35f));
            Aim(_ulL, _llL, legL); Aim(_llL, _footL, Vector3.Slerp(legL, Vector3.down, 0.35f));
        }

        void OnDestroy() { if (_src != null) Destroy(_src.gameObject); _clips?.Dispose(); }
    }
}
