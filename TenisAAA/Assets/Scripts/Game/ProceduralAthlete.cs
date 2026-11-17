using Tenis.Core;
using UnityEngine;

namespace Tenis.Game
{
    /// <summary>
    /// Placeholder athlete built from primitives and animated procedurally from <see cref="AthleteAnimation"/>:
    /// running legs/arms with stride frequency, body lean for balance and acceleration, split-step, stroke
    /// phases (backswing → swing → contact → follow-through → recovery), every smash phase and the serve.
    /// When the rigged Mixamo character is ready, a clip-based view reads the same AthleteAnimation.
    /// </summary>
    public class ProceduralAthlete : MonoBehaviour
    {
        Transform _root, _hips, _torso, _head, _shoulderR, _shoulderL, _armR, _armL, _legR, _legL, _racket;
        public bool rightHanded = true;

        // pose source for retargeting onto a real (rigged) character
        public Transform Root => _root;
        public Transform Hips => _hips;
        public Transform Torso => _torso;
        public Transform ArmR => _armR;
        public Transform ArmL => _armL;
        public Transform LegR => _legR;
        public Transform LegL => _legL;
        public void SetVisible(bool v) { foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = v; }

        public static ProceduralAthlete Create(string name, Color shirt, Color shorts, Material baseMat)
        {
            var go = new GameObject(name); var a = go.AddComponent<ProceduralAthlete>(); a.Build(shirt, shorts, baseMat); return a;
        }

        Transform Part(string n, PrimitiveType t, Transform parent, Vector3 pos, Vector3 scale, Color c, Material baseMat)
        {
            var g = GameObject.CreatePrimitive(t); g.name = n; var col = g.GetComponent<Collider>(); if (Application.isPlaying) Destroy(col); else DestroyImmediate(col);
            var r = g.GetComponent<Renderer>(); r.sharedMaterial = new Material(baseMat) { color = c };
            g.transform.SetParent(parent, false); g.transform.localPosition = pos; g.transform.localScale = scale;
            return g.transform;
        }
        Transform Pivot(string n, Transform parent, Vector3 pos) { var t = new GameObject(n).transform; t.SetParent(parent, false); t.localPosition = pos; return t; }

        void Build(Color shirt, Color shorts, Material m)
        {
            var skin = new Color(0.86f, 0.66f, 0.5f);
            _root = Pivot("root", transform, Vector3.zero);
            _hips = Pivot("hips", _root, new Vector3(0, 0.95f, 0));
            _torso = Pivot("torso", _hips, Vector3.zero);
            Part("tronco", PrimitiveType.Capsule, _torso, new Vector3(0, 0.33f, 0), new Vector3(0.38f, 0.33f, 0.24f), shirt, m);
            _head = Part("cabeca", PrimitiveType.Sphere, _torso, new Vector3(0, 0.78f, 0), Vector3.one * 0.22f, skin, m);
            _shoulderR = Pivot("ombroD", _torso, new Vector3(0.23f, 0.58f, 0)); _shoulderL = Pivot("ombroE", _torso, new Vector3(-0.23f, 0.58f, 0));
            _armR = Pivot("bracoD", _shoulderR, Vector3.zero); _armL = Pivot("bracoE", _shoulderL, Vector3.zero);
            Part("b", PrimitiveType.Capsule, _armR, new Vector3(0, -0.3f, 0), new Vector3(0.1f, 0.3f, 0.1f), skin, m);
            Part("b", PrimitiveType.Capsule, _armL, new Vector3(0, -0.3f, 0), new Vector3(0.1f, 0.3f, 0.1f), skin, m);
            _legR = Pivot("pernaD", _hips, new Vector3(0.11f, 0, 0)); _legL = Pivot("pernaE", _hips, new Vector3(-0.11f, 0, 0));
            Part("p", PrimitiveType.Capsule, _legR, new Vector3(0, -0.24f, 0), new Vector3(0.15f, 0.25f, 0.15f), shorts, m);
            Part("p", PrimitiveType.Capsule, _legL, new Vector3(0, -0.24f, 0), new Vector3(0.15f, 0.25f, 0.15f), shorts, m);
            Part("c", PrimitiveType.Capsule, _legR, new Vector3(0, -0.68f, 0), new Vector3(0.11f, 0.24f, 0.11f), skin, m);
            Part("c", PrimitiveType.Capsule, _legL, new Vector3(0, -0.68f, 0), new Vector3(0.11f, 0.24f, 0.11f), skin, m);
            Transform hand = rightHanded ? _armR : _armL;
            _racket = Pivot("raquete", hand, new Vector3(0, -0.6f, 0));
            Part("cabo", PrimitiveType.Cylinder, _racket, new Vector3(0, -0.14f, 0), new Vector3(0.03f, 0.14f, 0.03f), new Color(0.1f, 0.1f, 0.1f), m);
            Part("aro", PrimitiveType.Cylinder, _racket, new Vector3(0, -0.45f, 0), new Vector3(0.27f, 0.008f, 0.33f), new Color(0.15f, 0.15f, 0.18f), m)
                .localRotation = Quaternion.Euler(0, 0, 90);
        }

