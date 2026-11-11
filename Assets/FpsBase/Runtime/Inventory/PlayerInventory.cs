using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FpsBase
{
    // Five slots of InventoryItems. 1-5 select a slot; the selected item is shown and, if it is a
    // HitscanWeapon, receives the fire button. Interact picks up the dropped item under the
    // crosshair, or drops the selected item when nothing is in reach.
    [RequireComponent(typeof(PlayerLook), typeof(PlayerMotor))]
    public class PlayerInventory : MonoBehaviour
    {
        public const int SlotCount = 5;
        private const float ScopeInSeconds = 0.12f;
        private const float ScopeOutSeconds = 0.08f;

        [SerializeField] private InputActionReference fireAction;
        [SerializeField] private InputActionReference scopeAction;
        [SerializeField] private InputActionReference interactAction;
        [SerializeField] private InputActionReference[] slotActions = new InputActionReference[SlotCount];
        // Held items attach here, and pickups and shots are aimed along it: the camera.
        [SerializeField] private Transform holder;
        [SerializeField] private AudioSource audioSource;
        [SerializeField] private InventoryItem[] startingItems;
        [SerializeField] private float interactRange = 2.5f;
        [SerializeField] private float throwSpeed = 4f;

        private readonly InventoryItem[] slots = new InventoryItem[SlotCount];
        private readonly InputPump.Timeline<bool> fireSamples = new();
        private PlayerLook look;
        private PlayerMotor motor;
        private HitscanWeapon equippedWeapon;
        private bool remote;
        private bool scopeRequested;
        private float scopeProgress;

        // Slot contents or selection changed.
        public event Action Changed;

        // A shot from the equipped weapon hit a Damageable. True when it killed.
        public event Action<bool> Hit;

        public int SelectedIndex { get; private set; }

        public InventoryItem SelectedItem => slots[SelectedIndex];

        // The dropped item under the crosshair within reach, refreshed every frame.
        public InventoryItem HoveredItem { get; private set; }

        public HitscanWeapon EquippedWeapon => equippedWeapon;

        public Texture2D ScopeTexture => equippedWeapon != null ? equippedWeapon.ScopeTexture : null;

        public float ScopeBlend { get; private set; }

        public float InteractRange => interactRange;

        // The items this player spawns holding, in slot order. Their order gives them network ids.
        public InventoryItem[] StartingItems => startingItems;

        public InventoryItem GetItem(int slot)
        {
            return slots[slot];
        }

        // Driven by network commands and snapshots instead of this machine's keys. Call before the
        // object is first enabled, so it never touches the shared input actions.
        public void MarkRemote()
        {
            remote = true;
        }

        public void SelectSlot(int slot)
        {
            if (slot >= 0 && slot < SlotCount && slot != SelectedIndex)
            {
                Select(slot);
            }
        }

        // Adopts the host's slots and selection on a client.
        public void ApplyNetworkState(InventoryItem[] items, int selected)
        {
            bool changed = selected != SelectedIndex;
            for (int i = 0; i < SlotCount; i++)
            {
                changed |= items[i] != slots[i];
            }
            if (!changed)
            {
                return;
            }

            Unequip();
            InventoryItem shown = slots[SelectedIndex];
            if (shown != null && shown.IsHeld && shown.transform.parent == holder)
            {
                shown.gameObject.SetActive(false);
            }
            for (int i = 0; i < SlotCount; i++)
            {
                InventoryItem item = items[i];
                if (item != null && (!item.IsHeld || item.transform.parent != holder))
                {
                    item.Hold(holder);
                    item.gameObject.SetActive(false);
                }
                slots[i] = item;
            }
            Select(Mathf.Clamp(selected, 0, SlotCount - 1));
        }

        private void Awake()
        {
            look = GetComponent<PlayerLook>();
            motor = GetComponent<PlayerMotor>();
        }

        private void OnEnable()
        {
            if (remote)
            {
                return;
            }
            fireAction.action.performed += RecordFire;
            fireAction.action.canceled += RecordFire;
            fireAction.action.Enable();
            scopeAction?.action.Enable();
            interactAction.action.Enable();
            foreach (InputActionReference slotAction in slotActions)
            {
                slotAction.action.Enable();
            }
        }

        private void OnDisable()
        {
            if (remote)
            {
                return;
            }
            fireAction.action.performed -= RecordFire;
            fireAction.action.canceled -= RecordFire;
            ResetFire();
            ClearScope();
            fireAction.action.Disable();
            scopeAction?.action.Disable();
            interactAction.action.Disable();
            foreach (InputActionReference slotAction in slotActions)
            {
                slotAction.action.Disable();
            }
        }

        private void Start()
        {
            // A client's copy of another player holds whatever the host's snapshots say.
            if (remote && NetSession.IsClient)
            {
                return;
            }

            for (int i = 0; i < startingItems.Length && i < SlotCount; i++)
            {
                InventoryItem item = startingItems[i];
                if (item != null)
                {
                    slots[i] = item;
                    item.Hold(holder);
                    item.gameObject.SetActive(false);
                }
            }
            Select(0);
        }

        private void Update()
        {
            if (remote)
            {
                return;
            }

            if (InputPump.GameplayActive && motor.Health.IsAlive)
            {
                HandleSlotKeys();
                if (ScopeTexture != null && scopeAction?.action.WasPressedThisFrame() == true)
                {
                    scopeRequested = !scopeRequested && !equippedWeapon.OnCooldown;
                }
                HoveredItem = FindItemInReach();
                if (interactAction.action.WasPressedThisFrame())
                {
                    // A client asks the host, and the change arrives with a snapshot.
                    if (NetSession.IsClient)
                    {
                        NetSession.Instance.SendInteract(HoveredItem);
                    }
                    else if (HoveredItem != null)
                    {
                        PickUp(HoveredItem);
                    }
                    else
                    {
                        DropSelected();
                    }
                }
                ResolveShots();
            }
            else
            {
                HoveredItem = null;
                ResetFire();
                CancelScope();
            }

            float duration = scopeRequested ? ScopeInSeconds : ScopeOutSeconds;
            SetScopeProgress(Mathf.MoveTowards(scopeProgress, scopeRequested ? 1f : 0f, Time.unscaledDeltaTime / duration));
        }

        private void RecordFire(InputAction.CallbackContext context)
        {
            if (!InputPump.GameplayActive)
            {
                ResetFire();
                return;
            }

            if (equippedWeapon != null)
            {
                fireSamples.Add(InputPump.EventGameTime(context.time, context.control.device), context.performed);
            }
        }

        // Resolves each shot in the frame it comes due, using its scheduled time for aim and recoil.
        private void ResolveShots()
        {
            if (equippedWeapon == null)
            {
                return;
            }

            double now = Time.timeAsDouble;
            while (fireSamples.TryRead(now, out var sample))
            {
                equippedWeapon.SetTrigger(sample.Value, sample.Time);
            }
            equippedWeapon.FireThrough(now);
        }

        private void ResetFire()
        {
            fireSamples.Clear();
            if (equippedWeapon != null)
            {
                equippedWeapon.ResetTrigger(fireAction.action.IsPressed());
            }
        }

        private void HandleSlotKeys()
        {
            for (int i = 0; i < slotActions.Length && i < SlotCount; i++)
            {
                if (i != SelectedIndex && slotActions[i].action.WasPressedThisFrame())
                {
                    Select(i);
                    if (NetSession.IsClient)
                    {
                        NetSession.Instance.SendSelect(i);
                    }
                    return;
                }
            }
        }

        private void Select(int slot)
        {
            Unequip();
            InventoryItem previous = slots[SelectedIndex];
            if (previous != null)
            {
                previous.gameObject.SetActive(false);
            }

            SelectedIndex = slot;
            InventoryItem item = slots[slot];
            if (item != null)
            {
                item.gameObject.SetActive(true);
                if (item.TryGetComponent(out equippedWeapon))
                {
                    equippedWeapon.Bind(look, audioSource, fireAction.action.IsPressed());
                    equippedWeapon.Hit += ForwardHit;
                    if (!remote)
                    {
                        equippedWeapon.Fired += CancelScope;
                    }
                }
            }
            Changed?.Invoke();
        }

        // Fills the selected slot if empty, else the first empty slot. With every slot full, the
        // selected item is dropped to make room.
        public void PickUp(InventoryItem item)
        {
            int slot = slots[SelectedIndex] == null ? SelectedIndex : Array.IndexOf(slots, null);
            if (slot < 0)
            {
                DropSelected();
                slot = SelectedIndex;
            }

            slots[slot] = item;
            item.Hold(holder);
            item.gameObject.SetActive(false);
            Select(slot);
        }

        public void DropSelected()
        {
            InventoryItem item = slots[SelectedIndex];
            if (item == null)
            {
                return;
            }

            Unequip();
            slots[SelectedIndex] = null;

            // Spawns in front of the camera, pulled back from any wall so it can't start inside one.
            Vector3 forward = holder.forward;
            Vector3 position = holder.position + forward * 0.6f;
            if (Physics.Raycast(holder.position, forward, out RaycastHit wall, 0.8f, ~0, QueryTriggerInteraction.Ignore))
            {
                position = wall.point - forward * 0.3f;
            }
            item.Drop(position, holder.rotation, motor.Velocity + forward * throwSpeed + Vector3.up);
            Changed?.Invoke();
        }

        private void Unequip()
        {
            fireSamples.Clear();
            ClearScope();
            if (equippedWeapon != null)
            {
                equippedWeapon.Hit -= ForwardHit;
                equippedWeapon.Fired -= CancelScope;
                equippedWeapon = null;
            }
        }

        private void CancelScope()
        {
            scopeRequested = false;
        }

        private void ClearScope()
        {
            CancelScope();
            SetScopeProgress(0f);
        }

        private void SetScopeProgress(float progress)
        {
            if (scopeProgress == progress)
            {
                return;
            }

            scopeProgress = progress;
            ScopeBlend = Mathf.SmoothStep(0f, 1f, progress);
            look.SetZoom(1f + ScopeBlend);
            if (SelectedItem != null)
            {
                SelectedItem.SetScopePose(ScopeBlend);
            }
        }

        private InventoryItem FindItemInReach()
        {
            if (!Physics.Raycast(holder.position, holder.forward, out RaycastHit hit, interactRange, ~0, QueryTriggerInteraction.Ignore))
            {
                return null;
            }

            InventoryItem item = hit.collider.GetComponentInParent<InventoryItem>();
            return item != null && !item.IsHeld ? item : null;
        }

        private void ForwardHit(bool killed)
        {
            Hit?.Invoke(killed);
        }
    }
}
