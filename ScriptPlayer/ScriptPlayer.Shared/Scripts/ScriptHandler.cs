﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace ScriptPlayer.Shared.Scripts
{
    public class ScriptHandler
    {
        public event EventHandler<PositionCollection> PositionsChanged;
        private int _lastIndex;
        private TimeSpan _lastTimestamp;
        private List<ScriptAction> _actions;
        private TimeSource _timesource;
        private ConversionMode _conversionMode = ConversionMode.UpOrDown;
        private static TimeSpan _delay = TimeSpan.Zero;
        private List<TimeSpan> _beats;
        private List<ScriptAction> _unfilledActions;
        private bool _fillGaps;
        public event EventHandler<ScriptActionEventArgs> ScriptActionRaised;
        public event EventHandler<IntermediateScriptActionEventArgs> IntermediateScriptActionRaised;

        public TimeSpan Delay { get; set; }

        public ConversionMode ConversionMode  
        {
            get => _conversionMode;
            set
            {
                _conversionMode = value;
                ProcessScript();
            }
        }

        public bool FillGaps
        {
            get => _fillGaps;
            set
            {
                _fillGaps = value;
                ProcessScript();
            }
        }

        public void Clear()
        {
            _actions?.Clear();
            _beats?.Clear();
            ProcessScript();
        }

        private void SaveBeatFile()
        {
            if (_actions.FirstOrDefault() is BeatScriptAction)
            {
                _beats = _actions.Select(a => a.TimeStamp).ToList();
            }
            else
            {
                _beats = null;
            }
        }

        private void ConvertBeatFile()
        {
            if (_beats == null)
                return;

            _actions = BeatsToFunScriptConverter.Convert(_beats, _conversionMode)
                .OrderBy(a => a.TimeStamp)
                .Cast<ScriptAction>()
                .ToList();

            ProcessScript();
        }

        private void UpdatePositions()
        {
            PositionCollection collection = new PositionCollection(_actions.OfType<FunScriptAction>().Select(f => new TimedPosition
            {
                Position = f.Position,
                TimeStamp = f.TimeStamp
            }));

            OnPositionsChanged(collection);
        }

        public ScriptHandler()
        {
            _actions = new List<ScriptAction>();
            Delay = new TimeSpan(0);
        }

        protected virtual void OnScriptActionRaised(ScriptActionEventArgs e)
        {
            ScriptActionRaised?.Invoke(this, e);
        }

        protected virtual void OnIntermediateScriptActionRaised(IntermediateScriptActionEventArgs e)
        {
            IntermediateScriptActionRaised?.Invoke(this, e);
        }

        public IEnumerable<ScriptAction> GetScript()
        {
            if (_actions == null)
                return new List<ScriptAction>();

            return _actions.AsReadOnly();
        }

        public IEnumerable<ScriptAction> GetUnfilledScript()
        {
            if (_unfilledActions == null)
                return new List<ScriptAction>();

            return _unfilledActions.AsReadOnly();
        } 

        public void SetScript(IEnumerable<ScriptAction> script)
        {
            var actions = new List<ScriptAction>(script);
            actions.Sort((a, b) => a.TimeStamp.CompareTo(b.TimeStamp));

            _actions = actions;
            
            SaveBeatFile();
            ConvertBeatFile();

            ProcessScript();
        }

        private void ProcessScript()
        {
            FillScriptGaps();
            UpdatePositions();
            ResetCache();
        }

        private void FillScriptGaps()
        {
            _unfilledActions = new List<ScriptAction>(_actions);

            if (!FillGaps) return;

            List<ScriptAction> additionalActions = new List<ScriptAction>();

            TimeSpan previous = TimeSpan.MinValue;
            foreach (ScriptAction action in _actions)
            {
                if (previous != TimeSpan.MinValue)
                {
                    TimeSpan duration = action.TimeStamp - previous;
                    if (duration > TimeSpan.FromSeconds(10))
                    {
                        TimeSpan start = previous + TimeSpan.FromSeconds(2);
                        TimeSpan end = action.TimeStamp - TimeSpan.FromSeconds(2);
                        TimeSpan gapduration = end - start;

                        int fillers = (int)Math.Round(gapduration.Divide(TimeSpan.FromMilliseconds(500)));

                        bool up = true;

                        for (int i = 0; i <= fillers; i++)
                        {
                            up ^= true;
                            additionalActions.Add(new FunScriptAction
                            {
                                Position = (byte)(up ? 99 : 0), 
                                TimeStamp = start + gapduration.Multiply(i).Divide(fillers)
                            });
                        }
                    }
                }

                previous = action.TimeStamp;
            }

            _actions.AddRange(additionalActions);
            _actions.Sort((a,b) => a.TimeStamp.CompareTo(b.TimeStamp));
        }

        public void SetTimesource(TimeSource timesource)
        {
            if (_timesource != null)
                _timesource.ProgressChanged -= TimesourceOnProgressChanged;

            _timesource = timesource;

            if (_timesource != null)
                _timesource.ProgressChanged += TimesourceOnProgressChanged;
        }

        private void TimesourceOnProgressChanged(object sender, TimeSpan timeSpan)
        {
            CheckLastActionAfter(timeSpan - _delay);
        }

        private void ResetCache()
        {
            _lastIndex = -1;
            _lastTimestamp = new TimeSpan(0);
        }

        private void CheckLastActionAfter(TimeSpan newTimeSpan)
        {
            if (newTimeSpan < _lastTimestamp)
                ResetCache();

            int passedIndex = -1;

            if (_actions == null) return;

            for (int i = _lastIndex + 1; i < _actions.Count; i++)
            {
                if (GetTimestamp(i) > newTimeSpan)
                    break;

                if (GetTimestamp(i) <= newTimeSpan)
                    passedIndex = i;
            }

            if (passedIndex >= 0)
            {
                _lastIndex = passedIndex;
                _lastTimestamp = GetTimestamp(passedIndex);

                ScriptActionEventArgs args = new ScriptActionEventArgs(_actions[passedIndex]);

                if (passedIndex + 1 < _actions.Count)
                    args.RawNextAction = _actions[passedIndex + 1];

                if (passedIndex > 0)
                    args.RawPreviousAction = _actions[passedIndex - 1];

                OnScriptActionRaised(args);
            }
            else if (_lastIndex >= 0 && _lastIndex < _actions.Count - 1)
            {
                TimeSpan previous = GetTimestamp(_lastIndex);
                TimeSpan next = GetTimestamp(_lastIndex + 1);

                double progress = (newTimeSpan - previous).Divide(next - previous);

                if (progress >= 0.0 && progress <= 1.0)
                {
                    IntermediateScriptActionEventArgs args = new IntermediateScriptActionEventArgs(_actions[_lastIndex],_actions[_lastIndex + 1], progress);
                    OnIntermediateScriptActionRaised(args);
                }
            }
        }

        private TimeSpan GetTimestamp(int i)
        {
            return _actions[i].TimeStamp + Delay;
        }

        public static void SetDelay(TimeSpan delay)
        {
            _delay = delay;
        }

        public ScriptAction FirstEventAfter(TimeSpan currentPosition)
        {
            return _actions?.FirstOrDefault(a => a.TimeStamp > currentPosition);
        }

        public TimeSpan GetDuration()
        {
            if (_actions == null)
                return TimeSpan.Zero;

            if (_actions.Count == 0)
                return TimeSpan.Zero;

            return _actions.Max(a => a.TimeStamp);
        }

        protected virtual void OnPositionsChanged(PositionCollection e)
        {
            PositionsChanged?.Invoke(this, e);
        }
    }

    public class IntermediateScriptActionEventArgs : EventArgs
    {
        public double Progress;
        public ScriptAction RawPreviousAction;
        public ScriptAction RawNextAction;

        public IntermediateScriptActionEventArgs(ScriptAction previous, ScriptAction next, double progress)
        {
            RawPreviousAction = previous;
            RawNextAction = next;
            Progress = progress;
        }

        public IntermediateScriptActionEventArgs<T> Cast<T>() where T : ScriptAction
        {
            return new IntermediateScriptActionEventArgs<T>(RawPreviousAction as T, RawNextAction as T, Progress);
        }
    }

    public class ScriptActionEventArgs : EventArgs
    {
        public ScriptAction RawPreviousAction;
        public ScriptAction RawCurrentAction;
        public ScriptAction RawNextAction;

        public ScriptActionEventArgs(ScriptAction previous, ScriptAction current, ScriptAction next)
        {
            RawPreviousAction = previous;
            RawCurrentAction = current;
            RawNextAction = next;
        }

        public ScriptActionEventArgs(ScriptAction current)
        {
            RawPreviousAction = null;
            RawCurrentAction = current;
            RawNextAction = null;
        }

        public ScriptActionEventArgs<T> Cast<T>() where T : ScriptAction
        {
            return new ScriptActionEventArgs<T>(RawPreviousAction as T, RawCurrentAction as T, RawNextAction as T);
        }
    }
}
