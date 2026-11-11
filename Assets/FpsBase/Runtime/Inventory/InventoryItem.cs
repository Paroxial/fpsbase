using UnityEngine;
using UnityEngine.Rendering;

namespace FpsBase
{
    // Something that fits in an inventory slot and exists either held, parented to the player's
    // camera with physics off, or dropped, as a free rigidbody in the world that can be picked up.
    [RequireComponent(typeof(Rigidbody))]
    public class InventoryItem : MonoBehaviour
    {
        [SerializeField] private string displayName = "Item";
        // Where the model sits relative to the camera while held.
        [SerializeField] private Vector3 heldPosition = new Vector3(0.18f, -0.2f, 0.38f);
        // Enabled only while dropped, so a held item never blocks the camera ray.
        [SerializeField] private Collider worldCollider;

        private Rigidbody body;
        private Renderer[] renderers;

        public string DisplayName => displayName;

        public bool IsHeld { get; private set; }

        // Out in the world as a free item. A freshly spawned item is neither held nor dropped.
        public bool IsDropped { get; private set; }

        public void Hold(Transform holder)
        {
            IsHeld = true;
            IsDropped = false;
            Rigidbody itemBody = Body();
            itemBody.isKinematic = true;
            // Interpolation on a kinematic body would override the parent-driven pose and jitter.
            itemBody.interpolation = RigidbodyInterpolation.None;
            worldCollider.enabled = false;
            transform.SetParent(holder, false);
            transform.SetLocalPositionAndRotation(heldPosition, Quaternion.identity);
            SetShadows(ShadowCastingMode.Off);
        }

        public void SetScopePose(float blend)
        {
            transform.localPosition = heldPosition + Vector3.down * (0.45f * blend);
        }

        public void Drop(Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            IsHeld = false;
            IsDropped = true;
            transform.SetParent(null, false);
            transform.SetPositionAndRotation(position, rotation);
            gameObject.SetActive(true);
            worldCollider.enabled = true;
            SetShadows(ShadowCastingMode.On);

            // On a client the host simulates the item and snapshots place it.
            Rigidbody itemBody = Body();
            if (NetSession.IsClient)
            {
                itemBody.isKinematic = true;
                itemBody.interpolation = RigidbodyInterpolation.None;
                return;
            }
            itemBody.isKinematic = false;
            itemBody.interpolation = RigidbodyInterpolation.Interpolate;
            itemBody.linearVelocity = velocity;
            itemBody.angularVelocity = transform.right * 4f;
        }

        public Rigidbody Rigidbody => Body();

        // Resolved lazily: an item can be held before its first Awake if it starts inactive.
        private Rigidbody Body()
        {
            if (body == null)
            {
                body = GetComponent<Rigidbody>();
            }
            return body;
        }

        private void SetShadows(ShadowCastingMode mode)
        {
            if (renderers == null)
            {
                renderers = GetComponentsInChildren<Renderer>(true);
            }
            foreach (Renderer itemRenderer in renderers)
            {
                itemRenderer.shadowCastingMode = mode;
            }
        }
    }
}
