using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace FpsBase
{
    // Source-engine movement (the Counter-Strike lineage): ground friction and acceleration, air
    // strafing, and friction-free jumps on the landing tick. Simulated only in FixedUpdate at
    // Simulation.TickRate. Strafe gain per second depends on the tick rate, as it does in CS.
    // The root transform holds the authoritative tick position; the view child is placed at the
    // interpolated position each frame, so everything visible should live under it.
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(CharacterController), typeof(PlayerLook), typeof(PlayerHealth))]
    public class PlayerMotor : MonoBehaviour
    {
        private const double JumpBufferSeconds = 2.0 / Simulation.TickRate;

        [SerializeField] private InputActionReference moveAction;
        [SerializeField] private InputActionReference walkAction;
        [SerializeField] private InputActionReference jumpAction;
        [SerializeField] private Transform view;

        [Header("Speed (m/s), runs by default, the walk key slows down")]
        [SerializeField] private float runSpeed = 7.5f;
        [SerializeField] private float walkSpeed = 4f;

        [Header("Ground")]
        [SerializeField] private float groundAcceleration = 10f;
        [SerializeField] private float friction = 6f;
        [SerializeField] private float stopSpeed = 1.5f;

        [Header("Air (CS: sv_airaccelerate 12, 30 unit cap)")]
        [SerializeField] private float airAcceleration = 12f;
        [SerializeField] private float airSpeedCap = 0.76f;

        [Header("Vertical")]
        [SerializeField] private float gravity = 20f;
        [SerializeField] private float jumpHeight = 1.1f;
        [SerializeField] private float groundSnapSpeed = 3f;

        [Header("Pushing")]
        // Fraction of the player's speed into a rigidbody that the body is brought up to.
        [SerializeField] private float pushSpeedFactor = 0.8f;

        private readonly InputPump.Timeline<Vector2> moveSamples = new();
        private readonly InputPump.Timeline<bool> walkSamples = new();
        private readonly InputPump.Timeline<bool> jumpPresses = new();
        private CharacterController controller;
        private PlayerLook look;
        private Vector2 moveInput;
        private bool walking;
        private Vector3 velocity;
        private Vector3 previousPosition;
        private Vector3 currentPosition;
        private bool grounded;
        private bool landingGrace;
        private float fallApex;
        private double jumpExpiresAt = double.NegativeInfinity;
        private bool jumpedThisTick;
        private bool remote;

        // Raised after each locally driven tick with the input it ran on.
        public event Action<PlayerInput> Ticked;

        public Vector3 Velocity => velocity;

        public MotorState State => new MotorState
        {
            Position = currentPosition,
            Velocity = velocity,
            Grounded = grounded,
            LandingGrace = landingGrace,
            FallApex = fallApex,
        };

        public float HorizontalSpeed => new Vector2(velocity.x, velocity.z).magnitude;

        public bool IsGrounded => grounded;

        public PlayerHealth Health { get; private set; }

        // Holds everything visible, placed at the interpolated position each frame.
        public Transform View => view;

        public Vector3 InterpolatedPosition =>
            Vector3.Lerp(previousPosition, currentPosition, Simulation.InterpolationFactor);

        public Vector3 GetSimulationPosition(Transform point)
        {
            // Build the offset from local transforms without using the interpolated view position.
            Vector3 offset = Vector3.zero;
            Transform child = point;
            while (child != null && child != view)
            {
                offset = child.localPosition + child.localRotation * Vector3.Scale(child.localScale, offset);
                child = child.parent;
            }
            return child == view ? currentPosition + view.TransformVector(offset) : point.position;
        }

        public void Teleport(Vector3 position)
        {
            controller.enabled = false;
            transform.position = position;
            controller.enabled = true;
            previousPosition = position;
            currentPosition = position;
            velocity = Vector3.zero;
            grounded = false;
            landingGrace = false;
            fallApex = position.y;
            jumpPresses.Clear();
            jumpExpiresAt = double.NegativeInfinity;
            view.position = position;
        }

        // Driven by network input or snapshots instead of this machine's devices. Call before the
        // object is first enabled, so it never touches the shared input actions.
        public void MarkRemote()
        {
            remote = true;
        }

        public void SetState(MotorState state)
        {
            controller.enabled = false;
            transform.position = state.Position;
            controller.enabled = true;
            previousPosition = state.Position;
            currentPosition = state.Position;
            velocity = state.Velocity;
            grounded = state.Grounded;
            landingGrace = state.LandingGrace;
            fallApex = state.FallApex;
        }

        // A client's view of another player: placed from snapshots rather than simulated.
        public void ApplyProxyState(Vector3 position, Vector3 proxyVelocity, bool proxyGrounded)
        {
            transform.position = position;
            previousPosition = position;
            currentPosition = position;
            velocity = proxyVelocity;
            grounded = proxyGrounded;
        }

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            look = GetComponent<PlayerLook>();
            previousPosition = transform.position;
            currentPosition = transform.position;
            fallApex = currentPosition.y;
            // Existing prefabs can predate the RequireComponent declaration.
            Health = GetComponent<PlayerHealth>();
            if (Health == null)
            {
                Health = gameObject.AddComponent<PlayerHealth>();
            }
        }

        private void OnEnable()
        {
            if (remote)
            {
                return;
            }
            moveAction.action.performed += RecordMove;
            moveAction.action.canceled += RecordMove;
            walkAction.action.performed += RecordWalk;
            walkAction.action.canceled += RecordWalk;
            InputSystem.onEvent += RecordJump;
            moveAction.action.Enable();
            walkAction.action.Enable();
            jumpAction.action.Enable();
        }

        private void OnDisable()
        {
            if (remote)
            {
                return;
            }
            moveAction.action.performed -= RecordMove;
            moveAction.action.canceled -= RecordMove;
            walkAction.action.performed -= RecordWalk;
            walkAction.action.canceled -= RecordWalk;
            InputSystem.onEvent -= RecordJump;
            moveAction.action.Disable();
            walkAction.action.Disable();
            jumpAction.action.Disable();
            moveSamples.Clear();
            walkSamples.Clear();
            jumpPresses.Clear();
            moveInput = Vector2.zero;
            walking = false;
            landingGrace = false;
            jumpExpiresAt = double.NegativeInfinity;
        }

        private void RecordMove(InputAction.CallbackContext context)
        {
            moveSamples.Add(InputPump.EventGameTime(context.time, context.control.device), context.ReadValue<Vector2>());
        }

        private void RecordWalk(InputAction.CallbackContext context)
        {
            walkSamples.Add(InputPump.EventGameTime(context.time, context.control.device), context.performed);
        }

        private void RecordJump(InputEventPtr input, InputDevice device)
        {
            if (!InputPump.GameplayActive || (!input.IsA<StateEvent>() && !input.IsA<DeltaStateEvent>()))
            {
                return;
            }

            foreach (InputControl control in jumpAction.action.controls)
            {
                if (control.device != device || control is not AxisControl axis || !axis.ReadValueFromEvent(input, out float value))
                {
                    continue;
                }

                // Wheel deltas are pulses. The action's accumulated scroll state can hide later pulses.
                bool pressed = axis is ButtonControl button
                    ? value >= button.pressPointOrDefault && !button.isPressed
                    : device is Mouse mouse && control.parent == mouse.scroll && value >= InputSystem.settings.defaultButtonPressPoint;
                if (pressed)
                {
                    jumpPresses.Add(InputPump.EventGameTime(input.time, device), true);
                    break;
                }
            }
        }

        private void FixedUpdate()
        {
            if (remote || !Health.IsAlive)
            {
                return;
            }

            double tickTime = Time.fixedTimeAsDouble;
            while (moveSamples.TryRead(tickTime, out var move))
            {
                moveInput = move.Value;
            }
            while (walkSamples.TryRead(tickTime, out var walk))
            {
                walking = walk.Value;
            }
            while (jumpPresses.TryRead(tickTime, out var jump))
            {
                jumpExpiresAt = jump.Time + JumpBufferSeconds;
            }

            Vector2 angles = look.GetAngles(tickTime);
            var input = new PlayerInput
            {
                Tick = Simulation.CurrentTick,
                Move = moveInput,
                Walk = walking,
                Jump = tickTime - jumpExpiresAt <= InputPump.TimeTolerance,
                Yaw = angles.x,
                Pitch = angles.y,
            };
            Simulate(input);
            if (jumpedThisTick)
            {
                jumpExpiresAt = double.NegativeInfinity;
            }
            Ticked?.Invoke(input);
        }

        // Runs one movement tick. Also replays ticks for client reconciliation, so it reads nothing
        // but the input and the motor's own state.
        public void Simulate(PlayerInput input)
        {
            Tick(Time.fixedDeltaTime, input);
        }

        private void Update()
        {
            view.position = InterpolatedPosition;
        }

        // CharacterController treats rigidbodies as walls; this shoves them instead. Raised during
        // controller.Move inside the tick, possibly several times per body. Writing the velocity
        // takes effect at once, so topping it up to the push speed can't stack. Skips anything
        // underfoot.
        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            Rigidbody body = hit.rigidbody;
            if (body == null || body.isKinematic || NetSession.IsClient || hit.moveDirection.y < -0.3f)
            {
                return;
            }

            Vector3 direction = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z).normalized;
            float pushSpeed = Vector3.Dot(new Vector3(velocity.x, 0f, velocity.z), direction) * pushSpeedFactor;
            float missing = pushSpeed - Vector3.Dot(body.linearVelocity, direction);
            if (missing > 0f)
            {
                body.linearVelocity += direction * missing;
            }
        }

        private void Tick(float dt, PlayerInput command)
        {
            Vector2 input = Vector2.ClampMagnitude(command.Move, 1f);
            float maxSpeed = command.Walk ? walkSpeed : runSpeed;
            Vector3 wish = Quaternion.Euler(0f, command.Yaw, 0f) * new Vector3(input.x, 0f, input.y);
            float wishSpeed = wish.magnitude * maxSpeed;
            Vector3 wishDirection = wish.normalized;

            bool jumping = grounded && command.Jump;
            jumpedThisTick = jumping;

            // Give a press just after the first grounded tick one more chance to carry momentum.
            var horizontal = new Vector3(velocity.x, 0f, velocity.z);
            if (grounded && !jumping && !landingGrace)
            {
                horizontal = ApplyFriction(horizontal, dt);
                horizontal = Accelerate(horizontal, wishDirection, wishSpeed, wishSpeed, groundAcceleration, dt);
            }
            else
            {
                horizontal = Accelerate(horizontal, wishDirection, Mathf.Min(wishSpeed, airSpeedCap), wishSpeed, airAcceleration, dt);
            }

            float vertical = velocity.y;
            if (jumping)
            {
                vertical = Mathf.Sqrt(2f * gravity * jumpHeight);
            }
            else if (grounded)
            {
                vertical = -groundSnapSpeed;
            }
            else
            {
                vertical -= gravity * dt;
            }

            velocity = new Vector3(horizontal.x, vertical, horizontal.z);
            previousPosition = currentPosition;
            CollisionFlags flags = controller.Move(velocity * dt);
            currentPosition = transform.position;

            // Keep only the horizontal speed the controller achieved, so walls absorb momentum.
            Vector3 achieved = (currentPosition - previousPosition) / dt;
            velocity.x = achieved.x;
            velocity.z = achieved.z;
            bool onGround = (flags & CollisionFlags.Below) != 0;
            landingGrace = onGround && !grounded;
            grounded = onGround;
            if ((flags & CollisionFlags.Above) != 0 && velocity.y > 0f)
            {
                velocity.y = 0f;
            }
            if (landingGrace && vertical < 0f && !NetSession.IsClient)
            {
                Health.ApplyFallDamage(fallApex - currentPosition.y);
            }
            fallApex = grounded ? currentPosition.y : Mathf.Max(fallApex, currentPosition.y);
        }

        private Vector3 ApplyFriction(Vector3 horizontal, float dt)
        {
            float speed = horizontal.magnitude;
            if (speed < 0.01f)
            {
                return Vector3.zero;
            }

            float drop = Mathf.Max(speed, stopSpeed) * friction * dt;
            return horizontal * (Mathf.Max(speed - drop, 0f) / speed);
        }

        // Source's Accelerate / AirAccelerate. Only the velocity component along the wish direction is
        // capped, at targetSpeed, while the step scales with the full wishSpeed. In the air the cap is
        // tiny, so holding forward at speed adds nothing, but a wish direction kept nearly
        // perpendicular to velocity (strafe key plus mouse turn) adds speed every tick.
        private static Vector3 Accelerate(Vector3 horizontal, Vector3 direction, float targetSpeed, float wishSpeed, float acceleration, float dt)
        {
            float missing = targetSpeed - Vector3.Dot(horizontal, direction);
            if (missing <= 0f)
            {
                return horizontal;
            }
            return horizontal + direction * Mathf.Min(acceleration * wishSpeed * dt, missing);
        }
    }
}
