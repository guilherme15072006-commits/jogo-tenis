using System;

namespace Tenis.Core
{
    /// <summary>Every tunable number of the gameplay lives here and is loaded from
    /// StreamingAssets/tennis_config.json, so balancing never requires touching code.
    /// Field names are JSON keys (Unity JsonUtility compatible: public fields, [Serializable]).</summary>
    [Serializable]
    public class TennisConfig
    {
        public BallConfig ball = new BallConfig();
        public SurfaceConfig[] surfaces = SurfaceConfig.Defaults();
        public string surface = "clay";
        public NetConfig net = new NetConfig();
        public GestureConfig gesture = new GestureConfig();
        public PowerConfig power = new PowerConfig();
        public AimConfig aim = new AimConfig();
        public TimingConfig timing = new TimingConfig();
        public ShotRulesConfig shots = new ShotRulesConfig();
        public MovementConfig movement = new MovementConfig();
        public ServeConfig serve = new ServeConfig();
        public RacketConfig racket = new RacketConfig();

        public SurfaceConfig ActiveSurface
        {
            get
            {
                foreach (var s in surfaces) if (s.name == surface) return s;
                return surfaces[0];
            }
        }
    }

    [Serializable]
    public class BallConfig
    {
        public double mass = 0.0577;            // kg (ITF 56.0–59.4 g)
        public double radius = 0.0335;          // m
        public double gravity = 9.81;
        public double airDensity = 1.21;        // kg/m³
        public double dragCoefficient = 0.55;
        public double spinDecayPerSecond = 0.035; // fraction of spin lost per second in the air
        public double stepHz = 240;             // fixed simulation rate
        public double maxFlightSeconds = 6;
        public double shellInertiaFactor = 0.6667; // I = k m r² (thin shell ≈ 2/3, real ball ≈ 0.55–0.67)
    }

    [Serializable]
    public class SurfaceConfig
    {
        public string name = "clay";
        public double restitution = 0.80;       // vertical COR
        public double friction = 0.62;          // sliding friction coefficient at bounce
        public double spinRetention = 0.85;     // spin kept after bounce (deformation losses)

        public static SurfaceConfig[] Defaults() => new[]
        {
            new SurfaceConfig { name = "clay",  restitution = 0.80, friction = 0.62, spinRetention = 0.85 },
            new SurfaceConfig { name = "hard",  restitution = 0.82, friction = 0.50, spinRetention = 0.88 },
            new SurfaceConfig { name = "grass", restitution = 0.74, friction = 0.36, spinRetention = 0.90 },
        };
    }

    [Serializable]
    public class NetConfig
    {
        public double centreHeight = 0.914;
        public double postHeight = 1.07;
        public double postDistanceFromCentre = 6.40; // doubles sideline + 0.914
        public double tapeThickness = 0.06;
        public double netBodyDamping = 0.12;   // speed kept when the ball hits the net body
        public double cordSpeedKeep = 0.45;    // speed kept on a net-cord (tape) touch
        public double cordUpwardBias = 0.35;   // how much the tape kicks the ball upward
    }

    [Serializable]
    public class GestureConfig
    {
        // All distances are normalised by screen HEIGHT so the feel is identical on any phone.
        public double minLength = 0.035;          // shorter → treated as tap (or rejected)
        public double tapMaxLength = 0.03;
        public double tapMaxDuration = 0.20;      // s
        public double minDuration = 0.03;         // s, below → accidental brush
        public double maxDuration = 0.90;         // s, above → not a swing (finger resting)
        public double minPeakSpeed = 0.35;        // screen-heights per second
        public double minStraightness = 0.55;     // chord / path length; scribbles are rejected
        public double duplicateWindow = 0.18;     // s, two accepted gestures closer than this → second is dropped
        public double cooldownAfterShot = 0.35;   // s, no new swing right after a contact
        public double shortGestureLength = 0.11;  // amplitude below this (but above min) → aim short zones
        // Angle classification (degrees, 0 = right, 90 = up on screen)
        public double verticalHalfWidth = 22;     // |angle-90| < this → vertical up (lob); |angle+90| < this → down
        public double horizontalHalfWidth = 22;   // |angle| or |angle-180| < this → horizontal (flat)
        public double joystickZoneMaxX = 0.45;    // fraction of screen width owned by the joystick
    }

    [Serializable]
    public class PowerConfig
    {
        public double fingerSpeedForFullSwing = 3.2;  // screen-heights/s that maps to a full swing (soft-saturated)
        public double amplitudeForFullSwing = 0.35;   // screen-heights
        public double speedWeight = 0.65;
        public double amplitudeWeight = 0.35;
        public double minRacketSpeed = 8;             // m/s
        public double maxRacketSpeed = 42;            // m/s (elite forehand racket-head speed ~ 35–45)
        public double prepMinFactor = 0.70;           // no preparation → 70 % of the swing
        public double balanceMinFactor = 0.60;
        // Hard caps on outgoing ball speed (m/s) per shot family
        public double maxGroundstrokeSpeed = 46;      // ~165 km/h
        public double maxVolleySpeed = 36;
        public double maxSmashSpeed = 55;
        public double maxServeSpeed = 69;             // ~250 km/h
        public double maxLobSpeed = 24;
        public double maxDropShotSpeed = 14;
    }

