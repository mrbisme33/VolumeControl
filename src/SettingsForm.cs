using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VolumeControl
{
    internal sealed class SettingsForm : Form
    {
        private sealed class Row
        {
            public string Exe, Match = "", Path, CommandLine = "";
            public AppRule Rule;
            public bool Playing, Live, Muted;
            public int Percent;
            public string Key => AppRule.MakeKey(Exe, Match);
        }

        private sealed class OutputChoice
        {
            public string Id, Name;
            public override string ToString() => Name;
        }

        private readonly ConfigStore _store;
        private readonly AudioEngine _engine;
        private readonly Action _engineChanged;

        private readonly CheckBox _startup = new CheckBox { Text = "Start with Windows", AutoSize = true };
        private readonly CheckBox _notify = new CheckBox { Text = "Notify when an app is muted by default", AutoSize = true };
        private readonly ComboBox _defaultMode = NewCombo("Mute", "Full", "Custom");
        private readonly TrackBar _defaultSlider = NewSlider();
        private readonly Label _defaultValue = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly ComboBox _output = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };

        private readonly ListView _list = new ListView
        {
            View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
            Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        private readonly ImageList _icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(20, 20) };

        private readonly ComboBox _ruleMode = NewCombo("Use default", "Mute", "Full", "Custom");
        private readonly TrackBar _ruleSlider = NewSlider();
        private readonly Label _ruleValue = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly Button _remove = new Button { Text = "Remove rule", AutoSize = true };
        private readonly TextBox _matchText = new TextBox { Dock = DockStyle.Fill };
        private readonly Button _addMatch = new Button { Text = "Add separate rule", AutoSize = true };
        private readonly TextBox _commandLine = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        private readonly ComboBox _ruleOutput = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly TableLayoutPanel _editor;

        private bool _loading;
        private List<Row> _rows = new List<Row>();

        private static ComboBox NewCombo(params object[] items)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Anchor = AnchorStyles.Left };
            c.Items.AddRange(items);
            return c;
        }

        private static TrackBar NewSlider() => new TrackBar
        {
            Minimum = 0, Maximum = 100, TickFrequency = 10, LargeChange = 10, SmallChange = 1,
            AutoSize = false, Height = 32, Dock = DockStyle.Fill,
        };

        public SettingsForm(ConfigStore store, AudioEngine engine)
        {
            _store = store;
            _engine = engine;
            Text = "VolumeControl";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(700, 620);
            MinimumSize = new Size(620, 520);
            StartPosition = FormStartPosition.CenterScreen;

            _list.SmallImageList = _icons;
            _list.Columns.Add("App", 180);
            _list.Columns.Add("Status", 120);
            _list.Columns.Add("Rule", 70);
            _list.Columns.Add("Output", 170);
            _list.Columns.Add("Command line contains", 130);

            var general = Grid(3);
            general.ColumnStyles[1] = new ColumnStyle(SizeType.Percent, 100);
            general.ColumnStyles[2] = new ColumnStyle(SizeType.Absolute, 54);
            general.Controls.Add(_defaultMode, 0, 0);
            general.Controls.Add(_defaultSlider, 1, 0);
            general.Controls.Add(_defaultValue, 2, 0);
            general.Controls.Add(_notify, 0, 1);
            general.SetColumnSpan(_notify, 3);
            general.Controls.Add(_startup, 0, 2);
            general.SetColumnSpan(_startup, 3);
            general.Controls.Add(Caption("Output device"), 0, 3);
            general.Controls.Add(_output, 1, 3);
            general.SetColumnSpan(_output, 2);
            var generalBox = Group("Default for new apps", general);

            _editor = Grid(4);
            _editor.ColumnStyles[1] = new ColumnStyle(SizeType.Percent, 100);
            _editor.ColumnStyles[2] = new ColumnStyle(SizeType.Absolute, 54);
            _editor.ColumnStyles[3] = new ColumnStyle(SizeType.AutoSize);
            _editor.Controls.Add(_ruleMode, 0, 0);
            _editor.Controls.Add(_ruleSlider, 1, 0);
            _editor.Controls.Add(_ruleValue, 2, 0);
            _editor.Controls.Add(_remove, 3, 0);
            _editor.Controls.Add(Caption("Match text"), 0, 1);
            _editor.Controls.Add(_matchText, 1, 1);
            _editor.SetColumnSpan(_matchText, 2);
            _editor.Controls.Add(_addMatch, 3, 1);
            _editor.Controls.Add(Caption("Command line"), 0, 2);
            _editor.Controls.Add(_commandLine, 1, 2);
            _editor.SetColumnSpan(_commandLine, 3);
            _editor.Controls.Add(Caption("Output"), 0, 3);
            _editor.Controls.Add(_ruleOutput, 1, 3);
            _editor.SetColumnSpan(_ruleOutput, 3);
            var editorBox = Group("Selected app", _editor);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(generalBox, 0, 0);
            root.Controls.Add(_list, 0, 1);
            root.Controls.Add(editorBox, 0, 2);
            Controls.Add(root);

            LoadGeneral();

            _startup.CheckedChanged += (s, e) => EditGeneral(d => d.StartWithWindows = _startup.Checked);
            _notify.CheckedChanged += (s, e) => EditGeneral(d => d.NotifyOnAutoMute = _notify.Checked);
            _defaultMode.SelectedIndexChanged += (s, e) => EditGeneral(d => d.DefaultMode = (RuleMode)(_defaultMode.SelectedIndex + 1));
            _defaultSlider.ValueChanged += (s, e) => EditGeneral(d => d.DefaultPercent = _defaultSlider.Value);
            _output.SelectedIndexChanged += (s, e) =>
            {
                if (!_loading && _output.SelectedItem is OutputChoice choice) _engine.SetOutput(choice.Id);
            };
            _list.SelectedIndexChanged += (s, e) => LoadEditor();
            _ruleMode.SelectedIndexChanged += (s, e) => EditRule();
            _ruleSlider.ValueChanged += (s, e) => EditRule();
            _ruleOutput.SelectedIndexChanged += (s, e) =>
            {
                var row = SelectedRow;
                if (_loading || row == null || !(_ruleOutput.SelectedItem is OutputChoice choice)) return;
                row.Rule = _store.SetOutput(row.Exe, row.Match, choice.Id);
                _engine.Reapply();
            };
            _remove.Click += (s, e) => RemoveRule();
            _addMatch.Click += (s, e) => AddMatchRule();

            _engineChanged = () => { if (IsHandleCreated) BeginInvoke((Action)RefreshRows); };
            _engine.Changed += _engineChanged;
            Load += (s, e) => RefreshRows();
            FormClosed += (s, e) => _engine.Changed -= _engineChanged;
        }

        private static TableLayoutPanel Grid(int columns)
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = columns, RowCount = 3 };
            for (int i = 0; i < columns; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return grid;
        }

        private static GroupBox Group(string title, Control content)
        {
            var box = new GroupBox { Text = title, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
            box.Controls.Add(content);
            return box;
        }

        private static Label Caption(string text) => new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };

        private void LoadGeneral()
        {
            _loading = true;
            lock (_store.Sync)
            {
                var d = _store.Data;
                _startup.Checked = d.StartWithWindows;
                _startup.Enabled = Installer.IsInstalledCopy;
                _notify.Checked = d.NotifyOnAutoMute;
                _defaultMode.SelectedIndex = (int)d.DefaultMode - 1;
                _defaultSlider.Value = d.DefaultPercent;
            }
            _loading = false;
            UpdateDefaultSlider();
        }

        private void UpdateDefaultSlider()
        {
            bool custom = _defaultMode.SelectedIndex == 2;
            _defaultSlider.Enabled = custom;
            _defaultValue.Text = custom ? _defaultSlider.Value + "%" : "";
        }

        private void EditGeneral(Action<Settings> edit)
        {
            UpdateDefaultSlider();
            if (_loading) return;
            lock (_store.Sync) edit(_store.Data);
            _store.Save();
            Installer.SyncStartup(_startup.Checked);
            _engine.Reapply();
        }

        private List<Row> BuildRows()
        {
            var rows = new Dictionary<string, Row>();
            lock (_store.Sync)
            {
                foreach (var r in _store.Data.Rules)
                    rows[r.Key] = new Row { Exe = r.Exe, Match = r.CommandLineContains, Rule = r };
            }

            string systemKey = AppRule.MakeKey(ConfigStore.SystemSoundsExe, "");
            if (!rows.ContainsKey(systemKey)) rows[systemKey] = new Row { Exe = ConfigStore.SystemSoundsExe };

            foreach (var s in _engine.Sessions)
            {
                var rule = _store.FindRule(s.Exe, s.CommandLine);
                string key = rule?.Key ?? AppRule.MakeKey(s.Exe, "");
                if (!rows.TryGetValue(key, out var row))
                    rows[key] = row = new Row { Exe = s.Exe };
                row.Path = s.Path ?? row.Path;
                if (!string.IsNullOrEmpty(s.CommandLine)) row.CommandLine = s.CommandLine;
                // Prefer the state of a session that is actually making sound.
                if (!row.Live || s.IsActive)
                {
                    row.Muted = s.Muted;
                    row.Percent = s.Percent;
                }
                row.Live = true;
                row.Playing |= s.IsActive;
            }
            return rows.Values.OrderByDescending(r => r.Playing).ThenByDescending(r => r.Live)
                .ThenBy(r => r.Exe, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void RefreshRows()
        {
            if (IsDisposed) return;
            string selected = SelectedRow?.Key;
            _rows = BuildRows();

            _loading = true;
            if (!_output.DroppedDown)
            {
                _output.Items.Clear();
                foreach (var o in _engine.Outputs)
                {
                    int index = _output.Items.Add(new OutputChoice { Id = o.Id, Name = o.Name });
                    if (o.IsDefault) _output.SelectedIndex = index;
                }
            }
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var row in _rows)
            {
                var item = new ListViewItem(DisplayName(row.Exe)) { ImageKey = IconFor(row), Tag = row };
                item.SubItems.Add(StatusText(row));
                item.SubItems.Add(RuleText(row.Rule));
                item.SubItems.Add(OutputText(row.Rule));
                item.SubItems.Add(row.Match);
                _list.Items.Add(item);
                if (row.Key == selected) item.Selected = true;
            }
            _list.EndUpdate();
            _loading = false;
            LoadEditor();
        }

        private static string DisplayName(string exe) => exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exe.Substring(0, exe.Length - 4) : exe;

        private string StatusText(Row row)
        {
            if (!row.Live) return row.Rule != null ? "Saved" : "Exempt";
            string level = row.Muted ? "muted" : row.Percent + "%";
            return (row.Playing ? "Playing, " : "Idle, ") + level + (_engine.Paused ? " (paused)" : "");
        }

        private string RuleText(AppRule rule)
        {
            if (rule == null || rule.Mode == RuleMode.UseDefault) return "Default";
            return rule.Mode == RuleMode.Custom ? rule.Percent + "%" : rule.Mode.ToString();
        }

        private string OutputText(AppRule rule)
        {
            if (!string.IsNullOrEmpty(rule?.CommandLineContains)) return "Same as app";
            if (string.IsNullOrEmpty(rule?.OutputId)) return "Windows default";
            return _engine.Outputs.FirstOrDefault(o => o.Id == rule.OutputId)?.Name ?? "Unavailable device";
        }

        private void LoadRuleOutputs(Row row)
        {
            if (_ruleOutput.DroppedDown) return;
            string current = row.Rule?.OutputId ?? "";
            _ruleOutput.Items.Clear();
            _ruleOutput.Items.Add(new OutputChoice { Id = "", Name = "Windows default" });
            foreach (var o in _engine.Outputs) _ruleOutput.Items.Add(new OutputChoice { Id = o.Id, Name = o.Name });
            if (current.Length > 0 && _engine.Outputs.All(o => o.Id != current))
                _ruleOutput.Items.Add(new OutputChoice { Id = current, Name = "Unavailable device" });
            _ruleOutput.SelectedItem = _ruleOutput.Items.Cast<OutputChoice>().First(c => c.Id == current);
            // Windows routes by program, so a command-line rule cannot have its own output.
            _ruleOutput.Enabled = _engine.PerAppOutputAvailable && row.Exe != ConfigStore.SystemSoundsExe && row.Match.Length == 0;
        }

        private string IconFor(Row row)
        {
            string key = row.Exe.ToLowerInvariant();
            if (_icons.Images.ContainsKey(key)) return key;
            if (row.Path == null || !File.Exists(row.Path)) return null;
            try
            {
                using (var icon = Icon.ExtractAssociatedIcon(row.Path))
                    _icons.Images.Add(key, icon.ToBitmap());
                return key;
            }
            catch (Exception) { return null; }
        }

        private Row SelectedRow => _list.SelectedItems.Count == 0 ? null : (Row)_list.SelectedItems[0].Tag;

        private void LoadEditor()
        {
            if (_loading) return;
            var row = SelectedRow;
            _editor.Enabled = row != null;
            _loading = true;
            if (row == null)
            {
                _ruleMode.SelectedIndex = 0;
                _commandLine.Text = "";
            }
            else
            {
                _ruleMode.SelectedIndex = (int)(row.Rule?.Mode ?? RuleMode.UseDefault);
                _ruleSlider.Value = row.Rule?.Percent ?? 100;
                _remove.Enabled = row.Rule != null;
                bool isSystem = row.Exe == ConfigStore.SystemSoundsExe;
                _addMatch.Enabled = !isSystem;
                _matchText.Enabled = !isSystem;
                if (_commandLine.Text != row.CommandLine) _commandLine.Text = row.CommandLine;
                LoadRuleOutputs(row);
            }
            _ruleSlider.Enabled = _ruleMode.SelectedIndex == (int)RuleMode.Custom;
            _ruleValue.Text = _ruleSlider.Enabled ? _ruleSlider.Value + "%" : "";
            _loading = false;
        }

        private void EditRule()
        {
            if (_loading || SelectedRow == null) return;
            var row = SelectedRow;
            row.Rule = _store.Upsert(row.Exe, row.Match, (RuleMode)_ruleMode.SelectedIndex, _ruleSlider.Value);
            _ruleSlider.Enabled = _ruleMode.SelectedIndex == (int)RuleMode.Custom;
            _ruleValue.Text = _ruleSlider.Enabled ? _ruleSlider.Value + "%" : "";
            _engine.Reapply();
        }

        private void RemoveRule()
        {
            var row = SelectedRow;
            if (row?.Rule == null) return;
            _store.Remove(row.Rule.Key);
            _engine.Reapply();
        }

        private void AddMatchRule()
        {
            var row = SelectedRow;
            string match = _matchText.Text.Trim();
            if (row == null || match.Length == 0) return;
            _store.Upsert(row.Exe, match, (RuleMode)_ruleMode.SelectedIndex, _ruleSlider.Value);
            _matchText.Clear();
            _engine.Reapply();
        }
    }
}
