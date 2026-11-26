using System;

namespace AwesomeVr
{
    internal static class ContactMath
    {
        internal static float PushImpulse(float depth, float objectSpeed)
        {
            if (depth <= 0) return 0;
            float requestedSpeed = Math.Min(3, depth*12);
            return Math.Max(0, Math.Min(.5f, (requestedSpeed-objectSpeed)*.25f));
        }
        internal static float Fraction(float distance, float hitDistance, float skin)
        {
            if (distance <= 0) return 1;
            return Math.Max(0, Math.Min(1, (hitDistance-Math.Max(0, skin))/distance));
        }
    }

    internal enum MenuClick { None, Pressed, Released }

    internal sealed class MenuClickLatch
    {
        private bool blocked = true, held;
        internal void Reset() { blocked = true; held = false; }
        internal MenuClick Update(bool ready, bool trigger)
        {
            if (!ready)
            {
                Reset();
                return MenuClick.None;
            }
            if (blocked)
            {
                if (!trigger) blocked = false;
                return MenuClick.None;
            }
            if (trigger == held) return MenuClick.None;
            held = trigger;
            return held ? MenuClick.Pressed : MenuClick.Released;
        }
    }

    internal enum CrowdGesture { None, ThumbsUp, FlipOff }

    internal sealed class GestureLatch
    {
        private const float ThumbStraight = .38f;
        private const float MiddleStraight = .32f;
        private const float IndexCurled = .42f;
        private const float MiddleCurled = .54f;
        private const float RingCurled = .51f;
        private const float PinkyCurled = .48f;
        private const float ReleaseMargin = .10f;
        private const float IndexMiddleSeparation = .12f;

        private CrowdGesture candidate, active;
        private double since;

        internal CrowdGesture Update(float[] curl, bool measured, double now)
        {
            CrowdGesture next = CrowdGesture.None;
            if (measured)
            {
                float flipMargin = active == CrowdGesture.FlipOff ? ReleaseMargin : 0;
                float thumbsMargin = active == CrowdGesture.ThumbsUp ? ReleaseMargin : 0;
                if (curl[2] < MiddleStraight + flipMargin &&
                    curl[1] > IndexCurled - flipMargin && curl[1] > curl[2] + IndexMiddleSeparation &&
                    curl[3] > RingCurled - flipMargin && curl[4] > PinkyCurled - flipMargin)
                    next = CrowdGesture.FlipOff;
                else if (curl[0] < ThumbStraight + thumbsMargin && curl[1] > IndexCurled - thumbsMargin &&
                    curl[2] > MiddleCurled - thumbsMargin && curl[3] > RingCurled - thumbsMargin &&
                    curl[4] > PinkyCurled - thumbsMargin)
                    next = CrowdGesture.ThumbsUp;
            }
            if (next == active && active != CrowdGesture.None) return active;
            active = CrowdGesture.None;
            if (next != candidate) { candidate = next; since = now; }
            if (next != CrowdGesture.None && now - since >= .10) active = next;
            return active;
        }
    }

    internal static class ParachuteReach
    {
        private const float FrontClearanceMeters = .20f;
        private const float GrabHalfWidthMeters = .33f;
        private const float GrabHeightRadiusMeters = .24f;
        private const float GrabDepthRadiusMeters = .24f;

        internal static bool Contains(float x, float y, float z, float centerX, float centerY, float centerZ)
        {
            if (z > Math.Min(-.01f, centerZ + FrontClearanceMeters)) return false;
            float side = (Math.Abs(x) - centerX) / GrabHalfWidthMeters;
            float height = (y - centerY) / GrabHeightRadiusMeters;
            float depth = (z - centerZ) / GrabDepthRadiusMeters;
            return side * side + height * height + depth * depth <= 1;
        }
    }

    internal sealed class PullLatch
    {
        private const double TrackingGraceSeconds = .30;
        private const double GrabWindowSeconds = .22;
        private const double HoldTimeoutSeconds = 2;
        private const float PullDistanceMeters = .17f;

        private bool armed, gripHeld;
        private float grabHeight, reachHeight;
        private double pressedAt = double.NegativeInfinity, reachedAt = double.NegativeInfinity;
        private double trackedAt = double.NegativeInfinity, armedAt;
        internal bool Armed { get { return armed; } }

        internal bool Update(bool available, bool tracked, bool grip, bool inReach, float height, double now)
        {
            if (!available)
            {
                Clear();
                gripHeld = grip;
                return false;
            }
            if (now - trackedAt > TrackingGraceSeconds || now - armedAt > HoldTimeoutSeconds) armed = false;
            if (grip && !gripHeld) pressedAt = now;
            if (tracked || grip) gripHeld = grip;
            if (!tracked) return false;
            trackedAt = now;
            if (inReach) { reachedAt = now; reachHeight = height; }
            if (!grip) { armed = false; pressedAt = double.NegativeInfinity; return false; }
            if (!armed && now - pressedAt <= GrabWindowSeconds && now - reachedAt <= GrabWindowSeconds)
            {
                armed = true;
                grabHeight = reachHeight;
                armedAt = now;
                pressedAt = double.NegativeInfinity;
                return false;
            }
            if (!armed || grabHeight - height < PullDistanceMeters) return false;
            Clear();
            return true;
        }

        private void Clear()
        {
            armed = false;
            pressedAt = reachedAt = trackedAt = double.NegativeInfinity;
        }
    }

    internal enum MenuDirection { None, Right, Left, Up, Down }

    internal sealed class MenuRepeat
    {
        private MenuDirection direction;
        private double next;

        internal MenuDirection Update(float x, float y, double now)
        {
            float horizontal = Math.Abs(x);
            float vertical = Math.Abs(y);
            MenuDirection current = MenuDirection.None;
            if (Math.Max(horizontal, vertical) >= .6f)
            {
                current = horizontal > vertical
                    ? (x > 0 ? MenuDirection.Right : MenuDirection.Left)
                    : (y > 0 ? MenuDirection.Up : MenuDirection.Down);
            }
            if (current != direction)
            {
                direction = current;
                next = now + .35;
                return current;
            }
            if (current == MenuDirection.None || now < next) return MenuDirection.None;
            next = now + .18;
            return current;
        }
    }
}
