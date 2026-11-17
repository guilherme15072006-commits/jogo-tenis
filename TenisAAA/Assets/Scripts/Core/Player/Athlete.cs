using System;

namespace Tenis.Core
{
    public enum Hand { Right, Left }
    public enum BackhandStyle { OneHanded, TwoHanded }

    /// <summary>Physical/technical profile. All ratings 0..1 (0.5 = average pro).
    /// Ranked mode uses the same profile for everyone (no pay-to-win), career mode may vary it.</summary>
    [Serializable]
    public class AthleteProfile
    {
        public Hand dominantHand = Hand.Right;
        public BackhandStyle backhand = BackhandStyle.TwoHanded;
        public double strength = 0.5;   // max racket speed
        public double accuracy = 0.5;   // landing error
        public double speed = 0.5;      // court movement
        public double touch = 0.5;      // drop shots / volleys
        public double serve = 0.5;
        public double reach = 0.5;      // arm/racket reach (kept equal in ranked)

        public double MaxSpeedFactor => 0.88 + 0.24 * speed;
        public double StrengthFactor => 0.85 + 0.30 * strength;
        public double AccuracyFactor => 1.35 - 0.70 * accuracy;   // multiplies the landing error
        public double ReachMetres(TimingConfig t) => t.maxReach * (0.92 + 0.16 * reach);
    }

    /// <summary>
    /// Court movement driven by the virtual joystick. Never teleports: progressive acceleration, deceleration,
    /// inertia on direction changes, speed cap scaled by stick deflection, a natural recovery drift when the
    /// stick is released, and a balance value (hurt by sharp turns at speed) used by power/precision.
    /// Joystick: x = right on screen, y = up on screen (towards the net). Converted to court axes using the side.
    /// </summary>
    [Serializable]
    public class PlayerMotor
    {
        public Vec3 position;
        public Vec3 velocity;
        public double balance = 1;              // 0..1
        public CourtSide side = CourtSide.Near;
        public Vec3 recoveryPoint;              // where the player drifts back to when the stick is released
        double _releasedFor;

        readonly MovementConfig _m;
        readonly AthleteProfile _a;

        public PlayerMotor(MovementConfig m, AthleteProfile a, Vec3 start, CourtSide side)
        {
            _m = m; _a = a; position = start; this.side = side;
            recoveryPoint = new Vec3(Court.Sign(side) * (Court.HalfLength + 0.6), 0, 0);
        }

        public double MaxSpeed => _m.maxSpeed * _a.MaxSpeedFactor;
        public double Speed => velocity.Horizontal.Length;

        /// <summary>Screen joystick → court direction. On the Near side "up" is +x and "right" is -y.</summary>
        public Vec3 StickToCourt(double sx, double sy)
        {
            int s = Court.Sign(side);                                // Near = -1
            return new Vec3(-s * sy, s * sx, 0);                     // Near: up → +x, right → -y
        }

        public void Tick(double dt, double stickX, double stickY)
        {
            double mag = Math.Min(1, Math.Sqrt(stickX * stickX + stickY * stickY));
            Vec3 desiredVel;
            if (mag > _m.joystickDeadZone)
            {
                _releasedFor = 0;
                double eff = (mag - _m.joystickDeadZone) / (1 - _m.joystickDeadZone);
                desiredVel = StickToCourt(stickX, stickY).Normalized * (MaxSpeed * eff);
            }
            else
            {
                _releasedFor += dt;
                Vec3 toHome = (recoveryPoint - position).Horizontal;
                double d = toHome.Length;
                desiredVel = (_releasedFor > _m.recoveryDelay && d > 0.25)
                    ? toHome.Normalized * Math.Min(MaxSpeed * _m.recoverySpeedFactor, d * 2.0)
                    : Vec3.Zero;
            }

            Vec3 v = velocity.Horizontal;
            double speed = v.Length;
            // inertia: when the requested direction opposes the current motion, part of the speed is lost
            if (speed > 0.3 && desiredVel.Length > 0.3)
            {
                double cos = Vec3.Dot(v.Normalized, desiredVel.Normalized);
                if (cos < 0.5)
                {
                    double turn = (0.5 - cos) / 1.5;                 // 0 (60°) .. 1 (180°)
                    double keep = MathD.Lerp(1, _m.turnPenalty, turn);
                    v *= Math.Pow(keep, dt * 8);
                    balance -= _m.balanceDrainOnTurn * turn * (speed / MaxSpeed) * dt * 4;
                }
            }
            Vec3 dv = desiredVel - v;
            bool accelerating = desiredVel.Length > v.Length - 1e-6;
            double rate = accelerating ? _m.acceleration : _m.deceleration;
            // progressive acceleration: slower pick-up from standstill, full rate once moving
            if (accelerating) rate *= MathD.Lerp(0.55, 1.0, MathD.Clamp01(speed / 2.0));
            double maxDv = rate * dt;
            if (dv.Length > maxDv) dv = dv.Normalized * maxDv;
            v += dv;
            if (v.Length > MaxSpeed) v = v.Normalized * MaxSpeed;
            velocity = v;
            position += v * dt;
            // keep inside a sensible area (cannot cross the net, cannot run into the stands)
            int s = Court.Sign(side);
            double minX = s < 0 ? -Court.HalfLength - 6 : 0.4, maxX = s < 0 ? -0.4 : Court.HalfLength + 6;
            position.x = MathD.Clamp(position.x, minX, maxX);
            position.y = MathD.Clamp(position.y, -Court.HalfDoublesWidth - 3.5, Court.HalfDoublesWidth + 3.5);
            balance = MathD.Clamp(balance + _m.balanceRecoveryPerSecond * dt * (1 - speed / (MaxSpeed * 1.5)), _m.minBalance, 1);
        }

        /// <summary>Called by the game when a shot is played to destabilise briefly (a stretched shot costs more).</summary>
        public void OnShot(double stretch01) => balance = Math.Max(_m.minBalance, balance - 0.25 * stretch01);
    }
}