        static float S(double v) => (float)v;
        /// <summary>Arm pose: raise forward from hanging (deg), then turn around the vertical axis (deg, + = to the right).</summary>
        static Quaternion Arm(float raise, float yaw) => Quaternion.Euler(0, yaw, 0) * Quaternion.Euler(-raise, 0, 0);

        /// <summary>Places and poses the body. yaw = facing in court space, converted to Unity.</summary>
        public void Apply(Vec3 courtPos, AthleteAnimation a)
        {
            transform.position = CourtSpace.ToUnity(courtPos);
            // court yaw (0 = +x) → Unity forward: court +x is Unity +z, court +y is Unity -x
            Vector3 fwd = CourtSpace.ToUnity(new Vec3(System.Math.Cos(a.facingYaw), System.Math.Sin(a.facingYaw), 0)).normalized;
            if (fwd.sqrMagnitude > 0.01f) _root.rotation = Quaternion.LookRotation(fwd, Vector3.up);

            bool planted = a.action == BodyAction.Preparation || a.action == BodyAction.Swing || a.action == BodyAction.Contact
                        || a.action == BodyAction.FollowThrough || a.action == BodyAction.Smash || a.action == BodyAction.Serve;
            float stride = S(a.strideTime), run = S(a.runBlend) * (planted ? 0.2f : 1f);   // feet stay set while hitting
            float legSwing = Mathf.Sin(stride) * 55f * run;
            float bob = Mathf.Abs(Mathf.Sin(stride)) * 0.05f * run;
            _hips.localPosition = new Vector3(0, 0.95f - 0.06f * run + bob, 0);
            _legR.localRotation = Quaternion.Euler(legSwing, 0, 0); _legL.localRotation = Quaternion.Euler(-legSwing, 0, 0);
            _torso.localRotation = Quaternion.Euler(Mathf.Rad2Deg * S(a.leanForward) + 8f * run, 0, -Mathf.Rad2Deg * S(a.leanSide));
            Quaternion armRunR = Quaternion.Euler(-legSwing * 0.8f, 0, 6), armRunL = Quaternion.Euler(legSwing * 0.8f, 0, -6);
            _armR.localRotation = armRunR; _armL.localRotation = armRunL;
            Transform hit = rightHanded ? _armR : _armL, off = rightHanded ? _armL : _armR;
            float side = rightHanded ? 1 : -1;
            float p = S(a.phaseProgress); p = p * p * (3 - 2 * p);   // ease-in/out inside every phase

            switch (a.action)
            {
                case BodyAction.SplitStep:
                    _hips.localPosition += Vector3.up * Mathf.Sin(p * Mathf.PI) * 0.12f;
                    _legR.localRotation = Quaternion.Euler(0, 0, 12); _legL.localRotation = Quaternion.Euler(0, 0, -12); break;
                case BodyAction.Ready:
                    _hips.localPosition = new Vector3(0, 0.88f, 0);
                    hit.localRotation = Quaternion.Euler(-40, 0, 10 * side); off.localRotation = Quaternion.Euler(-40, 0, -10 * side); break;
            }

            bool forehand = a.stroke == Stroke.Forehand || a.stroke == Stroke.ForehandVolley;
            bool volley = a.stroke == Stroke.ForehandVolley || a.stroke == Stroke.BackhandVolley || a.stroke == Stroke.BlockVolley;
            float hs = side * (forehand ? 1 : -1);          // +1 = swing on the right side of the body
            // horizontal swing arc around the body: backswing behind the hitting side → contact in front → finish across
            float backYaw = volley ? 45 : 135, contactYaw = 12, finishYaw = volley ? -15 : -115;
            float backRaise = volley ? 85 : 70, contactRaise = volley ? 85 : 78, finishRaise = volley ? 95 : 125;
            float torsoBack = volley ? 25 : 55, torsoFinish = volley ? -10 : -40;
            bool twoArms = !forehand && a.twoHanded && !volley;
            switch (a.action)
            {
                case BodyAction.Preparation:   // unit turn: shoulders rotate, racket goes back
                    _torso.localRotation *= Quaternion.Euler(0, hs * torsoBack * p, 0);
                    hit.localRotation = Arm(Mathf.Lerp(40, backRaise, p), hs * Mathf.Lerp(20, backYaw, p));
                    off.localRotation = twoArms ? hit.localRotation : Arm(Mathf.Lerp(40, 80, p), hs * Mathf.Lerp(0, 60, p)); break;
                case BodyAction.Swing:         // hips/shoulders unwind, racket accelerates forward
                    _torso.localRotation *= Quaternion.Euler(0, hs * Mathf.Lerp(torsoBack, 0, p), 0);
                    hit.localRotation = Arm(Mathf.Lerp(backRaise, contactRaise, p), hs * Mathf.Lerp(backYaw, contactYaw, p));
                    off.localRotation = twoArms ? hit.localRotation : Arm(60, -hs * 20 * p); break;
                case BodyAction.Contact:
                    hit.localRotation = Arm(contactRaise, hs * contactYaw);
                    off.localRotation = twoArms ? hit.localRotation : Arm(50, -hs * 30); break;
                case BodyAction.FollowThrough: // racket finishes high across the body
                    _torso.localRotation *= Quaternion.Euler(0, hs * torsoFinish * p, 0);
                    hit.localRotation = Arm(Mathf.Lerp(contactRaise, finishRaise, p), hs * Mathf.Lerp(contactYaw, finishYaw, p));
                    off.localRotation = twoArms ? hit.localRotation : Arm(40, -hs * 40); break;
                case BodyAction.Recovery:
                    hit.localRotation = Quaternion.Slerp(Arm(finishRaise, hs * finishYaw), Arm(40, side * 10), p); break;
                case BodyAction.Smash: PoseSmash(a, hit, off, side, p); break;
                case BodyAction.Serve: PoseServe(a, hit, off, side, p); break;
            }
        }

