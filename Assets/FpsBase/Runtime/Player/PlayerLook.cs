using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace FpsBase
{
    // Keeps a short history of view angles in game time. Simulation ticks steer with the angles at
    // their own moment, shots aim with the angles at the shot's moment, and the camera renders the
    // newest angles, so input never waits for a tick to be seen.
    public class PlayerLook : MonoBehaviour
    {
        // Degrees per mouse count at sensitivity 1, the convention most PC shooters share.
        private const float DegreesPerCount = 0.022f;
        private const string SensitivityKey = "FpsBase.Sensitivity";

        [SerializeField] private InputActionReference lookAction;
        [SerializeField] private Transform cameraTransform;
        // Seen only on other players: the body turns with the yaw, and the head, parented to the
        // camera, follows the aim.
        [SerializeField] private Transform bodyModel;
        [SerializeField] private GameObject headModel;
        [SerializeField] private float sensitivity = 1.5f;
        [SerializeField] private float maxPitch = 89f;

        [Header("Recoil")]
        // The climb levels off near this many shots' worth of kick, so a long spray can't aim the view
        // at the sky. Every shot still punches the view past the climb.
        [SerializeField] private int maxRecoilShots = 7;
        // Size of each shot's punch past the climb, as a multiple of that shot's kick and sway.
        [SerializeField] private float recoilKickRatio = 1.5f;
        // Angular frequency of the critically damped spring that carries the view to the climb and
        // absorbs each punch: a punch peaks after 1/rate seconds and settles in about 5/rate.
        [SerializeField] private float recoilSpringRate = 45f;
        // Angular frequency of the critically damped return to aim once firing stops.
        [SerializeField] private float recoilRecoverRate = 14f;

        private struct Recoil
        {
            public double Time;
            // Seconds left before the view starts returning to aim.
            public float Hold;
            // Where the spray has carried the view, in degrees: x yaw right, y pitch up.
            public Vector2 Climb;
            // Position in the gun's pattern.
            public float Shots;
            // Share of Climb and Shots still applied: 1 while firing, easing to 0 once stopped.
            public float Remaining;
            public float RemainingVelocity;
            // The view's spring displacement from the climb.
            public Vector2 Punch;
            public Vector2 PunchVelocity;

            public Vector2 Offset => Climb * Remaining + Punch;
        }

        // Angles after each recorded turn, in time order. baseAngles holds the angles before the
        // oldest entry once older ones are pruned.
        private readonly List<(double Time, Vector2 Angles)> history = new();
        private readonly List<Vector2> clusteredTurns = new();
        private Vector2 baseAngles;
        private Recoil recoil;
        // The camera position in the last rendered frame.
        private Vector3 shownPosition;
        private double shownTime;
        private bool remote;
        private Camera viewCamera;
        private float unscopedFov;
        private float zoom = 1f;

        public Transform CameraTransform => cameraTransform;

        public float Yaw => LatestAngles.x;

        public float Pitch => LatestAngles.y;

        public void SetZoom(float magnification)
        {
            zoom = magnification;
            if (viewCamera != null)
            {
                float halfAngle = unscopedFov * Mathf.Deg2Rad * 0.5f;
                viewCamera.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(halfAngle) / zoom) * Mathf.Rad2Deg;
            }
        }

        private Vector2 LatestAngles => history.Count > 0 ? history[history.Count - 1].Angles : baseAngles;

        // Persists across sessions in PlayerPrefs.
        public float Sensitivity
        {
            get => sensitivity;
            set
            {
                sensitivity = Mathf.Max(0.01f, value);
                PlayerPrefs.SetFloat(SensitivityKey, sensitivity);
            }
        }

        // Each shot adds the pattern's next kick and sway to the climb, the point the view settles
        // on, and punches the view past it with a burst of velocity, so even at the plateau every
        // shot visibly jumps. The climb holds for `holdSeconds` after the last shot, so it keeps
        // building through continuous fire, then eases back to aim and takes the pattern position
        // with it: a short pause resumes partway through the pattern, a full return starts it over.
        // The aimed angles never change, so once the view returns the crosshair sits where the
        // player pointed it.
        public void AddRecoil(RecoilPattern pattern, float holdSeconds)
        {
            recoil.Climb *= recoil.Remaining;
            recoil.Shots *= recoil.Remaining;
            recoil.Remaining = 1f;
            recoil.RemainingVelocity = 0f;

            Vector2 shot = pattern.GetRecoil(Mathf.FloorToInt(recoil.Shots));
            Vector2 climb = recoil.Climb * (1f - 1f / Mathf.Max(1, maxRecoilShots)) + shot;
            // The spring, not a jump, carries the view up to the new climb.
            recoil.Punch -= climb - recoil.Climb;
            // An impulse v on a critically damped spring peaks at v / (rate * e) past its rest point.
            recoil.PunchVelocity += shot * (recoilKickRatio * recoilSpringRate * (float)Math.E);
            recoil.Climb = climb;
            recoil.Shots += 1f;
            recoil.Hold = holdSeconds;
        }

        public void AddSingleShotRecoil(RecoilPattern pattern)
        {
            recoil.Punch += pattern.GetRecoil(0);
        }

        // x is yaw, y is pitch (positive looks down).
        public Vector2 GetAngles(double time)
        {
            return AnglesAt(time);
        }

        // Aimed by network state instead of this machine's mouse. Call before the object is first
        // enabled, so it never touches the shared input actions or the cursor.
        public void MarkRemote()
        {
            remote = true;
        }

        public void ShowModel()
        {
            if (bodyModel != null)
            {
                bodyModel.gameObject.SetActive(true);
            }
            if (headModel != null)
            {
                headModel.SetActive(true);
            }
        }

        // Turns the head at once, so shots fired before LateUpdate trace against the new aim.
        public void SetRemoteAngles(float yaw, float pitch)
        {
            history.Clear();
            baseAngles = new Vector2(yaw, pitch);
            RenderAim();
        }

        // Call with non-decreasing times: recoil only steps forward.
        public Vector3 GetShotDirection(double time)
        {
            AdvanceRecoil(ref recoil, time);
            return AimRotation(AnglesAt(time), recoil.Offset) * Vector3.forward;
        }

        // The camera position on screen at `time`, between the last rendered frame and this one,
        // so a shot leaves from the same moment its aim comes from.
        public Vector3 GetShotOrigin(double time)
        {
            return Vector3.Lerp(shownPosition, cameraTransform.position, Simulation.FrameBlend(time, shownTime));
        }

        private Vector2 AnglesAt(double time)
        {
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].Time - time <= InputPump.TimeTolerance)
                {
                    return history[i].Angles;
                }
            }
            return baseAngles;
        }

        private void Awake()
        {
            viewCamera = cameraTransform.GetComponent<Camera>();
            if (viewCamera != null)
            {
                unscopedFov = viewCamera.fieldOfView;
            }
            sensitivity = PlayerPrefs.GetFloat(SensitivityKey, sensitivity);
            baseAngles.x = transform.eulerAngles.y;
            recoil.Time = Time.timeAsDouble;
            shownPosition = cameraTransform.position;
            shownTime = Time.timeAsDouble;
            RenderAim();
            if (remote)
            {
                AddModelHitboxes();
            }
        }

        // Fits a trigger hitbox to every part of the model that lacks one, so shots land on exactly
        // what is drawn. Head parts deal the skull's multiplier.
        private void AddModelHitboxes()
        {
            if (bodyModel != null)
            {
                AddPartHitboxes(bodyModel.gameObject, 1f);
            }
            if (headModel != null)
            {
                Hitbox skull = headModel.GetComponentInChildren<Hitbox>(true);
                AddPartHitboxes(headModel, skull != null ? skull.DamageMultiplier : 1f);
            }
        }

        private static void AddPartHitboxes(GameObject model, float damageMultiplier)
        {
            foreach (MeshFilter part in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (part.sharedMesh == null || part.TryGetComponent(out Collider _))
                {
                    continue;
                }
                AddFittedCollider(part.gameObject, part.sharedMesh).isTrigger = true;
                part.gameObject.AddComponent<Hitbox>().DamageMultiplier = damageMultiplier;
            }
        }

        // Matches the primitive the part was built from. Any other mesh gets its bounding box.
        private static Collider AddFittedCollider(GameObject part, Mesh mesh)
        {
            Bounds bounds = mesh.bounds;
            switch (mesh.name)
            {
                case "Sphere":
                    var sphere = part.AddComponent<SphereCollider>();
                    sphere.center = bounds.center;
                    sphere.radius = bounds.extents.x;
                    return sphere;
                case "Capsule":
                case "Cylinder":
                    var capsule = part.AddComponent<CapsuleCollider>();
                    capsule.center = bounds.center;
                    capsule.radius = bounds.extents.x;
                    capsule.height = bounds.size.y;
                    return capsule;
                default:
                    var box = part.AddComponent<BoxCollider>();
                    box.center = bounds.center;
                    box.size = bounds.size;
                    return box;
            }
        }

        private void OnEnable()
        {
            if (remote)
            {
                return;
            }
            lookAction.action.Enable();
            InputSystem.onEvent += RecordLook;
            InputPump.Pumped += AfterPump;
            CursorCapture.Set(true);
        }

        private void OnDisable()
        {
            if (remote)
            {
                return;
            }
            InputPump.Pumped -= AfterPump;
            InputSystem.onEvent -= RecordLook;
            lookAction.action.Disable();
            baseAngles = LatestAngles;
            history.Clear();
            clusteredTurns.Clear();
            CursorCapture.Set(false);
        }

        private void RecordLook(InputEventPtr input, InputDevice device)
        {
            if (device is not Mouse mouse || !InputPump.GameplayActive ||
                (!input.IsA<StateEvent>() && !input.IsA<DeltaStateEvent>()))
            {
                return;
            }

            foreach (InputControl control in lookAction.action.controls)
            {
                if (control == mouse.delta && mouse.delta.ReadValueFromEvent(input, out Vector2 delta) && delta != Vector2.zero)
                {
                    Vector2 turn = delta * (sensitivity * DegreesPerCount / zoom);
                    if (InputPump.HasClusteredTimestamps(device))
                    {
                        clusteredTurns.Add(turn);
                    }
                    else
                    {
                        Append(InputPump.ToGameTime(input.time), turn);
                    }
                    break;
                }
            }
        }

        // Clustered timestamps lose the timing within the frame but keep the order, so the frame's
        // turns are spread evenly across the span it covers. Steering then follows the turn through
        // the frame's ticks instead of jumping once per frame, which is what made air strafing lose
        // speed at low frame rates.
        private void AfterPump()
        {
            int count = clusteredTurns.Count;
            if (count > 0)
            {
                double end = InputPump.SampleTime;
                double start = Math.Max(InputPump.PreviousSampleTime, Time.fixedTimeAsDouble);
                double spacing = Math.Max(0.0, end - start) / count;
                for (int i = 0; i < count; i++)
                {
                    Append(start + (i + 0.5) * spacing, clusteredTurns[i]);
                }
                clusteredTurns.Clear();
            }

            if (Cursor.lockState != CursorLockMode.Locked && ClickedOutsideUi())
            {
                CursorCapture.Set(true);
            }
            RenderAim();
        }

        // Entries stay in time order: a turn stamped earlier than the newest entry joins at the
        // newest entry's time, since time already queried can't be rewritten.
        private void Append(double time, Vector2 turn)
        {
            double newest = history.Count > 0 ? history[history.Count - 1].Time : double.NegativeInfinity;
            Vector2 angles = LatestAngles;
            ApplyLook(ref angles, turn);
            history.Add((Math.Max(time, newest), angles));
        }

        // Ticks and shots never ask for time before the last tick, so older entries fold into
        // baseAngles. Runs after this frame's ticks and shots.
        private void PruneHistory()
        {
            double keepFrom = Time.fixedTimeAsDouble;
            int drop = 0;
            while (drop < history.Count && history[drop].Time <= keepFrom)
            {
                baseAngles = history[drop].Angles;
                drop++;
            }
            history.RemoveRange(0, drop);
        }

        private void LateUpdate()
        {
            PruneHistory();
            RenderAim();
            shownPosition = cameraTransform.position;
            shownTime = Time.timeAsDouble;
        }

        private void RenderAim()
        {
            Recoil preview = recoil;
            AdvanceRecoil(ref preview, Time.timeAsDouble);
            cameraTransform.rotation = AimRotation(LatestAngles, preview.Offset);
            if (bodyModel != null)
            {
                bodyModel.rotation = Quaternion.Euler(0f, LatestAngles.x, 0f);
            }
        }

        private void ApplyLook(ref Vector2 angles, Vector2 delta)
        {
            angles.x = Mathf.Repeat(angles.x + delta.x, 360f);
            angles.y = Mathf.Clamp(angles.y - delta.y, -maxPitch, maxPitch);
        }

        private void AdvanceRecoil(ref Recoil state, double time)
        {
            if (time <= state.Time)
            {
                return;
            }
            float dt = (float)(time - state.Time);
            state.Time = time;

            float held = Mathf.Min(dt, state.Hold);
            state.Hold -= held;
            Relax(ref state.Remaining, ref state.RemainingVelocity, recoilRecoverRate, dt - held);
            Relax(ref state.Punch.x, ref state.PunchVelocity.x, recoilSpringRate, dt);
            Relax(ref state.Punch.y, ref state.PunchVelocity.y, recoilSpringRate, dt);

            if (state.Hold <= 0f && (state.Climb * state.Remaining).sqrMagnitude < 1e-6f &&
                state.Punch.sqrMagnitude < 1e-6f && state.PunchVelocity.sqrMagnitude < 1e-4f)
            {
                state = new Recoil { Time = time };
            }
        }

        // Critically damped spring toward zero, solved exactly: shots and frames step it by different
        // amounts but land on the same path.
        private static void Relax(ref float position, ref float velocity, float rate, float dt)
        {
            float decay = Mathf.Exp(-rate * dt);
            float carried = velocity + rate * position;
            position = (position + carried * dt) * decay;
            velocity = (velocity - rate * carried * dt) * decay;
        }

        private Quaternion AimRotation(Vector2 angles, Vector2 recoilOffset)
        {
            return Quaternion.Euler(Mathf.Clamp(angles.y - recoilOffset.y, -maxPitch, maxPitch), angles.x + recoilOffset.x, 0f);
        }

        private static bool ClickedOutsideUi()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
            {
                return false;
            }

            EventSystem events = EventSystem.current;
            return events == null || !events.IsPointerOverGameObject();
        }
    }
}
