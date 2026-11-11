using UnityEngine;

namespace FpsBase
{
    // Footsteps, jump and landing sounds driven by the motor's tick state. Runs its FixedUpdate after
    // PlayerMotor's (execution order -100), so each tick sees that tick's movement result.
    [RequireComponent(typeof(PlayerMotor))]
    public class PlayerAudio : MonoBehaviour
    {
        [SerializeField] private AudioSource audioSource;
        [SerializeField] private AudioClip[] footstepClips;
        [SerializeField] private AudioClip jumpClip;
        [SerializeField] private AudioClip landClip;
        [SerializeField] private AudioClip hurtClip;
        [SerializeField] private AudioClip deathClip;
        [SerializeField] private float strideLength = 2f;
        [SerializeField] private float minimumStepSpeed = 1f;
        [SerializeField] private float footstepVolume = 0.5f;

        // Ignores the one- or two-tick airborne blips from walking down steps and slopes.
        [SerializeField] private float minimumAirTime = 0.15f;

        private PlayerMotor motor;
        private PlayerHealth health;
        private float lastHealth;
        private bool wasGrounded = true;
        private float airTime;
        private float strideProgress;

        private void Awake()
        {
            motor = GetComponent<PlayerMotor>();
            health = GetComponent<PlayerHealth>();
        }

        private void OnEnable()
        {
            lastHealth = health.Health;
            health.HealthChanged += PlayDamageSound;
        }

        private void OnDisable()
        {
            health.HealthChanged -= PlayDamageSound;
        }

        // Covers fall and weapon damage on the host, and the health a client adopts from snapshots.
        private void PlayDamageSound()
        {
            float current = health.Health;
            if (current < lastHealth)
            {
                audioSource.PlayIfSet(health.IsAlive ? hurtClip : deathClip);
            }
            lastHealth = current;
        }

        private void FixedUpdate()
        {
            bool grounded = motor.IsGrounded;
            if (!grounded)
            {
                if (wasGrounded && motor.Velocity.y > 0f)
                {
                    audioSource.PlayIfSet(jumpClip);
                }
                airTime += Time.fixedDeltaTime;
                wasGrounded = false;
                return;
            }

            if (!wasGrounded && airTime >= minimumAirTime)
            {
                audioSource.PlayIfSet(landClip);
                strideProgress = 0f;
            }
            wasGrounded = true;
            airTime = 0f;

            float speed = motor.HorizontalSpeed;
            if (speed < minimumStepSpeed)
            {
                strideProgress = strideLength * 0.5f;
                return;
            }

            strideProgress += speed * Time.fixedDeltaTime;
            if (strideProgress >= strideLength)
            {
                strideProgress -= strideLength;
                audioSource.PlayRandom(footstepClips, footstepVolume);
            }
        }
    }
}
