using System.Collections.Generic;
using Tenis.Core;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Tenis.Game
{
    /// <summary>
    /// Plays captured humanoid animations (video automation, DeepMotion, Mixamo, mocap) on the rigged character,
    /// driven by the game's <see cref="AthleteAnimation"/> state.
    ///
    /// Graph:  layer 0 (full body) = locomotion blend [idle | run fwd | run back | shuffle L | shuffle R] + action
    ///         layer 1 (upper body mask) = the same action clip, used while the athlete is still moving,
    ///         so the legs keep running/adjusting while the torso and arms swing (a "running forehand").
    /// Locomotion clips are blended by the direction of motion relative to the body and their playback
    /// rate follows the real ground speed (no foot sliding). Strokes are time-warped so the contact frame
    /// happens exactly when the game resolves the hit.
    /// Missing clips fall back gracefully (e.g. no "corrida_frente" → the generic "corrida" is used).
    /// </summary>
    public class ClipAthlete
    {
        public static readonly Dictionary<string, string[]> ClipNames = new Dictionary<string, string[]>
        {
            { "espera", new[] { "espera", "idle" } },
            { "corrida", new[] { "corrida", "run" } },
            { "corrida_frente", new[] { "corrida_frente", "running", "run_forward", "sprint" } },
            { "corrida_tras", new[] { "corrida_tras", "running_backward", "run_backward", "backpedal" } },
            { "passo_esq", new[] { "passo_esq", "left_strafe", "strafe_left", "shuffle_left" } },
            { "passo_dir", new[] { "passo_dir", "right_strafe", "strafe_right", "shuffle_right" } },
            { "split", new[] { "split", "splitstep" } },
            { "parada", new[] { "parada", "stop", "brake" } },
            { "forehand", new[] { "forehand" } },
            { "backhand2", new[] { "backhand2", "backhand_2maos", "backhand" } },
            { "backhand1", new[] { "backhand1", "backhand_1mao", "backhand" } },
            { "slice", new[] { "slice" } },
            { "voleio_fh", new[] { "voleio_fh", "voleio_direita", "voleio" } },
            { "voleio_bh", new[] { "voleio_bh", "voleio_esquerda", "voleio" } },
            { "smash", new[] { "smash" } },
            { "saque", new[] { "saque", "serve" } },
        };

        /// <summary>Natural ground speed (m/s) of each locomotion clip at playback rate 1 (for stride sync).</summary>
        static readonly Dictionary<string, float> NaturalSpeed = new Dictionary<string, float>
        { { "corrida", 2.2f }, { "corrida_frente", 2.9f }, { "corrida_tras", 1.6f }, { "passo_esq", 2.2f }, { "passo_dir", 2.2f } };

        readonly Animator _animator;
        PlayableGraph _graph;
        AnimationLayerMixerPlayable _layers;
        AnimationMixerPlayable _full;           // 0 idle, 1 fwd, 2 back, 3 left, 4 right, 5 action
        AnimationClipPlayable[] _loco = new AnimationClipPlayable[5];
        string[] _locoKey = new string[5];
        AnimationClipPlayable _action, _actionUpper;
        AnimationClip _actionClip;
        readonly Dictionary<string, AnimationClip> _clips = new Dictionary<string, AnimationClip>();
        readonly Dictionary<string, float> _contact = new Dictionary<string, float>();
        float _actionWeight, _upperBlend, _speedSm;
        readonly float[] _w = { 1, 0, 0, 0, 0 };
        public bool HasLocomotion => _clips.ContainsKey("espera") || _clips.ContainsKey("corrida") || _clips.ContainsKey("corrida_frente");
        public int ClipCount => _clips.Count;

        [System.Serializable] class ContactEntry { public string nome; public float contato; }
        [System.Serializable] class ContactFile { public ContactEntry[] clipes; }

        public ClipAthlete(Animator animator)
        {
            _animator = animator;
            var all = Resources.LoadAll<AnimationClip>("Animacoes");
            foreach (var kv in ClipNames)
                foreach (var alias in kv.Value)
                {
                    AnimationClip found = null;
                    foreach (var c in all) if (c.name.ToLowerInvariant().StartsWith(alias) && !(alias == "corrida" && c.name.ToLowerInvariant() != "corrida")) { found = c; break; }
                    if (found != null) { _clips[kv.Key] = found; break; }
                }
            var json = Resources.Load<TextAsset>("Animacoes/contatos");
            if (json != null)
            {
                var f = JsonUtility.FromJson<ContactFile>(json.text);
                if (f?.clipes != null) foreach (var e in f.clipes) _contact[e.nome] = e.contato;
            }
            if (_clips.Count == 0) return;
            _graph = PlayableGraph.Create("Tenis.ClipAthlete");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var output = AnimationPlayableOutput.Create(_graph, "out", animator);
            _layers = AnimationLayerMixerPlayable.Create(_graph, 2);
            output.SetSourcePlayable(_layers);
            _full = AnimationMixerPlayable.Create(_graph, 6);
            _graph.Connect(_full, 0, _layers, 0); _layers.SetInputWeight(0, 1);
            string idle = Has("espera") ? "espera" : "corrida";
            string run = Has("corrida_frente") ? "corrida_frente" : "corrida";
            string back = Has("corrida_tras") ? "corrida_tras" : (Has("corrida") ? "corrida" : run);
            string left = Has("passo_esq") ? "passo_esq" : (Has("corrida") ? "corrida" : run);
            string right = Has("passo_dir") ? "passo_dir" : (Has("corrida") ? "corrida" : run);
            string[] keys = { idle, run, back, left, right };
            for (int i = 0; i < 5; i++) { _locoKey[i] = keys[i]; _loco[i] = Slot(Get(keys[i]), _full, i); }
            _action = Slot(null, _full, 5);
            // upper-body layer
            _actionUpper = AnimationClipPlayable.Create(_graph, null);
            _graph.Connect(_actionUpper, 0, _layers, 1);
            _layers.SetLayerMaskFromAvatarMask(1, UpperBodyMask());
            _layers.SetInputWeight(1, 0);
            Debug.Log($"[Tenis] animações capturadas carregadas: {string.Join(", ", _clips.Keys)}");
        }

        static AvatarMask UpperBodyMask()
        {
            var m = new AvatarMask();
            for (var p = AvatarMaskBodyPart.Root; p < AvatarMaskBodyPart.LastBodyPart; p++) m.SetHumanoidBodyPartActive(p, false);
            foreach (var p in new[] { AvatarMaskBodyPart.Body, AvatarMaskBodyPart.Head, AvatarMaskBodyPart.LeftArm, AvatarMaskBodyPart.RightArm,
                                      AvatarMaskBodyPart.LeftFingers, AvatarMaskBodyPart.RightFingers, AvatarMaskBodyPart.LeftHandIK, AvatarMaskBodyPart.RightHandIK })
                m.SetHumanoidBodyPartActive(p, true);
            return m;
        }

        bool Has(string k) => _clips.ContainsKey(k);
        AnimationClip Get(string k) => k != null && _clips.TryGetValue(k, out var c) ? c : null;

        AnimationClipPlayable Slot(AnimationClip c, AnimationMixerPlayable mixer, int input)
        {
            var p = AnimationClipPlayable.Create(_graph, c);
            p.SetApplyFootIK(true);
            _graph.Connect(p, 0, mixer, input);
            mixer.SetInputWeight(input, 0);
            return p;
        }

        string ActionKey(AthleteAnimation a, out bool isAction)
        {
            isAction = true;
            if (a.action == BodyAction.Serve) return "saque";
            if (a.action == BodyAction.Smash || a.stroke == Stroke.Smash && a.InShot) return "smash";
            if (a.action == BodyAction.SplitStep) return "split";
            if (a.action == BodyAction.Brake) return Has("parada") ? "parada" : null;
            if (a.action == BodyAction.Preparation || a.action == BodyAction.Swing || a.action == BodyAction.Contact ||
                a.action == BodyAction.FollowThrough || a.action == BodyAction.Recovery)
            {
                switch (a.stroke)
                {
                    case Stroke.Forehand: return "forehand";
                    case Stroke.Backhand: return a.twoHanded ? "backhand2" : "backhand1";
                    case Stroke.ForehandVolley: return "voleio_fh";
                    case Stroke.BackhandVolley:
                    case Stroke.BlockVolley: return "voleio_bh";
                }
            }
            isAction = false; return null;
        }

        /// <summary>True if this frame is handled by captured clips (the procedural pose must then stay off).</summary>
        public bool Covers(AthleteAnimation a)
        {
            var k = ActionKey(a, out bool isAction);
            if (isAction) return k != null && _clips.ContainsKey(k);
            return HasLocomotion;
        }

        void SetAction(AnimationClip clip)
        {
            if (clip == _actionClip) return;
            _graph.Disconnect(_full, 5); _action.Destroy();
            _action = AnimationClipPlayable.Create(_graph, clip); _action.SetApplyFootIK(true);
            _graph.Connect(_action, 0, _full, 5);
            _graph.Disconnect(_layers, 1); _actionUpper.Destroy();
            _actionUpper = AnimationClipPlayable.Create(_graph, clip);
            _graph.Connect(_actionUpper, 0, _layers, 1);
            _actionClip = clip;
        }

        public void Evaluate(AthleteAnimation a, double now, float dt)
        {
            if (!_graph.IsValid()) return;
            string key = ActionKey(a, out bool isAction);
            AnimationClip clip = isAction && key != null ? Get(key) : null;
            float target = clip != null ? 1f : 0f;
            // 150 ms cross-fades (instant only when there is no locomotion to blend with)
            _actionWeight = HasLocomotion ? Mathf.MoveTowards(_actionWeight, target, dt / 0.15f) : target;
            if (clip != null) SetAction(clip);
            if (_actionClip != null)
            {
                double t = key == "split" || key == "parada" ? a.phaseProgress * _actionClip.length
                         : clip != null ? WarpedTime(_actionClip, key, a, now) : _action.GetTime();
                _action.SetTime(t); _actionUpper.SetTime(t);
            }

            // ---- locomotion: direction of motion relative to the body + stride-synced rate
            float speed = (float)a.RunSpeed;
            float moving = Mathf.Clamp01((speed - 0.25f) / 1.2f);
            float ang = (float)a.MoveDirLocal;                       // 0 = forward, +π/2 = left (court yaw), π = back
            float fwd = Mathf.Max(0, Mathf.Cos(ang)), back = Mathf.Max(0, -Mathf.Cos(ang));
            float left = Mathf.Max(0, Mathf.Sin(ang)), right = Mathf.Max(0, -Mathf.Sin(ang));
            float sum = fwd + back + left + right + 1e-4f;
            float[] wt = { 1 - moving, moving * fwd / sum, moving * back / sum, moving * left / sum, moving * right / sum };
            // critically damped blend weights (≈120 ms): direction changes and stops flow instead of popping
            float kw = 1 - Mathf.Exp(-dt / 0.12f);
            for (int i = 0; i < 5; i++) _w[i] = Mathf.Lerp(_w[i], wt[i], kw);
            float ws = _w[0] + _w[1] + _w[2] + _w[3] + _w[4] + 1e-4f;
            float[] w = { _w[0] / ws, _w[1] / ws, _w[2] / ws, _w[3] / ws, _w[4] / ws };
            _speedSm = Mathf.Lerp(_speedSm, speed, 1 - Mathf.Exp(-dt / 0.15f));
            speed = _speedSm;

            // legs keep their locomotion while the upper body swings, as long as the athlete is really moving
            float wantUpper = isAction && clip != null && key != "split" && key != "saque" && key != "parada" && speed > 1.2f ? 1f : 0f;
            _upperBlend = Mathf.MoveTowards(_upperBlend, wantUpper, dt / 0.2f);
            float fullAction = _actionWeight * (1 - _upperBlend);
            float loco = HasLocomotion ? 1 - fullAction : 0f;
            for (int i = 0; i < 5; i++)
            {
                _full.SetInputWeight(i, loco * w[i]);
                if (i > 0 && NaturalSpeed.TryGetValue(_locoKey[i], out var nat))
                    _loco[i].SetSpeed(Mathf.Clamp(speed / nat, 0.6f, 2.2f));
            }
            _full.SetInputWeight(5, fullAction);
            _layers.SetInputWeight(1, _actionWeight * _upperBlend);
            _graph.Evaluate(dt);
        }

        /// <summary>Clip time so that the clip's contact frame coincides with the game's contact instant.
        /// Before contact the wind-up is stretched to the preparation time the player actually had (clamped to
        /// 0.6×–1.6× speed so it never looks robotic); after contact it plays in real time.</summary>
        double WarpedTime(AnimationClip clip, string key, AthleteAnimation a, double now)
        {
            double len = clip.length;
            double cc = _contact.TryGetValue(key, out var c) ? c : len * 0.45;
            double contactAt = a.ContactAt;
            if (now >= contactAt) return System.Math.Min(len, cc + (now - contactAt));
            double start = key == "saque" ? a.ServeTossAt : a.PrepStart;
            double avail = System.Math.Max(0.05, contactAt - start);
            double speed = MathD.Clamp(cc / avail, 0.6, 1.6);
            return System.Math.Max(0, cc - (contactAt - now) * speed);
        }

        public void Dispose() { if (_graph.IsValid()) _graph.Destroy(); }
    }
}