    [Serializable]
    public class AimConfig
    {
        public double deepTargetFromBaseline = 1.6;   // m inside the baseline
        public double shortTargetFromNet = 3.4;       // m from the net
        public double lateralTargetFromSideline = 1.1;// m inside the singles sideline
        public double lateralFullDeflection = 0.20;   // screen-heights of horizontal finger travel = full left/right
        public double baseErrorMetres = 0.45;         // 1-sigma landing error for an average athlete, perfect contact
        public double powerErrorGrowth = 1.6;         // error grows with (power)^2 * this
        public bool showIndicator = true;
    }

    [Serializable]
    public class TimingConfig
    {
        public double baseWindow = 0.110;             // s: |error| inside this = at least "Bom"
        public double perfectFraction = 0.25;
        public double veryGoodFraction = 0.50;
        public double lateFraction = 2.0;             // beyond window up to this × window → Atrasado, else Falha
        public double referenceBallSpeed = 22;        // m/s at which the window equals baseWindow
        public double awkwardHeightLow = 0.45;        // m, contact below this shrinks the window
        public double awkwardHeightHigh = 1.55;
        public double reachComfort = 0.9;             // m from body centre where reach starts costing window
        public double maxReach = 1.85;                // m, beyond this the ball is unreachable → Falha
        public double runSpeedPenalty = 0.05;         // window shrink per m/s of player speed at contact
        public double smashWindowFactor = 0.8;
        public double volleyWindowFactor = 0.85;
        public double dropShotWindowFactor = 0.9;
        public double idealContactHeight = 0.95;      // m (groundstrokes)
    }

    [Serializable]
    public class ShotRulesConfig
    {
        public double volleyMaxDistanceFromNet = 5.0; // m
        public double smashMinBallHeight = 2.35;      // m at predicted contact
        public double dropShotMaxIncomingSpeed = 26;  // m/s, faster balls cannot be dropped
        public double blockVolleyIncomingSpeed = 25;  // m/s or less than blockVolleyTime → block volley
        public double blockVolleyTime = 0.45;         // s available
        public string backhandStyle = "two";          // default when athlete does not define it
        // spin (rad/s, positive = topspin) and net clearance per shot type
        public double flatSpin = 90, topspinSpin = 330, sliceSpin = -230, lobSpin = 180, dropSpin = -260, smashSpin = 60, volleySpin = -80;
        public double flatClearance = 0.35, topspinClearance = 0.9, sliceClearance = 0.30, lobClearance = 4.0, dropClearance = 0.25, volleyClearance = 0.15;
    }

    [Serializable]
    public class MovementConfig
    {
        public double maxSpeed = 6.2;                 // m/s
        public double acceleration = 9.5;             // m/s²
        public double deceleration = 10.5;            // m/s² (braking takes ~0.5 s from a sprint)
        public double turnPenalty = 0.55;             // speed fraction kept on a 180° reversal (inertia)
        public double joystickDeadZone = 0.12;
        public double recoverySpeedFactor = 0.55;     // fraction of max speed while drifting back on release
        public double recoveryDelay = 0.35;           // s after release before the recovery starts
        public double balanceDrainOnTurn = 0.6;       // per 180° turn at full speed
        public double balanceRecoveryPerSecond = 0.9;
        public double minBalance = 0.35;
        public double assist = 0.75;                  // 0 = só joystick, 1 = o atleta corre sozinho até a bola
        public double assistStartDelay = 0.12;        // s depois do split-step
        public double turnRateDeg = 540;              // velocidade máxima de giro do corpo (graus/s)
        public double faceRunDistance = 2.8;          // longe da bola: corre de frente para onde vai
    }

    [Serializable]
    public class ServeConfig
    {
        public double tossApexHeight = 3.05;          // m
        public double idealContactBeforeApex = -0.03; // s (just after apex)
        public double contactWindow = 0.12;           // s
        public double maxGestureAfterToss = 1.6;      // s, then the toss is caught and redone (no fault)
        public double flatSpin = 50, topspinSpin = 420, sliceSpin = -300;   // rad/s (slice uses side-spin component)
        public double flatClearance = 0.12, topspinClearance = 0.55, sliceClearance = 0.25;
        public double secondServePowerCap = 0.82;     // fraction of max serve speed allowed on 2nd serve (safety)
    }

    [Serializable]
    public class RacketConfig
    {
        public double apparentRestitution = 0.42;     // e_A of a modern racket near the sweet spot
        public double offCentreRestitutionLoss = 0.35;// fraction lost at the edge of the frame
        public double spinTransfer = 0.55;            // fraction of tangential racket speed turned into surface speed
    }
}
