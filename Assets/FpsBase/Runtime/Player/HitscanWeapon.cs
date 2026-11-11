using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FpsBase
{
    [RequireComponent(typeof(InventoryItem))]
    public class HitscanWeapon : MonoBehaviour
    {
        // Body damage; a Hitbox on the struck collider multiplies it (4x on heads).
        [SerializeField] private float damage = 25f;
        [SerializeField] private float roundsPerMinute = 600f;
        [SerializeField] private bool automatic = true;
        [SerializeField] private Texture2D scopeTexture;
        [SerializeField] private float range = 200f;
        // Velocity each hit adds to a rigidbody's center of mass in m/s, the same for any mass.
        // Applied at the hit point, so off-center hits also spin the body.
        [SerializeField] private float impactVelocity = 2f;
        [SerializeField] private RecoilPattern recoil = new RecoilPattern();
        [SerializeField] private LayerMask hitMask = ~0;
        [SerializeField] private int impactMarkerCount = 32;
        [SerializeField] private Color impactColor = new Color(1f, 0.55f, 0.1f);

        [Header("Audio")]
        [SerializeField] private AudioClip[] fireClips;
        [SerializeField] private AudioClip hitClip;
        [SerializeField] private AudioClip killClip;

        private PlayerLook look;
        private AudioSource audioSource;
        private double nextFireTime;
        private bool triggerArmed;
        private bool triggerHeld;
        private GameObject impactRoot;
        private Material impactMaterial;
        private Transform[] impactMarkers;
        private int nextImpactMarker;

        // True when the shot kills the target.
        public event Action<bool> Hit;
        public event Action Fired;

        public Texture2D ScopeTexture => scopeTexture;

        public bool OnCooldown => nextFireTime - Time.timeAsDouble > InputPump.TimeTolerance;

        public void Bind(PlayerLook owner, AudioSource source, bool held)
        {
            look = owner;
            audioSource = source;
            ResetTrigger(held);
        }

        public void ResetTrigger(bool held)
        {
            triggerHeld = false;
            triggerArmed = !held;
        }

        public void SetTrigger(bool pressed, double time)
        {
            FireThrough(time, inclusive: false);
            if (!pressed)
            {
                ResetTrigger(false);
                return;
            }
            if (!triggerArmed || triggerHeld || (!automatic && time + InputPump.TimeTolerance < nextFireTime))
            {
                return;
            }

            Reflex.SetMarker(ReflexMarker.TriggerFlash);
            triggerHeld = true;
            nextFireTime = Math.Max(nextFireTime, time);
            FireThrough(time);
        }

        public void FireThrough(double time, bool inclusive = true)
        {
            if (!triggerHeld || !(roundsPerMinute > 0f) || float.IsInfinity(roundsPerMinute))
            {
                return;
            }

            double interval = 60.0 / roundsPerMinute;
            double cutoff = inclusive ? time + InputPump.TimeTolerance : time - InputPump.TimeTolerance;
            while (triggerHeld && nextFireTime <= cutoff)
            {
                double shotTime = nextFireTime;
                nextFireTime += interval;
                if (!automatic)
                {
                    ResetTrigger(true);
                }
                Fire(look.GetShotOrigin(shotTime), look.GetShotDirection(shotTime), (float)interval, shotTime);
                Fired?.Invoke();
            }
        }

        private void Awake()
        {
            CreateImpactMarkers();
        }

        private void OnDestroy()
        {
            Destroy(impactRoot);
            Destroy(impactMaterial);
        }

        private void OnValidate()
        {
            recoil.Invalidate();
        }

        public struct ShotResult
        {
            public bool Hit;
            public Vector3 Point;
            public Vector3 Normal;
            public Collider Collider;
            // Damage the hit deals, with the struck collider's Hitbox multiplier applied.
            public float Damage;
        }

        public ShotResult TraceShot(Vector3 origin, Vector3 direction)
        {
            // Remote models can move between physics ticks.
            Physics.SyncTransforms();
            RaycastHit[] hits = Physics.RaycastAll(origin, direction, range, hitMask, QueryTriggerInteraction.Collide);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            RaycastHit? capsuleHit = null;
            Transform capsuleOwner = null;
            foreach (RaycastHit hit in hits)
            {
                Collider collider = hit.collider;
                if (look != null && collider.transform.IsChildOf(look.transform))
                {
                    continue;
                }

                bool hasHitbox = collider.TryGetComponent(out Hitbox hitbox);
                Transform bodyOwner = null;
                if (collider is CharacterController)
                {
                    if (collider.TryGetComponent(out PlayerMotor motor) && motor.BodyHitbox != null)
                    {
                        continue;
                    }
                    bodyOwner = collider.transform;
                }
                else if (collider is CapsuleCollider && collider.GetComponentInParent<PlayerMotor>() is PlayerMotor owner &&
                         owner.BodyHitbox == collider)
                {
                    bodyOwner = owner.transform;
                }

                if (collider.isTrigger && !hasHitbox && bodyOwner == null)
                {
                    continue;
                }

                // Only a hit zone on the same player can take precedence over the capsule.
                // Stop at the next blocker so a hit zone behind cover cannot override it.
                if (capsuleHit.HasValue && (!hasHitbox || !collider.transform.IsChildOf(capsuleOwner)))
                {
                    break;
                }
                if (!hasHitbox && bodyOwner != null)
                {
                    capsuleHit = hit;
                    capsuleOwner = bodyOwner;
                    continue;
                }

                return MakeShotResult(origin, hit, hasHitbox ? hitbox.DamageMultiplier : 1f, capsuleHit?.collider);
            }

            if (capsuleHit.HasValue)
            {
                return MakeShotResult(origin, capsuleHit.Value, 1f);
            }

            Vector3 end = origin + direction * range;
            DebugOverlay.RecordShot(origin, end, null, null, 0f, 0f);
            return new ShotResult { Point = end };
        }

        private ShotResult MakeShotResult(Vector3 origin, RaycastHit hit, float multiplier, Collider first = null)
        {
            Collider collider = hit.collider;
            float hitDamage = damage * multiplier;
            DebugOverlay.RecordShot(origin, hit.point, first != null ? first : collider, collider, multiplier, hitDamage);
            return new ShotResult { Hit = true, Point = hit.point, Normal = hit.normal, Collider = collider, Damage = hitDamage };
        }

        // Velocity each hit adds to the body's center of mass, the same for any mass.
        public void Push(Rigidbody body, Vector3 point, Vector3 direction)
        {
            if (body != null && !body.isKinematic)
            {
                body.AddForceAtPosition(direction * (impactVelocity * body.mass), point, ForceMode.Impulse);
            }
        }

        // Sound and impact marker for a shot this machine didn't fire itself.
        public void PlayShotEffects(Vector3 point, Vector3 normal, bool hit)
        {
            audioSource.PlayRandom(fireClips);
            if (hit)
            {
                PlaceImpactMarker(point, normal);
            }
        }

        public void ConfirmHit(bool killed)
        {
            audioSource.PlayIfSet(killed ? killClip : hitClip);
            Hit?.Invoke(killed);
        }

        private void Fire(Vector3 origin, Vector3 direction, float interval, double time)
        {
            if (automatic)
            {
                look.AddRecoil(recoil, interval * 1.15f);
            }
            else
            {
                look.AddSingleShotRecoil(recoil);
            }
            audioSource.PlayRandom(fireClips);

            ShotResult result = NetSession.IsOnline
                ? NetSession.Instance.TraceAsShown(this, origin, direction, time)
                : TraceShot(origin, direction);
            if (result.Hit)
            {
                PlaceImpactMarker(result.Point, result.Normal);
            }

            Damageable target = result.Hit ? result.Collider.GetComponentInParent<Damageable>() : null;
            if (NetSession.IsClient)
            {
                // The shooter decides the hit; the host applies whatever the shot reports.
                NetSession.Instance.SendFire(origin, direction, result);
                if (target != null && target.IsAlive)
                {
                    ConfirmHit(target.Health <= result.Damage);
                }
                return;
            }

            if (result.Hit)
            {
                Push(result.Collider.attachedRigidbody, result.Point, direction);
            }
            if (target != null)
            {
                ConfirmHit(target.ApplyDamage(result.Damage, look.GetComponent<PlayerHealth>()));
            }
            if (NetSession.Instance != null)
            {
                NetSession.Instance.ReportShot(look, result);
            }
        }

        private void CreateImpactMarkers()
        {
            impactRoot = new GameObject($"{name} Impact Markers");
            impactMarkers = new Transform[impactMarkerCount];
            for (int i = 0; i < impactMarkers.Length; i++)
            {
                GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                marker.name = "Impact";
                marker.SetActive(false);
                Destroy(marker.GetComponent<Collider>());

                var markerRenderer = marker.GetComponent<Renderer>();
                if (impactMaterial == null)
                {
                    impactMaterial = new Material(markerRenderer.sharedMaterial) { color = impactColor };
                }
                markerRenderer.sharedMaterial = impactMaterial;
                markerRenderer.shadowCastingMode = ShadowCastingMode.Off;

                marker.transform.SetParent(impactRoot.transform, false);
                marker.transform.localScale = Vector3.one * 0.05f;
                impactMarkers[i] = marker.transform;
            }
        }

        private void PlaceImpactMarker(Vector3 point, Vector3 normal)
        {
            Transform marker = impactMarkers[nextImpactMarker];
            nextImpactMarker = (nextImpactMarker + 1) % impactMarkers.Length;
            marker.SetPositionAndRotation(point, Quaternion.LookRotation(normal));
            marker.gameObject.SetActive(true);
        }
    }
}
