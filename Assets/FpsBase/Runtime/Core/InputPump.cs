using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.PlayerLoop;

namespace FpsBase
{
    // Pumps before catch-up ticks so both simulation and UI can see the pending input, and places
    // each event in game time so ticks can consume input as of their own moment.
    public static class InputPump
    {
        public const double TimeTolerance = 1e-9;

        // A pump's events from one device count as clustered when their timestamps span less than
        // this share of the frame. Genuine timestamps spread across the whole frame.
        private const double ClusterShare = 0.1;
        // Frames shorter than this are too short to tell clustered from genuine timestamps.
        private const double MinClassifyWindow = 0.004;
        private const float ClusterEvidenceRate = 0.1f;

        private struct Pump { }

        private struct Batch
        {
            public int Count;
            public double Min;
            public double Max;
        }

        private static readonly Dictionary<int, Batch> Batches = new();
        // Per device: running share of pumps whose events were clustered. Devices start out treated
        // as clustered, the safe assumption, until their own timestamps show otherwise.
        private static readonly Dictionary<int, float> ClusteredShare = new();
        private static InputSettings.UpdateMode previousMode;
        private static bool previousEventMergingDisabled;
        private static bool pumping;
        private static double pumpEventTime;
        private static double previousPumpEventTime;

        public static event Action Pumped;

        public static bool GameplayActive => Time.timeScale > 0f && Cursor.lockState == CursorLockMode.Locked;

        // Game time of this frame's pump and the previous one: the span this frame's input covers.
        public static double SampleTime { get; private set; }

        public static double PreviousSampleTime { get; private set; }

        // Windows can stamp a whole frame's reports with the moment Unity drains its message queue
        // rather than when they happened. Such a device's timestamps carry no timing within the
        // frame, so callers place its events themselves.
        public static bool HasClusteredTimestamps(InputDevice device)
        {
            return !ClusteredShare.TryGetValue(device.deviceId, out float share) || share >= 0.5f;
        }

        // Game time for a discrete event such as a key or button change. A clustered device's event
        // happened at an unknown moment in the frame, so it goes to the frame's midpoint: unbiased,
        // and unlike the frame's end it still reaches this frame's later ticks.
        public static double EventGameTime(double eventTime, InputDevice device)
        {
            return ToGameTime(HasClusteredTimestamps(device) ? (previousPumpEventTime + pumpEventTime) / 2.0 : eventTime);
        }

        public static double ToGameTime(double eventTime)
        {
            double gameTime = Time.timeAsDouble + (eventTime - Time.unscaledTimeAsDouble) * Time.timeScale;
            // Clock conversion noise should not move an event across a tick or shot boundary.
            gameTime = Math.Round(gameTime * 1_000_000.0) / 1_000_000.0;
            return Math.Max(Time.fixedTimeAsDouble, gameTime);
        }

        public class Timeline<T>
        {
            private readonly List<(double Time, T Value)> samples = new();
            private int first;

            public int Count => samples.Count - first;

            public (double Time, T Value) this[int index] => samples[first + index];

            public void Add(double time, T value)
            {
                int index = samples.Count;
                while (index > first && samples[index - 1].Time > time)
                {
                    index--;
                }
                samples.Insert(index, (time, value));
            }

            public bool TryRead(double through, out (double Time, T Value) sample)
            {
                if (Count == 0 || samples[first].Time - through > TimeTolerance)
                {
                    sample = default;
                    return false;
                }

                sample = samples[first++];
                if (first >= Count)
                {
                    samples.RemoveRange(0, first);
                    first = 0;
                }
                return true;
            }

            public void Clear()
            {
                samples.Clear();
                first = 0;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            Pumped = null;
            Batches.Clear();
            ClusteredShare.Clear();
            pumping = false;
            pumpEventTime = 0.0;
            previousPumpEventTime = 0.0;
            SampleTime = 0.0;
            PreviousSampleTime = 0.0;
        }

        // Flips the mode on the live settings object instead of swapping in a copy: the Input
        // System's own editor play-mode hooks expect that object to survive, and swapping broke
        // input after leaving play mode. The previous mode comes back on quit, so a project
        // settings asset is never left changed.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            InputSettings settings = InputSystem.settings;
            previousMode = settings.updateMode;
            previousEventMergingDisabled = settings.disableRedundantEventsMerging;
            settings.updateMode = InputSettings.UpdateMode.ProcessEventsManually;
            // A merged mouse report can straddle two ticks and lose the earlier timestamp.
            settings.disableRedundantEventsMerging = true;
            PlayerLoopUtility.AddLast(typeof(EarlyUpdate), typeof(Pump), PumpInput);
            InputSystem.onEvent += MeasureEvent;
            Application.quitting += Uninstall;
        }

        // Time has already advanced to this frame in the TimeUpdate phase.
        private static void PumpInput()
        {
            PreviousSampleTime = SampleTime;
            SampleTime = Time.timeAsDouble;
            previousPumpEventTime = pumpEventTime;
            pumpEventTime = InputState.currentTime;

            pumping = true;
            InputSystem.Update();
            pumping = false;
            ClassifyBatches();

            Reflex.SetMarker(ReflexMarker.InputSample);
            Pumped?.Invoke();
        }

        // Only events processed by this pump count; the editor runs its own input updates too.
        private static void MeasureEvent(InputEventPtr input, InputDevice device)
        {
            if (!pumping || device == null || (!input.IsA<StateEvent>() && !input.IsA<DeltaStateEvent>()))
            {
                return;
            }

            double time = input.time;
            Batches.TryGetValue(device.deviceId, out Batch batch);
            batch.Min = batch.Count == 0 ? time : Math.Min(batch.Min, time);
            batch.Max = batch.Count == 0 ? time : Math.Max(batch.Max, time);
            batch.Count++;
            Batches[device.deviceId] = batch;
        }

        // One verdict per device per pump, from the spread of that pump's timestamps. A single event
        // carries no spread, so it leaves the device's classification as it was.
        private static void ClassifyBatches()
        {
            double window = pumpEventTime - previousPumpEventTime;
            if (window >= MinClassifyWindow)
            {
                foreach (KeyValuePair<int, Batch> entry in Batches)
                {
                    if (entry.Value.Count < 2)
                    {
                        continue;
                    }

                    float clustered = entry.Value.Max - entry.Value.Min < ClusterShare * window ? 1f : 0f;
                    float share = ClusteredShare.TryGetValue(entry.Key, out float previous) ? previous : 1f;
                    ClusteredShare[entry.Key] = share + ClusterEvidenceRate * (clustered - share);
                }
            }
            Batches.Clear();
        }

        private static void Uninstall()
        {
            Application.quitting -= Uninstall;
            InputSystem.onEvent -= MeasureEvent;
            PlayerLoopUtility.Remove(typeof(Pump));
            InputSettings settings = InputSystem.settings;
            if (settings != null)
            {
                settings.updateMode = previousMode;
                settings.disableRedundantEventsMerging = previousEventMergingDisabled;
            }
        }
    }
}
