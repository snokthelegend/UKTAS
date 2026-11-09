using System;
using System.Globalization;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Exact V1 load hold.
    ///
    /// A savestate restore is not one assignment: it enables and disables objects, runs
    /// OnEnable, restarts coroutines (whose first segment runs synchronously), updates every
    /// animator and writes dozens of components around the player. Any of that can move V1 or
    /// give his Rigidbody velocity again after the player snapshot was applied. This module
    /// therefore freezes him before the restore starts and keeps him frozen until the first
    /// resumed tick:
    ///
    ///  - the Rigidbody is made kinematic, its velocity is zeroed and its saved pose is held;
    ///  - the saved world pose, local pose and Rigidbody pose are re-applied after every
    ///    restore step (and at each paused frame end), so an OnEnable or animator update
    ///    cannot leave him a fraction of a unit off;
    ///  - the exact pose, rotation, velocity, angular velocity, camera angles and animator
    ///    states are written back when playback resumes, immediately before the tick's physics.
    ///
    /// Values are stored twice: as Unity's floats and in a widened double copy, so the restore
    /// writes back the same bits with no formatting, rounding or re-derivation.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        Savestate _playerLoadState;
        bool _playerLoadHeld;
        BodyState _playerLoadFallbackBody;

        /// <summary>True while a loaded savestate is pinning V1's pose and Rigidbody.</summary>
        public bool PlayerLoadHeld { get { return _playerLoadHeld; } }

        static PlayerExactState CapturePlayerExact(PlayerState snapshot, Rigidbody body, Transform transform)
        {
            PlayerExactState exact = new PlayerExactState();
            if (transform != null)
            {
                Vector3 position = transform.position;
                Quaternion rotation = transform.rotation;
                Vector3 localPosition = transform.localPosition;
                Quaternion localRotation = transform.localRotation;
                Vector3 localScale = transform.localScale;

                exact.HasWorldPose = true;
                exact.PositionX = position.x;
                exact.PositionY = position.y;
                exact.PositionZ = position.z;
                exact.RotationX = rotation.x;
                exact.RotationY = rotation.y;
                exact.RotationZ = rotation.z;
                exact.RotationW = rotation.w;

                exact.HasLocalPose = true;
                exact.LocalPositionX = localPosition.x;
                exact.LocalPositionY = localPosition.y;
                exact.LocalPositionZ = localPosition.z;
                exact.LocalRotationX = localRotation.x;
                exact.LocalRotationY = localRotation.y;
                exact.LocalRotationZ = localRotation.z;
                exact.LocalRotationW = localRotation.w;
                exact.LocalScaleX = localScale.x;
                exact.LocalScaleY = localScale.y;
                exact.LocalScaleZ = localScale.z;
            }

            if (body != null)
            {
                Vector3 bodyPosition = body.position;
                Quaternion bodyRotation = body.rotation;
                exact.HasBodyPose = true;
                exact.BodyPositionX = bodyPosition.x;
                exact.BodyPositionY = bodyPosition.y;
                exact.BodyPositionZ = bodyPosition.z;
                exact.BodyRotationX = bodyRotation.x;
                exact.BodyRotationY = bodyRotation.y;
                exact.BodyRotationZ = bodyRotation.z;
                exact.BodyRotationW = bodyRotation.w;

                // A kinematic body rejects velocity reads with a warning, so only store them
                // for a body the game can actually move.
                if (!body.isKinematic)
                {
                    Vector3 velocity = body.velocity;
                    Vector3 angularVelocity = body.angularVelocity;
                    exact.HasBodyVelocity = true;
                    exact.VelocityX = velocity.x;
                    exact.VelocityY = velocity.y;
                    exact.VelocityZ = velocity.z;
                    exact.AngularVelocityX = angularVelocity.x;
                    exact.AngularVelocityY = angularVelocity.y;
                    exact.AngularVelocityZ = angularVelocity.z;
                }
            }

            if (snapshot != null && snapshot.HasCameraAngles)
            {
                exact.HasCameraAngles = true;
                exact.CameraPitch = snapshot.CameraPitch;
                exact.CameraYaw = snapshot.CameraYaw;
            }
            return exact;
        }

        static Vector3 ExactPosition(PlayerState snapshot)
        {
            if (snapshot == null)
                return Vector3.zero;
            PlayerExactState exact = snapshot.Exact;
            if (exact != null && exact.HasWorldPose)
                return new Vector3((float)exact.PositionX, (float)exact.PositionY, (float)exact.PositionZ);
            return snapshot.Position;
        }

        static Quaternion ExactRotation(PlayerState snapshot)
        {
            if (snapshot == null)
                return Quaternion.identity;
            PlayerExactState exact = snapshot.Exact;
            if (exact != null && exact.HasWorldPose)
                return new Quaternion((float)exact.RotationX, (float)exact.RotationY,
                                      (float)exact.RotationZ, (float)exact.RotationW);
            return snapshot.Rotation;
        }

        static Vector3 ExactBodyPosition(PlayerState snapshot)
        {
            if (snapshot == null)
                return Vector3.zero;
            PlayerExactState exact = snapshot.Exact;
            if (exact != null && exact.HasBodyPose)
                return new Vector3((float)exact.BodyPositionX, (float)exact.BodyPositionY,
                                   (float)exact.BodyPositionZ);
            return snapshot.BodyPosition;
        }

        static Quaternion ExactBodyRotation(PlayerState snapshot)
        {
            if (snapshot == null)
                return Quaternion.identity;
            PlayerExactState exact = snapshot.Exact;
            if (exact != null && exact.HasBodyPose)
                return new Quaternion((float)exact.BodyRotationX, (float)exact.BodyRotationY,
                                      (float)exact.BodyRotationZ, (float)exact.BodyRotationW);
            return snapshot.BodyRotation;
        }

        static string Exact(double value)
        {
            return value.ToString("G17", CultureInfo.InvariantCulture);
        }

        /// <summary>Freeze V1 before the restore writes anything.</summary>
        void BeginPlayerLoadFreeze(Savestate state)
        {
            // A second load must start from the real dynamic body, not from the kinematic body
            // of the hold it replaces: legacy states without a BodyState use that body as their
            // fallback when the hold is released.
            if (_playerLoadHeld)
                ReleasePlayerLoadHold();

            if (state == null || state.Player == null || !state.Player.HasPlayer)
                return;

            _playerLoadState = state;
            _playerLoadHeld = true;
            _playerLoadFallbackBody = null;
            try
            {
                Rigidbody body = _bridge.PlayerRigidbody;
                if (body == null)
                    return;
                // Keep the pre-load body so a legacy state without BodyState can still be
                // unfrozen to the settings the player had before the load.
                _playerLoadFallbackBody = CaptureBody(body);
                if (!body.isKinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
                body.isKinematic = true;
                try { body.Sleep(); }
                catch { }
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Player load freeze skipped: " + ex.Message);
            }
        }

        /// <summary>
        /// Re-pin V1 to the saved pose. Called at the end of every frame while the loaded state
        /// waits for its first tick, so game Updates cannot move or rotate him.
        /// </summary>
        public void MaintainPlayerLoadHold()
        {
            if (!_playerLoadHeld || _playerLoadState == null)
                return;
            PlayerState snapshot = _playerLoadState.Player;
            if (snapshot == null || !snapshot.HasPlayer)
                return;

            try
            {
                Rigidbody body = _bridge.PlayerRigidbody;
                if (body != null)
                {
                    if (!body.isKinematic)
                    {
                        body.velocity = Vector3.zero;
                        body.angularVelocity = Vector3.zero;
                    }
                    body.isKinematic = true;
                }
                ApplyExactPlayerPose(snapshot, true);
                ReassertPlayerShape(snapshot);
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Player load hold skipped: " + ex.Message);
            }
        }

        /// <summary>
        /// Writes the saved transform, local pose, Rigidbody pose and (optionally) the body
        /// pose. Local first, world last, so a parent that moved cannot change the saved
        /// world position.
        /// </summary>
        void ApplyExactPlayerPose(PlayerState snapshot, bool includeBody)
        {
            if (snapshot == null || !snapshot.HasPlayer)
                return;
            Component player = Resolve(snapshot.Movement) ?? _bridge.PlayerComponent;
            if (player == null)
                return;

            Transform transform = player.transform;
            if (snapshot.HasLocalPose)
            {
                Vector3 localPosition = snapshot.LocalPosition;
                Quaternion localRotation = snapshot.LocalRotation;
                Vector3 localScale = snapshot.LocalScale;
                PlayerExactState exact = snapshot.Exact;
                if (exact != null && exact.HasLocalPose)
                {
                    localPosition = new Vector3((float)exact.LocalPositionX, (float)exact.LocalPositionY,
                                                (float)exact.LocalPositionZ);
                    localRotation = new Quaternion((float)exact.LocalRotationX, (float)exact.LocalRotationY,
                                                   (float)exact.LocalRotationZ, (float)exact.LocalRotationW);
                    localScale = new Vector3((float)exact.LocalScaleX, (float)exact.LocalScaleY,
                                             (float)exact.LocalScaleZ);
                }
                transform.localPosition = localPosition;
                transform.localRotation = localRotation;
                transform.localScale = localScale;
            }
            transform.position = ExactPosition(snapshot);
            transform.rotation = ExactRotation(snapshot);
            Physics.SyncTransforms();

            if (!includeBody)
                return;
            Rigidbody body = _bridge.PlayerRigidbody;
            if (body == null || !snapshot.HasBodyPose)
                return;
            try
            {
                body.position = ExactBodyPosition(snapshot);
                body.rotation = ExactBodyRotation(snapshot);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Re-applies the parts of V1 the pose write does not cover: the crouched capsule,
        /// NewMovement's slide/crouch fields and the latched ground flags.
        ///
        /// These are written early by RestorePlayer / RestorePlayerExtra, but everything that
        /// runs after them (coroutine restarts in PostRestore, module restores, animator
        /// updates) can put the game's own crouch state back. The pose is re-applied late, the
        /// shape was not, so a checkpoint taken mid-slide resumed standing: the first tick runs
        /// with the standing capsule and crouch flags off, the game stands V1 up, and the rest
        /// of the run continues a crouch height (0.8 units) higher with its velocity untouched.
        /// Re-applying both together keeps the state the game reads consistent with the pose.
        /// </summary>
        void ReassertPlayerShape(PlayerState snapshot)
        {
            if (snapshot == null || !snapshot.HasPlayer)
                return;
            Component player = Resolve(snapshot.Movement) ?? _bridge.PlayerComponent;
            if (player == null)
                return;
            try
            {
                CapsuleCollider capsule = ReadMember(player, new[] { "playerCollider", "capsule" }) as CapsuleCollider;
                float before = capsule != null ? capsule.height : 0f;
                ApplyNamedFields(player, snapshot.SlideFields);
                RestorePlayerCapsule(player, snapshot);
                ApplyGroundFlags(player, snapshot.Ground);
                Physics.SyncTransforms();
                if (capsule != null && snapshot.HasCapsule &&
                    Math.Abs(before - snapshot.CapsuleHeight) > 0.0001f)
                {
                    _plugin.LogWarning("Player shape re-asserted before the resumed tick: capsule height " +
                                       before.ToString("0.000", CultureInfo.InvariantCulture) + " -> " +
                                       snapshot.CapsuleHeight.ToString("0.000", CultureInfo.InvariantCulture) +
                                       " (sliding=" + ReadBool(player, new[] { "sliding" }, false) +
                                       " crouching=" + ReadBool(player, new[] { "crouching" }, false) +
                                       "). The restore had left V1 in a different crouch state, which stands him up " +
                                       "by that much on the first tick.");
                }
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Player shape re-assert skipped: " + ex.Message);
            }
        }

        /// <summary>
        /// Called after the whole restore is complete. Re-applies animations and the exact
        /// pose after PostRestore (which restarts coroutines) and leaves V1 pinned.
        /// </summary>
        void FinishPlayerLoad(Savestate state)
        {
            if (!_playerLoadHeld || state == null || state.Player == null)
                return;
            try
            {
                // PostRestore restarts coroutines; their first segment can touch animators.
                RestoreAnimators(state.Player.Animators);
                ApplyExactPlayerPose(state.Player, true);
                ReassertPlayerShape(state.Player);
                if (state.Player.HasCameraAngles)
                    _bridge.SetCameraAngles(state.Player.CameraPitch, state.Player.CameraYaw);
                MaintainPlayerLoadHold();

                PlayerExactState exact = state.Player.Exact;
                _plugin.LogInfo("Player load hold: V1 frozen at tick " + state.Tick +
                                (exact != null && exact.HasWorldPose
                                    ? " pos (" + Exact(exact.PositionX) + ", " + Exact(exact.PositionY) + ", " +
                                      Exact(exact.PositionZ) + ")"
                                    : string.Empty) +
                                (exact != null && exact.HasBodyVelocity
                                    ? " vel (" + Exact(exact.VelocityX) + ", " + Exact(exact.VelocityY) + ", " +
                                      Exact(exact.VelocityZ) + ")"
                                    : string.Empty) +
                                " until the first resumed tick.");
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Player load hold finalization skipped: " + ex.Message);
            }
        }

        /// <summary>
        /// Put the exact saved body back and unfreeze. Runs immediately before the first
        /// resumed tick executes, after the load is completely finished.
        /// </summary>
        public void ReleasePlayerLoadHold()
        {
            if (!_playerLoadHeld)
                return;

            Savestate state = _playerLoadState;
            _playerLoadHeld = false;
            _playerLoadState = null;
            if (state == null || state.Player == null || !state.Player.HasPlayer)
            {
                _playerLoadFallbackBody = null;
                return;
            }

            PlayerState snapshot = state.Player;
            BodyState savedBody = snapshot.Body ?? _playerLoadFallbackBody;
            Rigidbody body = null;
            try
            {
                // Animations were written by FinishPlayerLoad while the hold was set; do not
                // run them again here, after the RNG restore, because Animator.Update can run
                // animation events. Restoring the exact pose is enough.
                ApplyExactPlayerPose(snapshot, false);

                body = _bridge.PlayerRigidbody;
                if (body != null)
                {
                    // Body position is writable while kinematic; the saved flag is put back by
                    // RestoreBody below.
                    try { body.isKinematic = true; }
                    catch { }
                    if (snapshot.HasBodyPose)
                    {
                        try
                        {
                            body.position = ExactBodyPosition(snapshot);
                            body.rotation = ExactBodyRotation(snapshot);
                        }
                        catch
                        {
                        }
                    }
                    if (savedBody != null)
                    {
                        if (savedBody.Body == null)
                            savedBody.Body = body;
                        RestoreBody(savedBody);
                        if (!body.isKinematic)
                        {
                            // RestoreBody sets these, but a kinematic-to-dynamic transition can
                            // discard the first velocity write on some Unity versions. Write the
                            // saved values once more after the body is dynamic.
                            body.velocity = savedBody.Velocity;
                            body.angularVelocity = savedBody.AngularVelocity;
                            if (savedBody.Sleeping)
                                body.Sleep();
                            else
                                body.WakeUp();
                        }
                    }
                    else
                    {
                        try { body.isKinematic = false; }
                        catch { }
                    }
                }

                // Last chance to keep the checkpoint's crouch: the first tick's physics runs
                // immediately after this, and a standing capsule there stands V1 up.
                ReassertPlayerShape(snapshot);

                if (snapshot.HasCameraAngles)
                    _bridge.SetCameraAngles(snapshot.CameraPitch, snapshot.CameraYaw);
                Physics.SyncTransforms();

                PlayerExactState exact = snapshot.Exact;
                _plugin.LogInfo("Player load hold released at tick " + state.Tick +
                                (exact != null && exact.HasBodyVelocity
                                    ? " vel (" + Exact(exact.VelocityX) + ", " + Exact(exact.VelocityY) + ", " +
                                      Exact(exact.VelocityZ) + ")"
                                    : string.Empty) +
                                " kinematic=" + (body != null ? body.isKinematic.ToString() : "?") + ".");
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Player load hold release skipped: " + ex.Message);
            }
            finally
            {
                _playerLoadFallbackBody = null;
            }
        }

        /// <summary>The position the held state will resume with, for diagnostics.</summary>
        public bool TryGetHeldPlayerPosition(out Vector3 position)
        {
            position = Vector3.zero;
            if (!_playerLoadHeld || _playerLoadState == null || _playerLoadState.Player == null)
                return false;
            position = ExactPosition(_playerLoadState.Player);
            return true;
        }

        /// <summary>
        /// The saved velocity while the body is held kinematic (a live read would report zero).
        /// </summary>
        public bool TryGetHeldPlayerVelocity(out Vector3 velocity)
        {
            velocity = Vector3.zero;
            if (!_playerLoadHeld || _playerLoadState == null || _playerLoadState.Player == null)
                return false;
            PlayerState snapshot = _playerLoadState.Player;
            PlayerExactState exact = snapshot.Exact;
            if (exact != null && exact.HasBodyVelocity)
            {
                velocity = new Vector3((float)exact.VelocityX, (float)exact.VelocityY,
                                       (float)exact.VelocityZ);
                return true;
            }
            BodyState body = snapshot.Body ?? _playerLoadFallbackBody;
            if (body != null)
            {
                velocity = body.Velocity;
                return true;
            }
            return false;
        }
    }
}
