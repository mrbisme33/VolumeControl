using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace VolumeControl
{
    internal sealed class SessionSnapshot
    {
        public string Exe, Path, CommandLine, DeviceId;
        public bool IsSystem, IsActive, Muted;
        public int Percent;
    }

    internal sealed class OutputDevice
    {
        public string Id, Name;
        public bool IsDefault;
    }

    /// <summary>
    /// Owns all Core Audio objects on one MTA thread. COM callbacks only queue work here:
    /// Windows forbids changing session registrations from inside a callback.
    /// </summary>
    internal sealed class AudioEngine
    {
        private const int Render = 0, DeviceStateActive = 1, ClsCtxAll = 23, RoleMultimedia = 1, VtLpwstr = 31;
        private const int SessionActive = 1, SessionExpired = 2;
        // Apps often set their own level while starting. Changes this soon are not treated as user edits.
        private const int StartupGraceMs = 3000;

        // Marks volume changes as ours, so P7 can tell them apart from Volume Mixer edits.
        private static readonly Guid OurContext = new Guid("5C1E7A02-9E0B-4F6B-9A34-6F3C1D2B7A11");

        private readonly ConfigStore _store;
        private readonly string _onlyExe;
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Dictionary<string, DeviceEntry> _devices = new Dictionary<string, DeviceEntry>();
        private readonly HashSet<string> _notified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly DeviceWatcher _deviceWatcher;
        private readonly Thread _thread;
        private IMMDeviceEnumerator _enumerator;
        private bool _paused;
        private volatile List<SessionSnapshot> _snapshot = new List<SessionSnapshot>();
        private volatile List<OutputDevice> _outputs = new List<OutputDevice>();
        private readonly Dictionary<uint, string> _routes = new Dictionary<uint, string>();
        private AppRouting _routing;
        private volatile bool _routingAvailable;

        public event Action Changed;
        public event Action<string> AutoMuted;

        public bool Paused => Volatile.Read(ref _paused);
        public IReadOnlyList<SessionSnapshot> Sessions => _snapshot;
        public IReadOnlyList<OutputDevice> Outputs => _outputs;
        public bool PerAppOutputAvailable => _routingAvailable;

        /// <summary>Makes a device the Windows default output for all roles.</summary>
        public void SetOutput(string id) => Post(() =>
        {
            var policy = (IPolicyConfig)new PolicyConfigCom();
            for (int role = 0; role < 3; role++) // console, multimedia, communications
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
            RefreshOutputs();
            Publish();
        });

        private void RefreshOutputs()
        {
            string defaultId = null;
            if (_enumerator.GetDefaultAudioEndpoint(Render, RoleMultimedia, out var current) == 0)
                current.GetId(out defaultId);

            var list = new List<OutputDevice>();
            if (_enumerator.EnumAudioEndpoints(Render, DeviceStateActive, out var devices) == 0)
            {
                devices.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        devices.Item(i, out var device);
                        device.GetId(out string id);
                        list.Add(new OutputDevice { Id = id, Name = FriendlyName(device) ?? id, IsDefault = id == defaultId });
                    }
                    catch (Exception) { }
                }
            }
            _outputs = list.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string FriendlyName(IMMDevice device)
        {
            if (device.OpenPropertyStore(0, out var props) != 0) return null;
            var key = new PropertyKey { Fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Pid = 14 };
            if (props.GetValue(ref key, out var value) != 0) return null;
            try { return value.Type == VtLpwstr ? Marshal.PtrToStringUni(value.Value) : null; }
            finally { PropVariantClear(ref value); }
        }

        [DllImport("ole32")]
        private static extern int PropVariantClear(ref PropVariant value);

        /// <param name="onlyExe">Test hook: manage only this exe and leave every other app alone.</param>
        public AudioEngine(ConfigStore store, bool startPaused = false, string onlyExe = null)
        {
            _store = store;
            _paused = startPaused;
            _onlyExe = onlyExe;
            _deviceWatcher = new DeviceWatcher(this);
            _thread = new Thread(Loop) { IsBackground = true, Name = "AudioEngine" };
            _thread.SetApartmentState(ApartmentState.MTA);
        }

        public void Start()
        {
            _thread.Start();
            Post(() =>
            {
                _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                _enumerator.RegisterEndpointNotificationCallback(_deviceWatcher);
                _routing = new AppRouting();
                _routingAvailable = _routing.Available;
                ScanAll();
            });
        }

        public void Post(Action action)
        {
            try { if (!_queue.IsAddingCompleted) _queue.Add(action); }
            catch (InvalidOperationException) { /* shutting down */ }
        }

        /// <summary>Blocks until everything queued so far has run.</summary>
        public void Flush()
        {
            using (var done = new ManualResetEventSlim())
            {
                Post(done.Set);
                done.Wait(5000);
            }
        }

        public void SetPaused(bool paused) => Post(() =>
        {
            if (_paused == paused) return;
            _paused = paused;
            if (paused) RestoreManaged(null);
            else ApplyAll();
            Publish();
        });

        public void Reapply() => Post(() => { ApplyAll(); Publish(); });

        /// <summary>Resets every current session of the given apps to 100% and unmuted (I5).</summary>
        public void RestoreApps(IEnumerable<string> exes)
        {
            var set = new HashSet<string>(exes, StringComparer.OrdinalIgnoreCase);
            Post(() => RestoreManaged(set));
            Flush();
        }

        public void Stop()
        {
            Post(() =>
            {
                RestoreManaged(null);
                foreach (var d in _devices.Values.ToList()) Unregister(d);
                _devices.Clear();
                try { _enumerator?.UnregisterEndpointNotificationCallback(_deviceWatcher); }
                catch (Exception) { }
            });
            _queue.CompleteAdding();
            _thread.Join(5000);
        }

        private void Loop()
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                // A vanished device or process must never take the tray app down (B5).
                try { action(); }
                catch (Exception) { }
            }
        }

        private void ScanAll()
        {
            if (_enumerator == null) return;
            var present = new HashSet<string>();
            if (_enumerator.EnumAudioEndpoints(Render, DeviceStateActive, out var devices) == 0)
            {
                devices.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        devices.Item(i, out var device);
                        device.GetId(out string id);
                        present.Add(id);
                        if (!_devices.ContainsKey(id)) AddDevice(id, device);
                    }
                    catch (Exception) { }
                }
            }
            foreach (var gone in _devices.Keys.Where(id => !present.Contains(id)).ToList())
            {
                Unregister(_devices[gone]);
                _devices.Remove(gone);
            }
            RefreshOutputs(); // Routing below needs to know which outputs exist.
            foreach (var d in _devices.Values) ScanSessions(d);
            foreach (var s in AllSessions.ToList())
            {
                try { ApplyRoute(s); }
                catch (Exception) { }
            }
            Publish();
        }

        private void AddDevice(string id, IMMDevice device)
        {
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object obj) != 0) return;
            var entry = new DeviceEntry(this, (IAudioSessionManager2)obj, id);
            entry.Manager.RegisterSessionNotification(entry);
            _devices[id] = entry;
        }

        private void ScanSessions(DeviceEntry device)
        {
            // Windows only sends new-session notifications once GetSessionEnumerator has been called.
            if (device.Manager.GetSessionEnumerator(out var sessions) != 0) return;
            sessions.GetCount(out int count);
            var seen = new HashSet<string>();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    sessions.GetSession(i, out var control);
                    control.GetState(out int state);
                    if (state == SessionExpired) continue;
                    control.GetSessionInstanceIdentifier(out string id);
                    seen.Add(id);
                    if (device.Sessions.TryGetValue(id, out var known))
                    {
                        known.IsActive = state == SessionActive;
                        continue;
                    }
                    var tracked = CreateSession(control, state);
                    tracked.DeviceId = device.Id;
                    device.Sessions[id] = tracked;
                    control.RegisterAudioSessionNotification(tracked);
                    Apply(tracked, firstTime: true);
                }
                catch (Exception) { }
            }
            foreach (var key in device.Sessions.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                Unregister(device.Sessions[key]);
                device.Sessions.Remove(key);
            }
        }

        private TrackedSession CreateSession(IAudioSessionControl2 control, int state)
        {
            bool system = control.IsSystemSoundsSession() == 0;
            string exe = ConfigStore.SystemSoundsExe, path = null, commandLine = "";
            uint pid = 0;
            if (!system)
            {
                control.GetProcessId(out pid);
                ProcessInfo.Read(pid, out exe, out path, out commandLine);
            }
            return new TrackedSession(this, control)
            {
                Pid = pid,
                Exe = exe,
                Path = path,
                CommandLine = commandLine,
                IsSystem = system,
                IsActive = state == SessionActive,
                FirstSeenTick = Environment.TickCount,
            };
        }

        private static void Unregister(TrackedSession s)
        {
            try { s.Control.UnregisterAudioSessionNotification(s); }
            catch (Exception) { }
        }

        private static void Unregister(DeviceEntry d)
        {
            foreach (var s in d.Sessions.Values) Unregister(s);
            try { d.Manager.UnregisterSessionNotification(d); }
            catch (Exception) { }
        }

        private IEnumerable<TrackedSession> AllSessions => _devices.Values.SelectMany(d => d.Sessions.Values);

        private void ApplyAll()
        {
            foreach (var s in AllSessions)
            {
                try { Apply(s, firstTime: false); }
                catch (Exception) { }
            }
        }

        private bool InScope(TrackedSession s) =>
            _onlyExe == null || string.Equals(s.Exe, _onlyExe, StringComparison.OrdinalIgnoreCase);

        /// <summary>Works out the target level. False means leave the session alone (exempt system sounds).</summary>
        private bool Resolve(TrackedSession s, out bool mute, out float level, out bool fromDefault)
        {
            mute = false;
            level = 1f;
            fromDefault = false;
            var rule = _store.FindRule(s.Exe, s.CommandLine);
            RuleMode mode;
            int percent;
            lock (_store.Sync)
            {
                mode = rule?.Mode ?? RuleMode.UseDefault;
                percent = rule?.Percent ?? 100;
                if (mode == RuleMode.UseDefault)
                {
                    if (s.IsSystem) return false; // B3: exempt unless given its own rule.
                    fromDefault = true;
                    mode = _store.Data.DefaultMode;
                    percent = _store.Data.DefaultPercent;
                }
            }
            mute = mode == RuleMode.Mute;
            level = mode == RuleMode.Custom ? percent / 100f : 1f;
            return true;
        }

        private void Apply(TrackedSession s, bool firstTime)
        {
            if (_paused || !InScope(s)) return;
            ApplyRoute(s);
            if (!Resolve(s, out bool mute, out float level, out bool fromDefault))
            {
                if (s.Managed) RestoreOne(s); // Its rule was removed, so hand it back.
                return;
            }

            var ctx = OurContext;
            s.Volume.GetMute(out bool currentMute);
            s.Volume.GetMasterVolume(out float currentLevel);
            if (mute)
            {
                if (!currentMute) s.Volume.SetMute(true, ref ctx);
            }
            else
            {
                if (Math.Abs(currentLevel - level) > 0.004f) s.Volume.SetMasterVolume(level, ref ctx);
                if (currentMute) s.Volume.SetMute(false, ref ctx);
            }
            s.Managed = true;
            if (!s.IsSystem) _store.MarkManaged(s.Exe);

            if (firstTime && mute && fromDefault && _notified.Add(s.Exe))
                AutoMuted?.Invoke(s.Exe);
        }

        /// <summary>Routes the session's process to its rule's output. A missing device means Windows default.</summary>
        private void ApplyRoute(TrackedSession s)
        {
            if (_paused || s.IsSystem || s.Pid == 0 || !InScope(s) || _routing == null || !_routing.Available) return;
            // Windows stores this per program, so only the exe-wide rule can choose an output.
            string wanted = _store.GetByKey(AppRule.MakeKey(s.Exe, ""))?.OutputId;
            if (string.IsNullOrEmpty(wanted) || !_outputs.Any(o => o.Id == wanted)) wanted = null;

            _routes.TryGetValue(s.Pid, out string current);
            if (wanted == current) return;
            if (!_routing.Set(s.Pid, wanted)) return;
            if (wanted == null) _routes.Remove(s.Pid);
            else _routes[s.Pid] = wanted;
            s.Managed = true;
        }

        private void RestoreManaged(HashSet<string> onlyTheseExes)
        {
            foreach (var s in AllSessions.ToList())
            {
                bool wanted = onlyTheseExes == null ? s.Managed : (!s.IsSystem && onlyTheseExes.Contains(s.Exe));
                if (!wanted) continue;
                try
                {
                    // The uninstaller starts with no route history, so it goes by the saved rule.
                    if (onlyTheseExes != null && !string.IsNullOrEmpty(_store.GetByKey(AppRule.MakeKey(s.Exe, ""))?.OutputId))
                        _routes[s.Pid] = "";
                    RestoreOne(s);
                }
                catch (Exception) { }
            }
        }

        private void RestoreOne(TrackedSession s)
        {
            var ctx = OurContext;
            s.Volume.SetMasterVolume(1f, ref ctx);
            s.Volume.SetMute(false, ref ctx);
            s.Managed = false;
            if (_routes.ContainsKey(s.Pid) && _routing.Set(s.Pid, null)) _routes.Remove(s.Pid);
        }

        /// <summary>P7: a change from the Volume Mixer becomes the app's saved rule.</summary>
        private void OnExternalChange(TrackedSession s, float level, bool muted)
        {
            if (_paused || !InScope(s)) return;
            bool exempt = !Resolve(s, out bool wantMute, out float wantLevel, out _);
            if (exempt) return;

            bool same = wantMute ? muted : !muted && Math.Abs(wantLevel - level) <= 0.004f;
            if (same) { Publish(); return; }

            if (unchecked(Environment.TickCount - s.FirstSeenTick) < StartupGraceMs)
            {
                Apply(s, firstTime: false);
                return;
            }

            RuleMode mode = muted ? RuleMode.Mute : level >= 0.995f ? RuleMode.Full : RuleMode.Custom;
            int percent = (int)Math.Round(level * 100);
            var existing = _store.FindRule(s.Exe, s.CommandLine);
            _store.Upsert(s.Exe, existing?.CommandLineContains ?? "", mode, percent);
            ApplyAll(); // Keeps the app's other sessions (other devices or child processes) in step.
            Publish();
        }

        private void Publish()
        {
            var list = new List<SessionSnapshot>();
            foreach (var s in AllSessions)
            {
                try
                {
                    s.Volume.GetMasterVolume(out float level);
                    s.Volume.GetMute(out bool muted);
                    list.Add(new SessionSnapshot
                    {
                        Exe = s.Exe,
                        Path = s.Path,
                        CommandLine = s.CommandLine,
                        DeviceId = s.DeviceId,
                        IsSystem = s.IsSystem,
                        IsActive = s.IsActive,
                        Muted = muted,
                        Percent = (int)Math.Round(level * 100),
                    });
                }
                catch (Exception) { }
            }
            _snapshot = list;
            Changed?.Invoke();
        }

        private sealed class DeviceEntry : IAudioSessionNotification
        {
            private readonly AudioEngine _engine;
            public readonly IAudioSessionManager2 Manager;
            public readonly Dictionary<string, TrackedSession> Sessions = new Dictionary<string, TrackedSession>();

            public readonly string Id;

            public DeviceEntry(AudioEngine engine, IAudioSessionManager2 manager, string id)
            {
                _engine = engine;
                Manager = manager;
                Id = id;
            }

            public int OnSessionCreated(IntPtr newSession)
            {
                _engine.Post(_engine.ScanAll);
                return 0;
            }
        }

        private sealed class TrackedSession : IAudioSessionEvents
        {
            private readonly AudioEngine _engine;
            public readonly IAudioSessionControl2 Control;
            public readonly ISimpleAudioVolume Volume;
            public string Exe, Path, CommandLine, DeviceId;
            public uint Pid;
            public bool IsSystem, IsActive, Managed;
            public int FirstSeenTick;

            public TrackedSession(AudioEngine engine, IAudioSessionControl2 control)
            {
                _engine = engine;
                Control = control;
                Volume = (ISimpleAudioVolume)control;
            }

            public int OnSimpleVolumeChanged(float volume, bool mute, ref Guid context)
            {
                if (context != OurContext) _engine.Post(() => _engine.OnExternalChange(this, volume, mute));
                return 0;
            }

            public int OnStateChanged(int state)
            {
                _engine.Post(_engine.ScanAll);
                return 0;
            }

            public int OnSessionDisconnected(int reason)
            {
                _engine.Post(_engine.ScanAll);
                return 0;
            }

            public int OnDisplayNameChanged(string name, ref Guid context) => 0;
            public int OnIconPathChanged(string path, ref Guid context) => 0;
            public int OnChannelVolumeChanged(int channels, IntPtr volumes, int changed, ref Guid context) => 0;
            public int OnGroupingParamChanged(ref Guid param, ref Guid context) => 0;
        }

        private sealed class DeviceWatcher : IMMNotificationClient
        {
            private readonly AudioEngine _engine;
            public DeviceWatcher(AudioEngine engine) { _engine = engine; }

            private int Rescan() { _engine.Post(_engine.ScanAll); return 0; }
            public int OnDeviceStateChanged(string id, int newState) => Rescan();
            public int OnDeviceAdded(string id) => Rescan();
            public int OnDeviceRemoved(string id) => Rescan();
            public int OnDefaultDeviceChanged(int flow, int role, string id) => Rescan();
            public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
        }
    }
}
