using UnityEngine;

namespace FpsBase
{
    // Scales damage for hits on this collider, e.g. 4 on a head. The damage itself still goes to the
    // Damageable on this object or a parent.
    [RequireComponent(typeof(Collider))]
    public class Hitbox : MonoBehaviour
    {
        [SerializeField] private float damageMultiplier = 1f;

        public float DamageMultiplier
        {
            get => damageMultiplier;
            set => damageMultiplier = value;
        }
    }
}
