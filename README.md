# FPS Base

A minimal Unity first-person shooter foundation with NVIDIA Reflex Low Latency and a 60 Hz fixed
simulation. It's meant as a starting point: movement, look, a hitscan weapon, targets, a sandbox
level and a debug HUD. The frame pipeline is already set up for low latency.

## Requirements

- Unity 6.3 LTS (`6000.3.x`) or newer, Windows
- NVIDIA GPU with a current driver (Reflex needs GTX 900 series or newer), Direct3D 11 or 12

The project also runs without NVIDIA hardware. Reflex reports itself unavailable and the game still plays.

## Open it

1. Unity Hub > **Add project from disk** > select this folder. Pick a 6000.3+ editor when asked.
2. When the Input System asks to enable the new input backends, choose **Yes**. The editor restarts.
3. On first load, `SandboxBuilder` generates the URP pipeline, the player prefab and
   `Assets/FpsBase/Scenes/Sandbox.unity`. Open the scene and press Play.

**FPS Base > Rebuild Sandbox** regenerates the scene and player prefab. It overwrites any edits
to those two files. Materials are reused.

**Laptops (Optimus):** if the HUD says Reflex requires an NVIDIA GPU, Windows is running Unity on the
integrated GPU. Set `Unity.exe` (and your build's exe) to *High performance* under Windows Settings >
System > Display > Graphics.

## Automated builds

`.github/workflows/build.yml` builds a Windows player with [GameCI](https://game.ci) on every push
to `main`, or on demand from the repository's **Actions** tab. The zipped build appears under the
run's **Artifacts**.

The workflow needs three repository secrets (**Settings > Secrets and variables > Actions**):

| Secret | Value |
|---|---|
| `UNITY_LICENSE` | Full contents of `C:\ProgramData\Unity\Unity_lic.ulf` (activate a Personal license in Unity Hub first) |
| `UNITY_EMAIL` | Unity account email |
| `UNITY_PASSWORD` | Unity account password |

## Controls

| Input | Action |
|---|---|
| WASD | Move |
| Mouse | Look |
| Space / Mouse Wheel Down | Jump |
| Left Shift | Walk (running is the default) |
| Left Mouse | Fire |
| 1-5 | Select inventory slot |
| E | Pick up the item in reach, or drop the held one |
| F1 | Cycle Reflex: Off > On > On + Boost |
| F2 | Toggle the debug overlay: collider wireframes, shot rays and a log of what each shot hit |
| Esc | Open / close the pause menu (sensitivity, volume, FPS limit, Resume, Quit) |
| Left-Click | Recapture the cursor after alt-tab |

Bindings live in `Assets/FpsBase/Input/Controls.inputactions`.

## Movement

`PlayerMotor` follows Source-engine (Counter-Strike) movement:

- **Air strafing.** In the air, only the velocity component along your wish direction is capped,
  at 0.76 m/s (CS's 30 units). The acceleration step still scales with full movement speed. Holding W
  at speed therefore adds nothing. Holding A or D while turning the mouse the same way keeps the wish
  direction nearly perpendicular to your velocity, and that adds speed every tick.
  `airAcceleration` (12, matching CS's `sv_airaccelerate`) sets how fast you gain speed.
- **Bunny hopping.** A jump press stays buffered for 33 ms (2 ticks). The first grounded tick after
  landing uses air steering and skips ground friction, so a jump one tick late still carries speed.
  Holding jump doesn't repeat. Mouse wheel down sends a separate press for each report.
- **Tick-rate dependence.** Strafe gain per second depends on `Simulation.TickRate`, just as 64-tick
  and 128-tick CS feel different.

The HUD shows horizontal speed, so you can watch strafe gains as they happen.

## Inventory, weapons, pause menu, audio

- **Inventory** (`PlayerInventory`). Five slots, selected with 1-5. The hotbar at the bottom center
  of the HUD highlights the selected slot. The selected item is shown, and if it is a
  `HitscanWeapon` it receives the fire button. Slot 1 starts with the Rifle (600 rpm, 25 damage),
  slot 2 with the SMG (960 rpm = 16/s, 18 damage), and slot 3 with the Sniper Rifle
  (60 rpm, 100 body damage). Right-click to toggle the sniper's 2× scope. Each shot
  returns to the unscoped view and requires a new trigger press.
- **Drop and pick up with E.** Looking at a dropped item within 2.5 m, E picks it up into the
  selected slot if empty, otherwise the first empty slot. With every slot full, it swaps with the
  item you're holding. Looking at nothing, E drops the selected item, thrown forward with your
  velocity. A prompt under the crosshair names the item in reach.
- **Items** (`InventoryItem`). An item is either held, parented to the camera at `heldPosition`
  with its rigidbody kinematic and its collider off, or dropped, as a free rigidbody you can shoot,
  shove and pick up. Each gun is a prefab in `Assets/FpsBase/Prefabs`: Model children, one fitted
  BoxCollider, a Rigidbody, `InventoryItem` and `HitscanWeapon`. To add a gun, duplicate a prefab,
  change its stats and model, and put it in the scene or the player's `startingItems`.
- **Pause menu** (`PauseMenu`). Esc toggles it. Offline, the menu sets `Time.timeScale` to 0, which
  pauses the simulation, while rendering, input and Reflex keep running. In a LAN session the game
  keeps running for everyone, and the menu only frees the cursor. It has sliders for sensitivity
  (`PlayerLook.Sensitivity`), volume (`MasterVolume`, i.e. `AudioListener.volume`) and FPS limit
  (`FrameLimiter`), and a Fullscreen toggle (borderless fullscreen or windowed). The FPS slider
  steps through presets from 30 to 480, plus Off. With Reflex available, the cap runs inside the
  driver's sleep; otherwise it falls back to `Application.targetFrameRate`. The sliders are saved in
  PlayerPrefs and restored at startup; Unity saves and restores the screen mode itself.
  `PlayerLook` captures the cursor when it is
  enabled and on any click that doesn't land on UI, so the player prefab works in scenes without the
  menu. The menu's full-screen backdrop blocks that recapture while it is open. Look and fire only
  respond while the cursor is captured. After any uncaptured moment, the trigger has to be released
  once before it fires again, so the click on Resume never fires a shot.
- **Recoil.** The sniper kicks immediately and springs back between shots. The automatic weapons'
  `recoil` pattern holds a `seed`, a `kick` (degrees up per shot) and a
  `sway` (largest sideways degrees per shot). The seed generates 30 shots of kick and sway: the
  first few go nearly straight up, then the sway runs left or right for 3 to 6 shots at a time and
  flips, so the spray zig-zags as it climbs. The same seed gives the same spray every time, so it
  can be learned. Rifle: seed 7, kick 1.3, sway 0.6. SMG: seed 21, kick 0.7, sway 0.45.
  Each shot adds its kick and sway to the climb, the point the view settles on, and punches the
  view past it with a burst of velocity (`recoilKickRatio`, 1.5x the shot's kick and sway). A stiff
  critically damped spring (`recoilSpringRate`, 45) carries the view there and absorbs the punch
  within about 0.1 s. The climb levels off near `maxRecoilShots` (7) kicks' worth, about 9 degrees
  for the Rifle, so long sprays can't aim at the sky, but every shot still punches. The climb holds
  for 1.15 fire intervals after each shot, then eases back to aim (`recoilRecoverRate`, 14): the
  view is back about 0.45 s after you stop. The pattern position eases back with it, so a short
  pause resumes partway through the pattern and a full return starts it over. Shots fire where the
  recoiled view points. Simulation advances recoil at shot times. The camera previews recoil and
  mouse aim at render time.
- **Fire rate.** Holding the trigger schedules shots at `roundsPerMinute`. Each shot uses the aim
  and recoil at its scheduled time. Shots resolve in the frame they come due rather than waiting
  for a tick, so a click never picks up an extra tick of latency above 60 fps. A short click still
  counts if the weapon is ready. Releasing the trigger cancels automatic fire until the next press.
- **Damage and hitboxes.** A `Hitbox` on the struck collider multiplies weapon damage. With the 4x
  head multiplier, the rifle deals 25 damage to the body and 100 to the head. The SMG deals 18
  to the body and 72 to the head. To add hit zones to anything with a `Damageable`, put a
  `Hitbox` on a child collider.
- **Physics props.** Each bullet adds `impactVelocity` (2 m/s) to a rigidbody's center of mass
  at the hit point, whatever its mass, so off-center hits also spin it. The player shoves
  rigidbodies by walking into them, bringing them up to 80% of the player's speed into them
  (`pushSpeedFactor`).
- **Audio.** `HitscanWeapon` plays a random `Gunshot_N` clip on each shot and a hit or kill
  confirmation on target hits. `PlayerAudio` plays footsteps every 2 m of grounded travel, plus jump
  and land sounds. All of them play through one 2D `AudioSource` on the camera.
  `Assets/FpsBase/Audio` holds synthesized placeholders. To use real sounds, replace those files under
  the same names. Extra variants such as `Gunshot_4.wav` are picked up on the next Rebuild Sandbox.
- **Debug overlay** (`DebugOverlay`, F2). Draws every collider within 80 m, except your own
  player's, as a wireframe over the scene, colored by role: red hit zones (`Hitbox`), yellow
  triggers, cyan player capsules, orange rigidbodies, green static geometry. Each of your last 12
  shots shows its ray and a label where it landed. The log at the top right shows which collider
  counted, the multiplier and the damage. On the host, the log also shows each remote shot as the
  host applied it. The overlay creates itself at startup, so any scene gets it.
- **Weapon models.** The guns are built from primitives under each prefab's `Model` child. To use
  a real model, replace those children with your mesh and refit the root BoxCollider. Keep the
  model itself collider-free. The root collider is off while the gun is held, so it never blocks
  the camera ray.

## LAN multiplayer

`Assets/FpsBase/Runtime/Network/` adds host-authoritative LAN play over plain UDP (port 27015). It
uses no networking package.

- **Start a session.** Open the pause menu. **Host** starts a session on this machine. **Join**
  connects to the address in the field: an IP, a LAN host name, or `address:port`. Leave the field
  blank to find a host by LAN broadcast. **Leave** ends the session and reloads the scene. The menu
  and HUD show the session's state. The command-line switches `-host` and `-join [address]` do the
  same at startup, which makes it easy to run several builds side by side. A session runs in the
  background, so the host keeps ticking while another window has focus. Windows Firewall asks to allow
  the game the first time it hosts.
- **The host runs everything.** It simulates its own player, every remote player from that
  player's streamed input, all rigidbodies, damage and respawns, and the inventory. Each tick it
  sends every client a full snapshot. A snapshot holds the players, items, scene rigidbodies and
  target health, plus recent shot events.
- **Clients predict their own movement.** Each tick a client runs its own input through the same
  `PlayerMotor.Simulate` the host uses and sends the input, repeating the last 8 ticks to survive
  packet loss. A snapshot says which input the host last simulated and where that left the player.
  If the client's own result at that tick is more than 2 cm or 0.1 m/s off, the client takes the
  host's state and replays its later inputs. The HUD counts these corrections. When an input
  arrives late, the host waits for it rather than guessing, so in steady play nothing gets corrected.
- **Everything else is interpolated.** Clients render other players, dropped items and props 1
  tick (17 ms) behind the newest snapshot, between the two snapshots around that moment. A late
  snapshot, or a host below 60 fps, holds the newest pose until the next one arrives. Their
  rigidbodies stay kinematic. Only the host's physics moves anything.
- **Shots are client-authoritative.** A client resolves its own shot against what it sees and
  shows the result right away: recoil, sound, impact marker and hit confirmation. It sends the host
  what the shot struck, by network id, along with the damage. The host applies that damage and the
  rigidbody push as reported, without re-checking. Clients hear and see other players' shots from
  snapshot events.
- **Shots trace against what was on screen at their moment.** A click lands between two frames:
  the one on screen and the one being prepared. Its aim comes from the mouse history at that exact
  moment, so the host and clients both move every other player to the same point between those
  two frames' poses before tracing, and the shot leaves from the camera's position at that point
  too. A player tracking a target with the crosshair hits it at any frame rate, instead of missing
  by however far the target moved since the last frame.
- **Player models.** Each player prefab carries a primitive humanoid (`Body Model` under `View`,
  `Head Model` under the camera), inactive by default. Remote players switch it on. The body turns
  with the player's yaw, and the head follows the aim. The movement capsule stays the only solid
  collider and never takes shots. On spawn, `PlayerLook` fits a trigger `Hitbox` to each model
  part (box, sphere or capsule, matching its primitive), so shots land exactly on what is drawn:
  4x on the skull and visor, 1x everywhere else.
- **Inventory.** Slot selection takes effect on the client at once and is sent to the host. Pickups
  and drops are requests, and the result arrives in a snapshot. Commands repeat until the host
  acknowledges them, so none is lost or applied twice.
- **Ids.** Scene rigidbodies and targets are matched by hierarchy order, since both ends load the same
  scene. A player's starting items get ids from that player's id. Clients spawn any item they don't
  have from the session's `itemPrefabs`, matched by display name.

After pulling these changes into an existing project, run **FPS Base > Rebuild Sandbox** to
regenerate the player prefab and scene with the new pieces.

## How a frame runs

```
 [end of previous loop]  Reflex Sleep -> frame id++ -> SIMULATION_START
 (Unity pumps OS messages)
 EarlyUpdate, last       InputPump: InputSystem.Update() -> INPUT_SAMPLE -> InputPump.Pumped
                           Queues timestamped gameplay input and previews mouse look
 FixedUpdate x0..N       Resolves movement input through each tick, then advances physics
 Update                  Places remote players, interpolates View position, then handles
                           inventory, shots and menus
 LateUpdate              Records remote players' poses for the next frame's shots, then
                           updates the camera with the latest aim and recoil
 PostLateUpdate, first   SIMULATION_END
 FinishFrameRendering    RENDERSUBMIT_START (render thread)
 PresentAfterDraw        RENDERSUBMIT_END, PRESENT_START / PRESENT_END around Present (render thread)
 PostLateUpdate, last    Reflex Sleep ...
```

Design choices worth knowing before you build on it:

- **The simulation is 60 Hz, rendering is not.** `ProjectSettings/TimeManager.asset` sets the fixed
  step to exactly 1/60 s, and `Simulation.TickRate` holds it in code. Put gameplay state changes in
  `FixedUpdate`. Anything visible should be interpolated with `Simulation.InterpolationFactor`. The
  player does this already: the root holds the tick position, and the `View` child, which holds the
  camera, is placed at the interpolated position every frame.
- **Input is sampled before the ticks.** The Input System normally processes events in `PreUpdate`,
  *after* `FixedUpdate`, which adds a frame of delay to 60 Hz code. `InputPump` switches it to manual
  mode and runs one update at the end of `EarlyUpdate`. Movement, jump, mouse look, and fire keep
  the input event timestamps. Each physics tick consumes the events through its own time and leaves
  newer events queued. `PlayerLook` keeps one history of view angles in game time. Ticks steer with
  the angles at their own moment, shots aim with the angles at theirs, and the camera always shows
  the newest.
- **Some devices' timestamps can't be trusted.** Windows can stamp a whole frame's reports with the
  moment Unity drains its message queue. `InputPump` detects this per device: when a device's
  events within one pump keep spanning under 10% of the frame, its timestamps count as clustered.
  A clustered device's mouse turns are spread evenly across the frame in their original order.
  Its key and button changes go to the frame's midpoint, an unbiased guess that still reaches the
  frame's later ticks. Devices are treated as clustered until their own timestamps show otherwise.
  Frames under 4 ms are too short to tell the two apart, and too short for the difference to matter.
- **Reflex sleeps at the end of the loop, not the start.** Unity pumps Windows messages between player
  loops, so input that arrives while the driver holds the CPU still reaches the very next frame.
- **VSync is off** in every quality level. Use the menu's FPS limit (`FrameLimiter`) for a cap. With
  Reflex, it sleeps inside the driver, which adds less latency than `Application.targetFrameRate`.

## Reflex integration

`Assets/FpsBase/Runtime/Reflex/` binds Reflex directly from the driver's `nvapi64.dll` using the
public interface IDs and struct layouts from [NVIDIA/nvapi](https://github.com/NVIDIA/nvapi). No SDK
download and no native plugin are needed. The D3D11/D3D12 device is recovered from
`Texture.GetNativeTexturePtr()` through the COM `GetDevice` vtable slot.

API (`FpsBase.Reflex`):

- `Mode`: `Off`, `On`, `OnAndBoost` (Boost keeps GPU clocks high in CPU-bound scenes)
- `FrameRateLimit`: driver-side frame cap, `0` = none
- `SetMarker(ReflexMarker)`: simulation-thread markers. `TriggerFlash` already fires on each click
  for the Reflex Latency Analyzer.
- `TryGetLatency(out ReflexLatency)`: averages over the last 64 frames (sim start to GPU end, GPU time)
- `IsAvailable`, `UnavailableReason`, `DriverLowLatencyActive`

Measure latency in a **standalone build**. The editor shares one device across all of its windows, so
numbers taken there aren't representative.

### Not included

- **PCL Stats (ETW).** The GeForce overlay's *PC Latency* readout needs NVIDIA's PCL Stats ETW
  provider in addition to NVAPI markers. Without it, the overlay shows 0 ms. Reflex itself still works,
  and the in-game HUD reads latency straight from the driver.
- **Vulkan** (`VK_NV_low_latency2`), Reflex Frame Warp, and frame generation pacing.
- Shipping with the Reflex name or logo in your UI falls under NVIDIA's branding guidelines.

## Layout

```
Assets/FpsBase/
  Runtime/Core       Simulation, InputPump, PlayerLoopUtility, CursorCapture, MasterVolume, FrameLimiter, AudioSourceExtensions
  Runtime/Reflex     Reflex, NvApi, D3DDevice and marker/report types
  Runtime/Player     PlayerMotor, PlayerLook, PlayerInput, HitscanWeapon, RecoilPattern, PlayerAudio, PlayerHealth
  Runtime/Gameplay   Damageable, Hitbox
  Runtime/Network    NetSession (host and client), NetSocket, NetBuffer, NetProtocol, NetMessages, PoseBuffer
  Runtime/Inventory  PlayerInventory, InventoryItem
  Runtime/UI         DebugHud, PauseMenu, InventoryHud
  Audio/             placeholder sound effects
  Input/             Controls.inputactions
  Editor/            SandboxBuilder
```
