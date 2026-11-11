using UnityEngine;

namespace FpsBase
{
    public class PlayerHealth : Damageable
    {
        [SerializeField, Min(0f)] private float safeFallDistance = 3f;
        [SerializeField, Min(0f)] private float fallDamagePerMeter = 10f;

        private Vector3 spawnPosition;
        private string displayName;

        // The host names remote players from their Hello. Only the local player falls back to the
        // saved name, so on a client the remote players' names here are meaningless.
        public string DisplayName
        {
            get => displayName ?? PlayerName.Value;
            set => displayName = value;
        }

        protected override float RespawnDelay => 0f;

        public void ApplyFallDamage(float distance)
        {
            float excess = Mathf.Max(0f, distance - safeFallDistance);
            ApplyDamage(Mathf.RoundToInt(excess * fallDamagePerMeter));
        }

        public void SetSpawnPosition(Vector3 position)
        {
            spawnPosition = position;
        }

        protected override void Awake()
        {
            base.Awake();
            spawnPosition = transform.position;
            Died += ReportKill;
        }

        // Died fires only where damage is applied: offline and on the host.
        private void ReportKill(Damageable victim)
        {
            string killer = LastAttacker is PlayerHealth shooter && shooter != this ? shooter.DisplayName : "gravity";
            EventFeed.ReportKill(DisplayName, killer);
        }

        // On a client the host's snapshots move the player to the respawn point.
        protected override void SetPresent(bool present)
        {
            if (present && !NetSession.IsClient)
            {
                GetComponent<PlayerMotor>().Teleport(RandomSpawnPosition());
            }
        }

        private Vector3 RandomSpawnPosition()
        {
            SpawnPoint[] points = FindObjectsByType<SpawnPoint>();
            return points.Length > 0 ? points[Random.Range(0, points.Length)].transform.position : spawnPosition;
        }
    }
}
