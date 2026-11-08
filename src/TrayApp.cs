using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace VolumeControl
{
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly ConfigStore _store;
        private readonly AudioEngine _engine;
        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _pauseItem;
        private readonly SynchronizationContext _ui;
        private readonly EventWaitHandle _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        private readonly EventWaitHandle _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
        private readonly Control _marshal = new Control();
        private SettingsForm _settings;
        private bool _exiting;

        public TrayApp(ConfigStore store, string onlyExe)
        {
            _store = store;
            _marshal.CreateControl(); // Installs the WinForms sync context on this thread.
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

            _engine = new AudioEngine(store, false, onlyExe);
            _engine.AutoMuted += exe => _ui.Post(_ => ShowAutoMuted(exe), null);

            _pauseItem = new ToolStripMenuItem("Pause", null, (s, e) => TogglePause());
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open settings", null, (s, e) => ShowSettings());
            var outputItem = new ToolStripMenuItem("Output");
            outputItem.DropDownItems.Add("(none)"); // Windows only shows the arrow if a child exists.
            outputItem.DropDownOpening += (s, e) => FillOutputs(outputItem);
            menu.Items.Add(outputItem);
            menu.Items.Add(_pauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitApp());

            _tray = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
                Text = "VolumeControl",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };

            var listener = new Thread(ListenForSignals) { IsBackground = true, Name = "Signals" };
            listener.Start();

            _engine.Start();
        }

        private void ListenForSignals()
        {
            var handles = new WaitHandle[] { _showEvent, _exitEvent };
            while (true)
            {
                int which = WaitHandle.WaitAny(handles);
                if (which == 0) _ui.Post(_ => ShowSettings(), null);
                else { _ui.Post(_ => ExitApp(), null); return; }
            }
        }

        private void FillOutputs(ToolStripMenuItem parent)
        {
            parent.DropDownItems.Clear();
            foreach (var output in _engine.Outputs)
            {
                string id = output.Id;
                parent.DropDownItems.Add(new ToolStripMenuItem(output.Name, null, (s, e) => _engine.SetOutput(id))
                {
                    Checked = output.IsDefault,
                });
            }
            if (parent.DropDownItems.Count == 0)
                parent.DropDownItems.Add(new ToolStripMenuItem("No output devices") { Enabled = false });
        }

        private void TogglePause()
        {
            bool pause = !_engine.Paused;
            _engine.SetPaused(pause);
            _pauseItem.Text = pause ? "Resume" : "Pause";
            _tray.Text = pause ? "VolumeControl (paused)" : "VolumeControl";
        }

        private void ShowAutoMuted(string exe)
        {
            bool notify;
            lock (_store.Sync) notify = _store.Data.NotifyOnAutoMute;
            if (!notify) return;
            _tray.BalloonTipTitle = "App muted by default";
            _tray.BalloonTipText = exe + " was muted. Open VolumeControl to allow it.";
            _tray.ShowBalloonTip(3000);
        }

        private void ShowSettings()
        {
            if (_exiting) return;
            if (_settings == null || _settings.IsDisposed)
            {
                _settings = new SettingsForm(_store, _engine);
                _settings.Show();
            }
            if (_settings.WindowState == FormWindowState.Minimized) _settings.WindowState = FormWindowState.Normal;
            _settings.Activate();
        }

        private void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            _settings?.Close();
            _engine.Stop(); // B4: apps go back to normal before the tray icon disappears.
            _tray.Visible = false;
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tray.Dispose();
                _marshal.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
