using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine.XR.ARSubsystems;
using Unity.XR.CoreUtils;
using UnityEngine.SubsystemsImplementation;

namespace UnityEngine.XR.ARFoundation
{
    /// <summary>
    /// A generic manager for components generated by features detected in the physical environment.
    /// </summary>
    /// <remarks>
    /// When the manager is informed that a trackable has been added, a new <c>GameObject</c>
    /// is created with an <c>ARTrackable</c> component on it. If
    /// <see cref="ARTrackableManager{TSubsystem,TSubsystemDescriptor,TProvider,TSessionRelativeData,TTrackable}"/>
    /// is not null, then that Prefab will be instantiated.
    /// </remarks>
    /// <typeparam name="TSubsystem">The <c>Subsystem</c> which provides this manager data.</typeparam>
    /// <typeparam name="TSubsystemDescriptor">The <c>SubsystemDescriptor</c> required to create the Subsystem.</typeparam>
    /// <typeparam name="TProvider">The [provider](xref:UnityEngine.SubsystemsImplementation.SubsystemProvider) associated with the <typeparamref name="TSubsystem"/>.</typeparam>
    /// <typeparam name="TSessionRelativeData">A concrete struct used to hold data provided by the Subsystem.</typeparam>
    /// <typeparam name="TTrackable">The type of component that this component will manage (that is, create, update, and destroy).</typeparam>
    [RequireComponent(typeof(XROrigin))]
    [DisallowMultipleComponent]
    public abstract class ARTrackableManager<TSubsystem, TSubsystemDescriptor, TProvider, TSessionRelativeData, TTrackable>
        : SubsystemLifecycleManager<TSubsystem, TSubsystemDescriptor, TProvider>
        where TSubsystem : TrackingSubsystem<TSessionRelativeData, TSubsystem, TSubsystemDescriptor, TProvider>, new()
        where TSubsystemDescriptor : SubsystemDescriptorWithProvider<TSubsystem, TProvider>
        where TProvider : SubsystemProvider<TSubsystem>
        where TSessionRelativeData : struct, ITrackable
        where TTrackable : ARTrackable<TSessionRelativeData, TTrackable>
    {
        static List<TTrackable> s_Added = new List<TTrackable>();
        static List<TTrackable> s_Updated = new List<TTrackable>();
        static List<TTrackable> s_Removed = new List<TTrackable>();

        internal static ARTrackableManager<TSubsystem, TSubsystemDescriptor, TProvider, TSessionRelativeData, TTrackable> instance { get; private set; }

        /// <summary>
        /// A collection of all trackables managed by this component.
        /// </summary>
        public TrackableCollection<TTrackable> trackables => new(m_Trackables);

        /// <summary>
        /// Iterates over every instantiated <see cref="ARTrackable{TSessionRelativeData,TTrackable}"/> and
        /// activates or deactivates its <c>GameObject</c> based on the value of
        /// <paramref name="active"/>.
        /// This calls
        /// <a href="https://docs.unity3d.com/ScriptReference/GameObject.SetActive.html">GameObject.SetActive</a>
        /// on each trackable's <c>GameObject</c>.
        /// </summary>
        /// <param name="active">If <c>true</c> each trackable's <c>GameObject</c> is activated.
        /// Otherwise, it is deactivated.</param>
        public void SetTrackablesActive(bool active)
        {
            foreach (var trackable in trackables)
            {
                trackable.gameObject.SetActive(active);
            }
        }

        /// <summary>
        /// The <c>XROrigin</c> which will be used to instantiate detected trackables.
        /// </summary>
        protected XROrigin origin { get; private set; }

        /// <summary>
        /// The name prefix that should be used when instantiating new <c>GameObject</c>s.
        /// </summary>
        protected abstract string gameObjectName { get; }

        /// <summary>
        /// The Prefab that should be instantiated when adding a trackable. Can be `null`.
        /// </summary>
        /// <returns>The prefab should be instantiated when adding a trackable.</returns>
        protected virtual GameObject GetPrefab() => null;

        /// <summary>
        /// A dictionary of all trackables, keyed by <c>TrackableId</c>.
        /// </summary>
        protected Dictionary<TrackableId, TTrackable> m_Trackables = new();

        /// <summary>
        /// A dictionary of trackables added via <see cref="CreateTrackableImmediate(TSessionRelativeData)"/> but not yet reported as added.
        /// </summary>
        protected Dictionary<TrackableId, TTrackable> m_PendingAdds = new();

        /// <summary>
        /// Invoked by Unity once when this component wakes up.
        /// </summary>
        protected virtual void Awake()
        {
            origin = GetComponent<XROrigin>();
        }

        /// <inheritdoc />
        protected override void OnEnable()
        {
            base.OnEnable();
            instance = this;
            origin.TrackablesParentTransformChanged += OnTrackablesParentTransformChanged;
        }

        /// <inheritdoc />
        protected override void OnDisable()
        {
            base.OnDisable();
            origin.TrackablesParentTransformChanged -= OnTrackablesParentTransformChanged;
        }

        /// <summary>
        /// Determines whether an existing <see cref="ARTrackable{TSessionRelativeData,TTrackable}"/> can be added
        /// to the underlying subsystem.
        /// </summary>
        /// <remarks>
        /// If <paramref name="trackable"/> has not been added yet (that is, it is not tracked by this manager) and the
        /// manager is either disabled or does not have a valid subsystem, then the <paramref name="trackable"/>'s
        /// <see cref="ARTrackable{TSessionRelativeData,TTrackable}.pending"/> state is set to `true`.
        /// </remarks>
        /// <param name="trackable">An existing <see cref="ARTrackable{TSessionRelativeData,TTrackable}"/> to add to
        ///     the underlying subsystem.</param>
        /// <returns>Returns `true` if this manager is enabled, has a valid subsystem, and <paramref name="trackable"/>
        ///     is not already being tracked by this manager. Returns `false` otherwise.</returns>
        /// <exception cref="System.ArgumentNullException">Thrown if <paramref name="trackable"/> is `null`.</exception>
        protected bool CanBeAddedToSubsystem(TTrackable trackable)
        {
            if (trackable == null)
                throw new ArgumentNullException(nameof(trackable));

            // If it already has a valid trackableId, then don't re-add it
            if (!trackable.trackableId.Equals(TrackableId.invalidId))
                return false;

            // If we already know about it, then early out
            if (m_Trackables.ContainsKey(trackable.trackableId))
                return false;

            if (!enabled || subsystem == null)
            {
                // If the manager is disabled or there is no subsystem, and we don't already know about
                // this trackable, then it must be pending.
                trackable.pending = true;
                return false;
            }

            // Finally, we can only add it if we have a XR origin.
            return origin && origin.TrackablesParent;
        }

        void OnTrackablesParentTransformChanged(ARTrackablesParentTransformChangedEventArgs eventArgs)
        {
            foreach (var trackable in trackables)
            {
                var transform = trackable.transform;
                if (transform.parent != eventArgs.TrackablesParent)
                {
                    var desiredPose = eventArgs.TrackablesParent.TransformPose(trackable.pose);
                    transform.SetPositionAndRotation(desiredPose.position, desiredPose.rotation);
                }
            }
        }

        /// <summary>
        /// Update is called once per frame. This component's internal state
        /// is first updated, and then an event notifying whether any trackables have been added, removed, or updated
        /// is invoked by the derived manager.
        /// </summary>
        protected virtual void Update()
        {
            if (subsystem == null || !subsystem.running)
                return;

            using (new ScopedProfiler("GetChanges"))
            using (var changes = subsystem.GetChanges(Allocator.Temp))
            {
                using (new ScopedProfiler("ProcessAdded"))
                {
                    ClearAndSetCapacity(s_Added, changes.added.Length);
                    foreach (var added in changes.added)
                    {
                        s_Added.Add(CreateOrUpdateTrackable(added));
                    }
                }

                using (new ScopedProfiler("ProcessUpdated"))
                {
                    ClearAndSetCapacity(s_Updated, changes.updated.Length);
                    foreach (var updated in changes.updated)
                    {
                        s_Updated.Add(CreateOrUpdateTrackable(updated));
                    }
                }

                using (new ScopedProfiler("ProcessRemoved"))
                {
                    ClearAndSetCapacity(s_Removed, changes.removed.Length);
                    foreach (var trackableId in changes.removed)
                    {
                        if (m_Trackables.TryGetValue(trackableId, out var trackable))
                        {
                            m_Trackables.Remove(trackableId);
                            if (trackable)
                            {
                                s_Removed.Add(trackable);
                            }
                        }
                    }
                }
            }

            try
            {
                // User events
                if ((s_Added.Count) > 0 ||
                    (s_Updated.Count) > 0 ||
                    (s_Removed.Count) > 0)
                {
                    OnTrackablesChanged(s_Added, s_Updated, s_Removed);
                }
            }
            finally
            {
                // Make sure destroy happens even if a user callback throws an exception
                foreach (var removed in s_Removed)
                    DestroyTrackable(removed);
            }
        }

        /// <summary>
        /// Invoked when trackables have change (that is, they were added, updated, or removed).
        /// Use this to perform additional logic, or to invoke public events
        /// related to your trackables.
        /// </summary>
        /// <param name="added">A list of trackables added this frame.</param>
        /// <param name="updated">A list of trackables updated this frame.</param>
        /// <param name="removed">A list of trackables removed this frame.
        /// The trackable components are not destroyed until after this method returns.</param>
        protected virtual void OnTrackablesChanged(
            List<TTrackable> added,
            List<TTrackable> updated,
            List<TTrackable> removed)
        { }

        /// <summary>
        /// Invoked after creating the trackable. The trackable's <c>sessionRelativeData</c> property will already be set.
        /// </summary>
        /// <param name="trackable">The newly created trackable.</param>
        protected virtual void OnCreateTrackable(TTrackable trackable) { }

        /// <summary>
        /// Invoked just after session-relative data has been set on a trackable.
        /// </summary>
        /// <param name="trackable">The trackable that has just been updated.</param>
        /// <param name="sessionRelativeData">The session relative data used to update the trackable.</param>
        protected virtual void OnAfterSetSessionRelativeData(
            TTrackable trackable,
            TSessionRelativeData sessionRelativeData)
        { }

        /// <summary>
        /// Creates a <typeparamref name="TTrackable"/> immediately and leaves it in a "pending" state.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Trackables are usually created, updated, or destroyed during <see cref="Update()"/>.
        /// This method creates a trackable immediately and marks it as "pending"
        /// until it is reported as added by the subsystem. This is useful for subsystems that deal
        /// with trackables that can be both detected and manually created.
        /// </para><para>
        /// This method does not invoke <see cref="OnTrackablesChanged(List{TTrackable}, List{TTrackable}, List{TTrackable})"/>,
        /// so no "added" notifications will occur until the next call to <see cref="Update"/>.
        /// </para><para>
        /// The trackable will appear in the <see cref="trackables"/> collection immediately.
        /// </para>
        /// </remarks>
        /// <param name="sessionRelativeData">The data associated with the trackable. All spatial data should
        /// be relative to the <see cref="XROrigin"/>.</param>
        /// <returns>A new <c>TTrackable</c></returns>
        protected TTrackable CreateTrackableImmediate(TSessionRelativeData sessionRelativeData)
        {
            var trackable = CreateOrUpdateTrackable(sessionRelativeData);
            trackable.pending = true;
            m_PendingAdds.Add(trackable.trackableId, trackable);
            return trackable;
        }

        /// <summary>
        /// If in a "pending" state and <see cref="ARTrackable{TSessionRelativeData, TTrackable}.destroyOnRemoval"/> is
        /// `true`, this method destroys the trackable's <c>GameObject</c>. Otherwise, this method has no effect.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Trackables are usually removed only when the subsystem reports they
        /// have been removed during <see cref="Update"/>.
        /// </para><para>
        /// This method will immediately remove a trackable only if it was created by
        /// <see cref="CreateTrackableImmediate(TSessionRelativeData)"/>
        /// and has not yet been reported as added by the
        /// <see cref="SubsystemLifecycleManager{TSubsystem,TSubsystemDescriptor,TProvider}.subsystem"/>.
        /// </para><para>
        /// This can happen if the trackable is created and removed within the same frame, as the subsystem might never
        /// have a chance to report its existence. Derived classes should use this
        /// if they support the concept of manual addition and removal of trackables, as there might not
        /// be a removal event if the trackable is added and removed quickly.
        /// </para><para>
        /// If the trackable is not in a pending state (that is, it has already been reported as "added"),
        /// then this method does nothing.
        /// </para>
        /// <para>
        /// This method does not invoke <see cref="OnTrackablesChanged(List{TTrackable}, List{TTrackable}, List{TTrackable})"/>,
        /// so no "removed" notifications will occur until the next call to <see cref="Update"/>. "Removed" notifications will only
        /// occur if it was previously reported as "added".
        /// </para>
        /// </remarks>
        /// <param name="trackableId">The id of the trackable to destroy.</param>
        /// <returns><c>True</c> if the trackable is "pending" (that is, not yet reported as "added").</returns>
        protected bool DestroyPendingTrackable(TrackableId trackableId)
        {
            if (m_PendingAdds.TryGetValue(trackableId, out var trackable))
            {
                m_PendingAdds.Remove(trackableId);
                m_Trackables.Remove(trackableId);
                DestroyTrackable(trackable);
                return true;
            }

            return false;
        }

        static void ClearAndSetCapacity(List<TTrackable> list, int capacity)
        {
            list.Clear();
            if (list.Capacity < capacity)
                list.Capacity = capacity;
        }

        string GetTrackableName(TrackableId trackableId)
        {
            return gameObjectName + " " + trackableId;
        }

        (GameObject gameObject, bool shouldBeActive) CreateGameObjectDeactivated()
        {
            var prefab = GetPrefab();
            if (prefab == null)
            {
                var gameObject = new GameObject();
                gameObject.SetActive(false);
                gameObject.transform.parent = origin.TrackablesParent;
                return (gameObject, true);
            }
            else
            {
                var active = prefab.activeSelf;
                prefab.SetActive(false);
                var gameObject = Instantiate(prefab, origin.TrackablesParent);
                prefab.SetActive(active);
                return (gameObject, active);
            }
        }

        (GameObject gameObject, bool shouldBeActive) CreateGameObjectDeactivated(string name)
        {
            var tuple = CreateGameObjectDeactivated();
            tuple.gameObject.name = name;
            return tuple;
        }

        (GameObject gameObject, bool shouldBeActive) CreateGameObjectDeactivated(TrackableId trackableId)
        {
            using (new ScopedProfiler("CreateGameObject"))
            {
                return CreateGameObjectDeactivated(GetTrackableName(trackableId));
            }
        }

        TTrackable CreateTrackable(TSessionRelativeData sessionRelativeData)
        {
            var (gameObject, shouldBeActive) = CreateGameObjectDeactivated(sessionRelativeData.trackableId);
            var trackable = gameObject.GetComponent<TTrackable>();
            if (trackable == null)
            {
                trackable = gameObject.AddComponent<TTrackable>();
            }

            m_Trackables.Add(sessionRelativeData.trackableId, trackable);
            SetSessionRelativeData(trackable, sessionRelativeData);
            trackable.gameObject.SetActive(shouldBeActive);

            return trackable;
        }

        void SetSessionRelativeData(TTrackable trackable, TSessionRelativeData data)
        {
            trackable.SetSessionRelativeData(data);
            var worldSpacePose = origin.TrackablesParent.TransformPose(data.pose);
            trackable.transform.SetPositionAndRotation(worldSpacePose.position, worldSpacePose.rotation);
        }

        /// <summary>
        /// Creates the native counterpart for an existing <see cref="ARTrackable{TSessionRelativeData,TTrackable}"/>
        /// added with a call to [AddComponent](xref:UnityEngine.GameObject.AddComponent), for example.
        /// </summary>
        /// <param name="existingTrackable">The existing trackable component.</param>
        /// <param name="sessionRelativeData">The AR data associated with the trackable. This usually comes from the
        /// trackable's associated [subsystem](xref:UnityEngine.Subsystem)</param>
        protected void CreateTrackableFromExisting(TTrackable existingTrackable, TSessionRelativeData sessionRelativeData)
        {
            // Same as CreateOrUpdateTrackable
            var trackableId = sessionRelativeData.trackableId;
            m_Trackables.Add(trackableId, existingTrackable);
            SetSessionRelativeData(existingTrackable, sessionRelativeData);
            OnCreateTrackable(existingTrackable);
            OnAfterSetSessionRelativeData(existingTrackable, sessionRelativeData);
            existingTrackable.OnAfterSetSessionRelativeData();

            // Remaining logic from CreateTrackableImmediate
            m_PendingAdds.Add(trackableId, existingTrackable);
            existingTrackable.pending = true;
        }

        TTrackable CreateOrUpdateTrackable(TSessionRelativeData sessionRelativeData)
        {
            var trackableId = sessionRelativeData.trackableId;
            if (m_Trackables.TryGetValue(trackableId, out var trackable))
            {
                m_PendingAdds.Remove(trackableId);
                trackable.pending = false;
                SetSessionRelativeData(trackable, sessionRelativeData);
            }
            else
            {
                trackable = CreateTrackable(sessionRelativeData);
                OnCreateTrackable(trackable);
            }

            OnAfterSetSessionRelativeData(trackable, sessionRelativeData);
            trackable.OnAfterSetSessionRelativeData();
            return trackable;
        }

        void DestroyTrackable(TTrackable trackable)
        {
            if (trackable.destroyOnRemoval)
            {
                Destroy(trackable.gameObject);
            }
        }
    }
}
