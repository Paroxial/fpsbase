using System;
using UnityEngine;

namespace FpsBase
{
    public class Damageable : MonoBehaviour
    {
        [SerializeField] private float maxHealth = 100f;
        [SerializeField] private float respawnSeconds = 3f;

        private float health;
        private float respawnTime;
        private Renderer[] renderers;
        private Collider[] colliders;

        public event Action HealthChanged;
        public event Action<Damageable> Died;

        public float Health => health;

        public bool IsAlive => health > 0f;

        // Whoever dealt the latest damage; null for damage with no source, such as a fall.
        public Damageable LastAttacker { get; private set; }

        protected virtual float RespawnDelay => respawnSeconds;

        // Returns true when this damage kills.
        public bool ApplyDamage(float amount, Damageable attacker = null)
        {
            if (!IsAlive || amount <= 0f)
            {
                return false;
            }

            LastAttacker = attacker;
            health = Mathf.Max(0f, health - amount);
            HealthChanged?.Invoke();
            if (IsAlive)
            {
                return false;
            }

            SetPresent(false);
            respawnTime = Time.time + RespawnDelay;
            Died?.Invoke(this);
            return true;
        }

        // Adopts the host's health on a client.
        public void SetHealth(float value)
        {
            if (value == health)
            {
                return;
            }

            bool wasAlive = IsAlive;
            health = value;
            if (wasAlive != IsAlive)
            {
                SetPresent(IsAlive);
            }
            HealthChanged?.Invoke();
        }

        protected virtual void Awake()
        {
            renderers = GetComponentsInChildren<Renderer>();
            colliders = GetComponentsInChildren<Collider>();
            health = maxHealth;
        }

        private void Update()
        {
            if (!IsAlive && !NetSession.IsClient && Time.time >= respawnTime)
            {
                health = maxHealth;
                SetPresent(true);
                HealthChanged?.Invoke();
            }
        }

        protected virtual void SetPresent(bool present)
        {
            foreach (Renderer targetRenderer in renderers)
            {
                targetRenderer.enabled = present;
            }
            foreach (Collider targetCollider in colliders)
            {
                targetCollider.enabled = present;
            }
        }
    }
}