        void PoseSmash(AthleteAnimation a, Transform hit, Transform off, float side, float p)
        {
            switch (a.smash)
            {
                case SmashPhase.TurnSideways: _torso.localRotation *= Quaternion.Euler(0, -side * 80 * p, 0); break;                // body preparation
                case SmashPhase.FootPositioning: _torso.localRotation *= Quaternion.Euler(0, -side * 80, 0);                          // feet set
                    _legR.localRotation = Quaternion.Euler(-25, 0, 0); _legL.localRotation = Quaternion.Euler(20, 0, 0); break;
                case SmashPhase.ArmRaise: _torso.localRotation *= Quaternion.Euler(-12, -side * 80, 0);                              // non-hitting arm points at the ball
                    off.localRotation = Quaternion.Euler(-165, 0, 0); hit.localRotation = Arm(200, side * 35); break;     // racket drops behind the head
                case SmashPhase.AerialContact: _hips.localPosition += Vector3.up * 0.25f;                                           // jump + contact high
                    hit.localRotation = Quaternion.Euler(-180, 0, 0); off.localRotation = Quaternion.Euler(-40, 0, 0); break;
                case SmashPhase.EnergyTransfer: _hips.localPosition += Vector3.up * 0.25f * (1 - p);                                // trunk flexion drives the racket down
                    _torso.localRotation *= Quaternion.Euler(25 * p, side * 40 * p, 0); hit.localRotation = Quaternion.Euler(-180 + 130 * p, 0, -side * 20); break;
                case SmashPhase.Finish: _torso.localRotation *= Quaternion.Euler(25, side * 40, 0); hit.localRotation = Quaternion.Euler(-30, 0, -side * 60); break;
                case SmashPhase.Recovery: hit.localRotation = Quaternion.Slerp(Quaternion.Euler(-30, 0, -side * 60), Quaternion.Euler(-40, 0, 10 * side), p); break;
            }
        }

        void PoseServe(AthleteAnimation a, Transform hit, Transform off, float side, float p)
        {
            _torso.localRotation *= Quaternion.Euler(0, -side * 70, 0);
            switch (a.serve)
            {
                case ServeBodyPhase.Stance: hit.localRotation = Quaternion.Euler(-30, 0, 0); off.localRotation = Quaternion.Euler(-30, 0, 0); break;
                case ServeBodyPhase.Toss: off.localRotation = Quaternion.Euler(-30 - 140 * p, 0, 0); hit.localRotation = Quaternion.Euler(-30 + 40 * p, 0, side * 60 * p); break;
                case ServeBodyPhase.Trophy: off.localRotation = Quaternion.Euler(-170, 0, 0); hit.localRotation = Quaternion.Euler(-110, 0, side * 80);
                    _legR.localRotation = _legL.localRotation = Quaternion.Euler(-15, 0, 0); _hips.localPosition -= Vector3.up * 0.1f * p; break;
                case ServeBodyPhase.Drop: hit.localRotation = Quaternion.Euler(-110 + 40 * p, 0, side * 80 + 60 * p); break;
                case ServeBodyPhase.Contact: _hips.localPosition += Vector3.up * 0.15f; hit.localRotation = Quaternion.Euler(-185, 0, 0); break;
                case ServeBodyPhase.FollowThrough: _torso.localRotation *= Quaternion.Euler(20 * p, side * 90 * p, 0); hit.localRotation = Quaternion.Euler(-185 + 160 * p, 0, -side * 70 * p); break;
                case ServeBodyPhase.Recovery: hit.localRotation = Quaternion.Euler(-40, 0, 10 * side); break;
            }
        }
    }
}
