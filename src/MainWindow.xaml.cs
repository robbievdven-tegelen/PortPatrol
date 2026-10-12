using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace PortPatrol;

public sealed record LagChoice(LagMode Mode, string? Id, string Text);

public sealed record Gateway(string Url, string Region)
{
    public override string ToString() => Url;
    /// <summary>API gateways listed in the official Central Configuration API spec (servers list).</summary>
    public static readonly Gateway[] Known =
    {
        new("https://de1.api.central.arubanetworks.com", "de1 (eu)"),
        new("https://de2.api.central.arubanetworks.com", "de2 (eucentral2)"),
        new("https://de3.api.central.arubanetworks.com", "de3 (eucentral3)"),
        new("https://gb1.api.central.arubanetworks.com", "gb1 (ukwest2)"),
        new("https://us1.api.central.arubanetworks.com", "us1 (prod)"),
        new("https://us2.api.central.arubanetworks.com", "us2 (central-prod2)"),
        new("https://us4.api.central.arubanetworks.com", "us4 (uswest4)"),
        new("https://us5.api.central.arubanetworks.com", "us5 (uswest5)"),
        new("https://us6.api.central.arubanetworks.com", "us6 (us-east-1)"),
        new("https://ca1.api.central.arubanetworks.com", "ca1 (starman)"),
        new("https://in1.api.central.arubanetworks.com", "in1 (apac)"),
        new("https://jp1.api.central.arubanetworks.com", "jp1 (apaceast)"),
        new("https://au1.api.central.arubanetworks.com", "au1 (apacsouth)"),
        new("https://ae1.api.central.arubanetworks.com", "ae1 (uaenorth1)"),
        new("https://cn1.api.central.arubanetworks.com.cn", "cn1 (china-prod)"),
    };
}

public partial class MainWindow : Window
{
    CentralClient? _client;
    List<SwitchItem> _switches = new();
    List<LagInfo> _lags = new();
    List<(int Id, string Name)> _vlans = new();
    public sealed class VlanPick { public int Id { get; set; } public string Text { get; set; } = ""; public bool IsChecked { get; set; } }
    ObservableCollection<VlanPick> _trunkPicks = new();
    SwitchItem? _cur;
    readonly ObservableCollection<PortRow> _ports = new();
    ICollectionView _portsView;
    CancellationTokenSource? _cts;
    bool _busy;
    string? _dryKey;
    int _dryChanges;
    List<PlanItem> _dryPlan = new();
    ChangeSpec? _drySpec;
    readonly string _logFile = Path.Combine(SettingsStore.RunsDir, $"log_{DateTime.Now:yyyyMMdd-HHmmss}.txt");

    public MainWindow()
    {
        InitializeComponent();
        _portsView = CollectionViewSource.GetDefaultView(_ports);
        _portsView.Filter = PortFilter;
        GrdPorts.ItemsSource = _portsView;
        SettingsStore.PurgeLegacy();
        var (s, secret) = SettingsStore.Load();
        CmbBase.ItemsSource = Gateway.Known;
        CmbBase.Text = s.BaseUrl;
        CmbFunction.ItemsSource = CentralClient.Functions;
        TxtClientId.Text = s.ClientId;
        PwdSecret.Password = secret;
        ChkRemember.IsChecked = !string.IsNullOrEmpty(secret);
    }

    // ---------- logging ----------
    void Log(string m)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {m}";
        TxtLog.AppendText(line + Environment.NewLine);
        TxtLog.ScrollToEnd();
        try { File.AppendAllText(_logFile, line + Environment.NewLine); } catch { }
    }

    // ---------- connection ----------
    async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        var url = CmbBase.Text.Trim();
        var id = TxtClientId.Text.Trim();
        var secret = PwdSecret.Password;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) { TxtConnectMsg.Text = "The gateway must be a full https URL."; return; }
        if (id.Length == 0 || secret.Length == 0) { TxtConnectMsg.Text = "Client ID and secret are required."; return; }
        BtnConnect.IsEnabled = false;
        TxtConnectMsg.Text = "Requesting token...";
        try
        {
            var c = new CentralClient(url, id, secret);
            await c.TokenAsync(CancellationToken.None);
            _client?.Dispose();
            _client = c;
            if (ChkRemember.IsChecked == true) SettingsStore.Save(url, id, secret); else SettingsStore.Forget();
            TxtConnectMsg.Text = "Loading switches...";
            await LoadSwitchesAsync();
            TxtConn.Text = u.Host;
            TxtStatus.Text = $"Connected to {u.Host}";
            SettingsView.Visibility = Visibility.Collapsed;
            MainView.Visibility = Visibility.Visible;
            BtnSettings.Visibility = Visibility.Visible;
            BtnLogoff.Visibility = Visibility.Visible;
            TxtConnectMsg.Text = "";
            Log($"Connected to {u.Host}. {_switches.Count} switches loaded from Central.");
        }
        catch (Exception ex) { TxtConnectMsg.Text = "Connection failed: " + ex.Message; }
        finally { BtnConnect.IsEnabled = true; }
    }

    void BtnForget_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Forget();
        PwdSecret.Password = "";
        ChkRemember.IsChecked = false;
        TxtConnectMsg.Text = "Saved credentials removed.";
    }

    void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        MainView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
    }

    void BtnAbout_Click(object sender, RoutedEventArgs e)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var ver = asm.GetName().Version is Version av ? $"{av.Major}.{av.Minor}.{av.Build}" : "?";
        var orange = (System.Windows.Media.Brush)FindResource("Orange");
        TextBlock T(string s, double size, bool bold = false, System.Windows.Media.Brush? fg = null, Thickness? m = null) =>
            new TextBlock { Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = fg ?? System.Windows.Media.Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = m ?? new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
        var muted = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB8, 0xBC, 0xC2));
        var ok = new Button { Content = "Close", Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, IsDefault = true, IsCancel = true };
        var panel = new StackPanel { Margin = new Thickness(28, 22, 28, 22) };
        panel.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/icon.png")), Width = 96, Height = 96 });
        panel.Children.Add(T("PortPatrol", 22, true, orange, new Thickness(0, 8, 0, 0)));
        panel.Children.Add(T("Switch port management for Aruba new Central", 11, false, muted));
        panel.Children.Add(T($"Version {ver}", 13, true, null, new Thickness(0, 14, 0, 0)));
        panel.Children.Add(T("License: MIT (see LICENSE)", 12, false, muted, new Thickness(0, 6, 0, 0)));
        panel.Children.Add(T("Created by Imagination, 2026", 12, true, orange, new Thickness(0, 6, 0, 0)));
        panel.Children.Add(T("Unofficial tool. Not affiliated with or supported by Hewlett Packard Enterprise. Uses an alpha API; use at your own risk.", 10, false, muted, new Thickness(0, 14, 0, 0)));
        panel.Children.Add(ok);
        var w = new Window
        {
            Title = "About PortPatrol", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.Height,
            Width = 380, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = (System.Windows.Media.Brush)FindResource("Dark"), Content = panel
        };
        ok.Click += (_, _) => w.Close();
        w.ShowDialog();
    }

    void BtnLogoff_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _cts?.Cancel();
        _client?.Dispose(); _client = null;
        SettingsStore.ClearFunctions();
        _cur = null; _switches = new();
        _ports.Clear();
        LstSwitches.ItemsSource = null;
        TxtSwFilter.Text = "";
        TxtSwTitle.Text = "Select a switch"; TxtSwInfo.Text = "";
        TxtSel.Text = "No ports selected";
        TxtLog.Clear();
        InvalidateDry();
        PwdSecret.Password = "";
        BtnSettings.Visibility = Visibility.Collapsed;
        BtnLogoff.Visibility = Visibility.Collapsed;
        TxtConn.Text = "";
        TxtStatus.Text = "Not connected";
        MainView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        TxtConnectMsg.Text = ChkRemember.IsChecked == true
            ? "Logged off. Enter the client secret again, or start the app to use the saved credentials. 'Forget saved credentials' removes them from this PC."
            : "Logged off. Nothing is stored on this PC.";
    }

    async Task LoadSwitchesAsync()
    {
        var (defs, raw) = await _client!.ListSwitchesAsync(CancellationToken.None);
        var saved = SettingsStore.LoadFunctions();
        _switches = defs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new SwitchItem(d, saved.TryGetValue(d.StackId ?? d.Serial, out var f) ? f : (d.Function.Length > 0 ? d.Function : null))).ToList();
        _cur = null;
        ApplySwitchFilter();
        if (_switches.Count == 0)
            Log("No switches returned by /network-monitoring/v1/switches. First raw item: " + (raw ?? "(none)"));
        else
            Log("First raw switch item (for diagnostics): " + raw);
        Log($"{_client.FunctionNote}. Known for {_switches.Count(s => !s.FunctionAssumed)} of {_switches.Count} switches.");
    }

    void ApplySwitchFilter()
    {
        var f = TxtSwFilter.Text.Trim();
        LstSwitches.ItemsSource = _switches.Where(s => f.Length == 0 || s.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || s.Def.Serial.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    void TxtSwFilter_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) ApplySwitchFilter(); }

    // ---------- loading ports ----------
    async void LstSwitches_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstSwitches.SelectedItem is SwitchItem s && !_busy && !ReferenceEquals(s, _cur))
        {
            _cur = s;
            await LoadPortsAsync(false);
        }
    }

    async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadPortsAsync(true);

    bool _settingFn;
    void CmbFunction_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingFn || _cur == null || CmbFunction.SelectedItem is not string f || f == _cur.Function) return;
        _cur.Function = f; _cur.FunctionAssumed = false;
        var d = SettingsStore.LoadFunctions(); d[_cur.Def.StackId ?? _cur.Def.Serial] = f; SettingsStore.SaveFunctions(d);
        Log($"Device function for {_cur.Name} set to {f} (saved on this PC).");
        InvalidateDry(); UpdateButtons();
    }

    async Task LoadPortsAsync(bool keepSelection)
    {
        if (_cur == null || _client == null) return;
        _settingFn = true; CmbFunction.SelectedItem = _cur.Function; CmbFunction.IsEnabled = true; _settingFn = false;
        var keep = keepSelection ? GrdPorts.SelectedItems.Cast<PortRow>().Select(p => p.Name).ToHashSet() : new HashSet<string>();
        SetBusy(true);
        InvalidateDry();
        TxtSwTitle.Text = _cur.Name;
        TxtSwInfo.Text = "Loading...";
        _ports.Clear();
        try
        {
            _cts = new CancellationTokenSource();
            _cur.ScopeId ??= await _client.GetScopeIdAsync(_cur.Def, _cts.Token);
            var list = await _client.GetPortsAsync(_cur.ScopeId, _cts.Token);
            foreach (var r in list.Select(o => new PortRow(o)).OrderBy(r => r.SortKey)) _ports.Add(r);
            TxtSwInfo.Text = $"{(_cur.FunctionAssumed ? "device function ASSUMED " + _cur.Function + ", check it  |  " : "")}{_ports.Count} ports  |  {_ports.Count(p => p.AdminDown)} admin down  |  {_ports.Count(p => p.Hardened)} hardened  |  read {DateTime.Now:HH:mm:ss} (Central config, not live link state)";
            if (keep.Count > 0) foreach (var r in _ports.Where(p => keep.Contains(p.Name))) GrdPorts.SelectedItems.Add(r);
            Log($"Loaded {_cur.Name}: {_ports.Count} ports.");
            try { _lags = await _client.ListLagsAsync(_cur.ScopeId, _cts.Token); }
            catch (Exception lgx) { _lags = new(); Log($"LAG list not available for {_cur.Name}: {lgx.Message}"); }
            RebuildLagChoices();
            try
            {
                _vlans = await _client.ListVlansAsync(_cur.ScopeId, _cts.Token);
                TxtVlan.ItemsSource = _vlans.Select(v => v.Name.Length > 0 ? $"{v.Id}  ({v.Name})" : v.Id.ToString()).ToList();
                TxtTrunkNative.ItemsSource = TxtVlan.ItemsSource;
                _trunkPicks = new(_vlans.OrderBy(v => v.Id).Select(v => new VlanPick { Id = v.Id, Text = v.Name.Length > 0 ? $"{v.Id}  ({v.Name})" : v.Id.ToString() }));
                LstTrunkVlans.ItemsSource = _trunkPicks;
                Log(_vlans.Count > 0 ? $"{_vlans.Count} VLANs defined for {_cur.Name} ({_client.VlanNote})." : $"No VLAN list for {_cur.Name}; any VLAN number is accepted. {_client.VlanNote}");
            }
            catch (Exception vx) { _vlans = new(); TxtVlan.ItemsSource = null; TxtTrunkNative.ItemsSource = null; _trunkPicks = new(); LstTrunkVlans.ItemsSource = _trunkPicks; Log($"VLAN list not available for {_cur.Name}: {vx.Message}"); }
            string linkNote;
            try
            {
                var links = await _client.GetLinkStatesAsync(_cur.Def, _cts.Token);
                foreach (var r in _ports) { links.TryGetValue(r.Name, out var li); r.SetLink(li, true); }
                linkNote = $"link status from monitoring at {DateTime.Now:HH:mm:ss}, {_ports.Count(p => p.LinkUp)} up";
            }
            catch (Exception lex)
            {
                linkNote = "link status not available";
                Log($"Link status not available for {_cur.Name}: {lex.Message}");
            }
            TxtSwInfo.Text = $"{(_cur.FunctionAssumed ? "device function ASSUMED " + _cur.Function + ", check it  |  " : "")}{_ports.Count} ports  |  {_ports.Count(p => p.AdminDown)} admin down  |  {_ports.Count(p => p.Hardened)} hardened  |  config read {DateTime.Now:HH:mm:ss}  |  {linkNote}";
            _portsView.Refresh();
        }
        catch (Exception ex)
        {
            TxtSwInfo.Text = "Load failed";
            Log($"ERROR loading {_cur.Name}: {ex.Message}");
        }
        finally { SetBusy(false); }
    }

    bool PortFilter(object o)
    {
        if (o is not PortRow p) return false;
        return CmbPortFilter.SelectedIndex switch
        {
            1 => p.AdminDown,
            2 => p.State.AccessVlan == 666,
            3 => !p.Hardened,
            4 => p.Hardened,
            5 => p.LinkUp,
            6 => p.LinkKnown && !p.LinkUp,
            _ => true
        };
    }

    void CmbPortFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) _portsView.Refresh(); }

    // ---------- selection and spec ----------
    void GrdPorts_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var sel = GrdPorts.SelectedItems.Cast<PortRow>().ToList();
        foreach (var r in _ports) r.Expanded = sel.Count == 1 && ReferenceEquals(r, sel[0]);
        TxtSel.Text = sel.Count == 0 ? "No ports selected" : sel.Count == 1 ? $"Port {sel[0].Name}" : $"{sel.Count} ports selected";
        if (sel.Count == 1) { FillFromPort(sel[0]); TxtSelHint.Text = "Current settings of this port are shown. Change what you need, then Dry run."; }
        else
        {
            BtnClear_Click(this, new RoutedEventArgs());
            TxtSelHint.Text = sel.Count == 0 ? "Click a port to see its settings. Ctrl+click adds ports, Shift+click selects a range." : "Several ports selected: only fields you set are changed on all of them.";
        }
        InvalidateDry();
        UpdateButtons();
    }

    void FillFromPort(PortRow p)
    {
        var s = p.State;
        ChkClearDesc.IsChecked = false;
        _descPrefill = s.Desc;
        TxtDesc.Text = s.Desc ?? "";
        _stpAuto = false;   // the fields are refilled from the port; forget the auto-off state
        foreach (var x in new[] { CmbBpdu, CmbRoot, CmbEdge, CmbLoop }) { x.IsEnabled = true; x.ToolTip = null; }
        CmbAdmin.SelectedIndex = s.Enable ? 1 : 2;
        TxtVlan.Text = string.Equals(s.Mode, "ACCESS", StringComparison.OrdinalIgnoreCase) && s.AccessVlan is int v ? v.ToString() : "";
        CmbBpdu.SelectedIndex = s.BpduGuard ? 1 : 2;
        CmbRoot.SelectedIndex = s.RootGuard ? 1 : 2;
        CmbEdge.SelectedIndex = s.AdminEdge ? 1 : 2;
        CmbLoop.SelectedIndex = s.LoopProtect ? 1 : 2;
        CmbPoe.SelectedIndex = s.Poe is bool pw ? (pw ? 1 : 2) : 0;
        if (CmbLag.Items.Count > 0) CmbLag.SelectedIndex = 0;
    }

    void RebuildLagChoices()
    {
        var items = new List<LagChoice> { new(LagMode.NoChange, null, "(no change)"), new(LagMode.Remove, null, "Remove from LAG") };
        var ids = new SortedDictionary<int, string>();
        foreach (var l in _lags) if (int.TryParse(l.Id, out var n)) ids[n] = l.Description;
        foreach (var p in _ports) if (!string.IsNullOrEmpty(p.State.Lag) && int.TryParse(p.State.Lag, out var n) && !ids.ContainsKey(n)) ids[n] = "";
        foreach (var kv in ids) items.Add(new(LagMode.Join, kv.Key.ToString(), $"Join LAG {kv.Key}" + (kv.Value.Length > 0 ? $"  ({kv.Value})" : "")));
        items.Add(new(LagMode.CreateJoin, null, "Create new LAG..."));
        CmbLag.ItemsSource = items;
        CmbLag.SelectedIndex = 0;
        CmbLag.IsEnabled = true;
    }

    void CmbLag_Changed(object sender, SelectionChangedEventArgs e)
    {
        var c = CmbLag.SelectedItem as LagChoice;
        PnlNewLag.Visibility = c?.Mode == LagMode.CreateJoin ? Visibility.Visible : Visibility.Collapsed;
        if (c?.Mode == LagMode.CreateJoin && string.IsNullOrWhiteSpace(TxtLagNew.Text))
        {
            var used = _lags.Select(l => l.Id).Concat(_ports.Select(p => p.State.Lag ?? "")).Where(x => x.Length > 0).Select(x => int.TryParse(x, out var n) ? n : 0).ToHashSet();
            int next = 1; while (used.Contains(next)) next++;
            TxtLagNew.Text = next.ToString();
        }
        bool joining = c?.Mode == LagMode.Join || c?.Mode == LagMode.CreateJoin;   // joining a LAG cannot be combined with an access VLAN
        if (joining) TxtVlan.Text = "";
        TxtVlan.IsEnabled = !joining;
        TxtVlan.ToolTip = joining ? "Not used when joining or creating a LAG. Set the VLAN in a separate step." : null;
        var stpBoxes = new[] { CmbBpdu, CmbRoot, CmbEdge, CmbLoop };
        if (joining)
        {
            if (!_stpAuto) { _stpBefore = stpBoxes.Select(x => x.SelectedIndex).ToArray(); _stpAuto = true; }
            foreach (var x in stpBoxes) { x.SelectedIndex = 2; x.IsEnabled = false; x.ToolTip = "Switched off automatically: not used on a LAG to another switch."; }
        }
        else if (_stpAuto)
        {
            _stpAuto = false;
            for (int i = 0; i < stpBoxes.Length; i++) { stpBoxes[i].SelectedIndex = _stpBefore[i]; stpBoxes[i].IsEnabled = true; stpBoxes[i].ToolTip = null; }
        }
        bool lagChange = c != null && c.Mode != LagMode.NoChange;   // any LAG change needs the trunk/uplink switch
        if (lagChange)
        {
            if (!_allowAuto) { _allowBefore = ChkAllowTrunk.IsChecked == true; _allowAuto = true; }
            ChkAllowTrunk.IsChecked = true;
            ChkAllowTrunk.IsEnabled = false;
            ChkAllowTrunk.ToolTip = "Switched on automatically because LAG changes need it.";
        }
        else if (_allowAuto)
        {
            _allowAuto = false;
            ChkAllowTrunk.IsChecked = _allowBefore;
            ChkAllowTrunk.IsEnabled = true;
            ChkAllowTrunk.ToolTip = null;
        }
        if (IsLoaded) { InvalidateDry(); UpdateButtons(); }
    }
    string? _descPrefill;
    bool _allowAuto, _allowBefore, _stpAuto;
    int[] _stpBefore = new int[4];

    void BtnSelAll_Click(object sender, RoutedEventArgs e) { GrdPorts.Focus(); GrdPorts.SelectAll(); }
    void BtnSelNone_Click(object sender, RoutedEventArgs e) { GrdPorts.UnselectAll(); }

    void SpecChanged(object sender, RoutedEventArgs e) { if (IsLoaded) { InvalidateDry(); UpdateButtons(); } }

    static bool? Tri(ComboBox c) => c.SelectedIndex == 1 ? true : c.SelectedIndex == 2 ? false : null;

    ChangeSpec? ReadSpec(out string? err)
    {
        err = null;
        var s = new ChangeSpec { Enable = Tri(CmbAdmin), BpduGuard = Tri(CmbBpdu), RootGuard = Tri(CmbRoot), AdminEdge = Tri(CmbEdge), LoopProtect = Tri(CmbLoop), Poe = Tri(CmbPoe) };
        if (CmbLag.SelectedItem is LagChoice lc && lc.Mode != LagMode.NoChange)
        {
            s.LagMode = lc.Mode;
            if (lc.Mode == LagMode.Join) s.LagId = lc.Id;
            if (lc.Mode == LagMode.CreateJoin)
            {
                var ln = TxtLagNew.Text.Trim();
                if (!int.TryParse(ln, out var lnum) || lnum < 1 || lnum > 256) { err = "LAG number must be between 1 and 256."; return null; }
                s.LagId = lnum.ToString();
                s.LagDescription = TxtLagDesc.Text.Trim();
            }
        }
        if (ChkMakeTrunk.IsChecked == true)
        {
            var nm = System.Text.RegularExpressions.Regex.Match(TxtTrunkNative.Text.Trim(), @"^\d+");
            if (!nm.Success || !int.TryParse(nm.Value, out var nat) || nat < 1 || nat > 4094) { err = "Native VLAN: pick a VLAN from the list."; return null; }
            if (_vlans.Count > 0 && !_vlans.Any(x => x.Id == nat)) { err = $"Native VLAN {nat} is not defined on {_cur?.Name}. Pick one from the list."; return null; }
            s.MakeTrunk = true; s.TrunkNative = nat;
            if (ChkTrunkAll.IsChecked == true) s.TrunkAll = true;
            else
            {
                var list = _trunkPicks.Where(x => x.IsChecked).Select(x => x.Id.ToString()).ToList();
                if (list.Count == 0) { err = "Allowed VLANs: tick 'All VLANs' or at least one VLAN."; return null; }
                s.TrunkVlans = list;
            }
            s.BpduGuard = s.RootGuard = s.AdminEdge = s.LoopProtect = null;   // switched off automatically on a trunk
        }
        var t = TxtVlan.Text.Trim();
        var vm = System.Text.RegularExpressions.Regex.Match(t, @"^\d+");
        if (t.Length > 0)
        {
            if (!vm.Success || !int.TryParse(vm.Value, out var v) || v < 1 || v > 4094) { err = "VLAN must be a number between 1 and 4094."; return null; }
            if (_vlans.Count > 0 && !_vlans.Any(x => x.Id == v)) { err = $"VLAN {v} is not defined on {_cur?.Name}. Pick one from the list."; return null; }
            s.AccessVlan = v;
        }
        if (ChkClearDesc.IsChecked == true) s.ClearDescription = true;
        else if (TxtDesc.Text.Trim().Length > 0) s.Description = TxtDesc.Text.Trim();
        else if (!string.IsNullOrEmpty(_descPrefill)) s.ClearDescription = true;   // the field was filled from the port and has been emptied: clear it
        if (s.IsEmpty) { err = "No settings chosen."; return null; }
        return s;
    }

    void ChkMakeTrunk_Changed(object sender, RoutedEventArgs e)
    {
        bool on = ChkMakeTrunk.IsChecked == true;
        PnlTrunk.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) { TxtVlan.Text = ""; }
        TxtVlan.IsEnabled = !on;
        TxtVlan.ToolTip = on ? "Not used when making a trunk." : null;
        foreach (var x in new[] { CmbBpdu, CmbRoot, CmbEdge, CmbLoop })
        {
            x.IsEnabled = !on;
            x.ToolTip = on ? "Switched off automatically on a trunk." : null;
            if (on) x.SelectedIndex = 0;
        }
        SpecChanged(sender, e);
    }

    void ChkTrunkAll_Changed(object sender, RoutedEventArgs e)
    {
        BrdTrunkVlans.IsEnabled = ChkTrunkAll.IsChecked != true;
        SpecChanged(sender, e);
    }

    void ChkClearDesc_Changed(object sender, RoutedEventArgs e)
    {
        TxtDesc.IsEnabled = ChkClearDesc.IsChecked != true;
        if (ChkClearDesc.IsChecked == true) TxtDesc.Text = "";
        SpecChanged(sender, e);
    }

    string? CurrentKey()
    {
        var spec = ReadSpec(out _);
        if (spec == null || _cur == null) return null;
        var ports = string.Join(",", GrdPorts.SelectedItems.Cast<PortRow>().Select(p => p.Name).OrderBy(x => x));
        return $"{_cur.Name}|{ports}|{spec.Key}|{ChkAllowTrunk.IsChecked == true}";
    }

    void InvalidateDry()
    {
        _dryKey = null; _dryChanges = 0; _dryPlan = new();
    }

    void UpdateButtons()
    {
        bool hasSel = GrdPorts.SelectedItems.Count > 0;
        bool specOk = ReadSpec(out _) != null;
        BtnRefresh.IsEnabled = !_busy && _cur != null;
        BtnDry.IsEnabled = !_busy && hasSel && specOk;
        BtnApply.IsEnabled = !_busy && _dryKey != null && _dryKey == CurrentKey() && _dryChanges > 0;
        BtnRollback.IsEnabled = !_busy && _cur != null;
        BtnStop.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        LstSwitches.IsEnabled = !_busy;
    }

    void SetBusy(bool b) { _busy = b; UpdateButtons(); }

    void BtnPreset_Click(object sender, RoutedEventArgs e)
    {
        CmbAdmin.SelectedIndex = 1; TxtVlan.Text = "666";
        CmbBpdu.SelectedIndex = 1; CmbRoot.SelectedIndex = 1; CmbEdge.SelectedIndex = 1; CmbLoop.SelectedIndex = 1;
    }

    void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _descPrefill = null;
        TxtDesc.Text = ""; ChkClearDesc.IsChecked = false;
        ChkMakeTrunk.IsChecked = false; TxtTrunkNative.Text = ""; ChkTrunkAll.IsChecked = false; foreach (var tp in _trunkPicks) tp.IsChecked = false; LstTrunkVlans.Items.Refresh(); CmbAdmin.SelectedIndex = 0; TxtVlan.Text = ""; CmbBpdu.SelectedIndex = 0; CmbRoot.SelectedIndex = 0; CmbEdge.SelectedIndex = 0; CmbLoop.SelectedIndex = 0; CmbPoe.SelectedIndex = 0;
        if (CmbLag.Items.Count > 0) CmbLag.SelectedIndex = 0;
    }

    void BtnStop_Click(object sender, RoutedEventArgs e) { _cts?.Cancel(); Log("Stop requested, finishing the current port..."); }
    void BtnRuns_Click(object sender, RoutedEventArgs e) { try { System.Diagnostics.Process.Start("explorer.exe", SettingsStore.RunsDir); } catch { } }

    // ---------- dry run ----------
    async void BtnDry_Click(object sender, RoutedEventArgs e)
    {
        var spec = ReadSpec(out var err);
        if (spec == null || _cur == null || _client == null) { Log(err ?? "Nothing to do."); return; }
        var rows = GrdPorts.SelectedItems.Cast<PortRow>().OrderBy(r => r.SortKey).ToList();
        bool allow = ChkAllowTrunk.IsChecked == true;
        var key = CurrentKey();
        SetBusy(true);
        _cts = new CancellationTokenSource();
        Log($"--- DRY RUN {_cur.Name}, {rows.Count} ports (nothing is written) ---");
        try
        {
            var plan = await BuildPlanAsync(spec, allow, rows, _cts.Token);
            int changes = plan.Count(p => p.Plan.HasChanges);
            int skipped = plan.Count(p => p.Plan.Skip != null);
            int same = plan.Count - changes - skipped;
            Log($"Dry run result: {changes} would change, {same} already as requested, {skipped} skipped.");
            _dryKey = key; _dryChanges = changes; _dryPlan = plan; _drySpec = spec;
            TxtHint.Text = changes > 0 ? "Dry run done. Apply is unlocked for this exact selection and these settings." : "Nothing to change for this selection.";
        }
        catch (OperationCanceledException) { Log("Dry run stopped."); }
        catch (Exception ex) { Log("ERROR: " + ex.Message); }
        finally { SetBusy(false); }
    }

    async Task<List<PlanItem>> BuildPlanAsync(ChangeSpec spec, bool allow, List<PortRow> rows, CancellationToken ct)
    {
        var list = new List<PlanItem>();
        if (spec.LagMode != LagMode.NoChange)
        {
            try { _lags = await _client!.ListLagsAsync(_cur!.ScopeId!, ct); } catch (OperationCanceledException) { throw; } catch (Exception lx) { Log($"LAG list not refreshed: {lx.Message}"); }
        }
        var known = new HashSet<string>(_lags.Select(l => l.Id).Concat(_ports.Where(p => !string.IsNullOrEmpty(p.State.Lag)).Select(p => p.State.Lag!)));
        var members = _ports.Where(p => !string.IsNullOrEmpty(p.State.Lag)).GroupBy(p => p.State.Lag!).ToDictionary(g => g.Key, g => g.Count());
        foreach (var r in rows)
        {
            ct.ThrowIfCancellationRequested();
            var obj = await _client!.GetPortAsync(_cur!.ScopeId!, r.Name, ct);   // fresh read, not the cached grid
            var st = PortState.From(obj);
            var plan = spec.Plan(st, J.Str(obj["description"]) ?? "", allow, members, known);
            list.Add(new PlanItem(r.Name, st, plan));
            if (spec.Poe == false && plan.HasChanges && r.LinkUp) Log($"{r.Name,-8} NOTE    link is up: PoE off powers down the connected device");
            if (plan.Skip != null) Log($"{r.Name,-8} SKIP    {plan.Skip}");
            else if (plan.Changes.Count == 0) Log($"{r.Name,-8} same    nothing to change");
            else Log($"{r.Name,-8} CHANGE  {string.Join("; ", plan.Changes)}");
            await Task.Delay(150, ct);
        }
        return list;
    }

    // ---------- apply ----------
    async void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (_cur == null || _client == null || _drySpec == null) return;
        if (_dryKey == null || _dryKey != CurrentKey()) { Log("Selection or settings changed since the dry run. Run the dry run again."); UpdateButtons(); return; }
        var spec = _drySpec;
        var todo = _dryPlan.Where(p => p.Plan.HasChanges).ToList();
        bool anyLag = todo.Any(t => t.Plan.LagLeave != null || t.Plan.LagJoin != null || t.Plan.CreateLagName != null);
        if (!ConfirmCount($"{todo.Count} port(s) on {_cur.Name} will be changed in Aruba Central (device function {_cur.Function})." + (anyLag ? " This includes LAG changes, which can interrupt links." : ""), todo.Count)) { Log("Apply cancelled."); return; }
        SetBusy(true);
        _cts = new CancellationTokenSource();
        var snap = Path.Combine(SettingsStore.RunsDir, $"snapshot_{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        Log($"--- APPLY {_cur.Name}, {todo.Count} ports. Snapshot: {snap} ---");
        int ok = 0, skipped = 0, failed = 0;
        try
        {
            foreach (var it in todo)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var fresh = PortState.From(await _client.GetPortAsync(_cur.ScopeId!, it.Port, _cts.Token));
                if (fresh != it.Before) { Log($"{it.Port,-8} SKIPPED, port changed since the dry run"); skipped++; continue; }
                File.AppendAllText(snap, Snapshots.Line(_cur.Name, it.Port, _cur.ScopeId!, _cur.Function, it.Before) + Environment.NewLine);
                var pl = it.Plan;
                var scopeId = _cur.ScopeId!;
                PortState after;
                if (pl.LagLeave != null)
                {
                    Log($"{it.Port,-8} leaving LAG {pl.LagLeave} (via the LAG port list)");
                    Log("         " + await _client.LagLeaveAsync(scopeId, _cur.Function, it.Port, pl.LagLeave, _cts.Token));
                    after = await _client.WaitForLagAsync(scopeId, it.Port, null, _cts.Token);
                    if (!string.IsNullOrEmpty(after.Lag))
                    {
                        Log($"{it.Port,-8} ERROR   still in LAG {after.Lag} after the change. Stopped. Diagnostics:");
                        Log("         LAG object (committed): " + await _client.GetLagRawAsync(scopeId, pl.LagLeave, false, _cts.Token));
                        Log("         LAG object (effective): " + await _client.GetLagRawAsync(scopeId, pl.LagLeave, true, _cts.Token));
                        failed++; break;
                    }
                }
                if (pl.PreBody != null)
                {
                    Log($"{it.Port,-8} switching off BPDU guard / root guard / admin edge / loop protect before joining the LAG");
                    await _client.PatchPortAsync(scopeId, it.Port, _cur.Function, pl.PreBody, _cts.Token);
                    var pre = PortState.From(await _client.GetPortAsync(scopeId, it.Port, _cts.Token));
                    if ((pl.PreBody["stp"]?["bpdu-guard"] != null && pre.BpduGuard) || (pl.PreBody["stp"]?["root-guard"] != null && pre.RootGuard)
                        || (pl.PreBody["stp"]?["admin-edge-port"] != null && pre.AdminEdge) || (pl.PreBody["loop-protect"] != null && pre.LoopProtect))
                    { Log($"{it.Port,-8} ERROR   port settings not switched off. Stopped, the port is not in the LAG yet."); failed++; break; }
                }
                if (pl.CreateLagName != null)
                {
                    Log($"{it.Port,-8} creating LAG {pl.CreateLagName} with this port as first member");
                    await _client.CreateLagAsync(scopeId, _cur.Function, pl.CreateLagName, pl.CreateLagDescription, it.Port, _cts.Token);
                    after = await _client.WaitForLagAsync(scopeId, it.Port, pl.CreateLagName, _cts.Token);
                    if (after.Lag != pl.CreateLagName) { Log($"{it.Port,-8} ERROR   port is not in LAG {pl.CreateLagName} after create. Stopped."); failed++; break; }
                }
                else if (pl.LagJoin != null)
                {
                    Log($"{it.Port,-8} joining LAG {pl.LagJoin} (via the LAG port list)");
                    Log("         " + await _client.LagJoinAsync(scopeId, _cur.Function, it.Port, pl.LagJoin, _cts.Token));
                    after = await _client.WaitForLagAsync(scopeId, it.Port, pl.LagJoin, _cts.Token);
                    if (after.Lag != pl.LagJoin)
                    {
                        Log($"{it.Port,-8} ERROR   port is not in LAG {pl.LagJoin} after the change. Stopped. Diagnostics:");
                        Log("         LAG object (committed): " + await _client.GetLagRawAsync(scopeId, pl.LagJoin, false, _cts.Token));
                        failed++; break;
                    }
                }
                if (pl.Body.Count > 0)
                {
                    await _client.PatchPortAsync(scopeId, it.Port, _cur.Function, pl.Body, _cts.Token);
                    await Task.Delay(400, _cts.Token);
                }
                if (pl.Body2 != null)
                {
                    await Task.Delay(400, _cts.Token);
                    await _client.PatchPortAsync(scopeId, it.Port, _cur.Function, pl.Body2, _cts.Token);
                    await Task.Delay(400, _cts.Token);
                }
                after = PortState.From(await _client.GetPortAsync(scopeId, it.Port, _cts.Token));
                if (spec.Verify(after, out var why)) { Log($"{it.Port,-8} CHANGED, verified"); ok++; }
                else { Log($"{it.Port,-8} ERROR   readback differs ({why}). Stopped."); failed++; break; }
                await Task.Delay(400, _cts.Token);
            }
        }
        catch (OperationCanceledException) { Log("Apply stopped by operator."); }
        catch (Exception ex) { Log("ERROR: " + ex.Message + " Stopped."); failed++; }
        Log($"Apply result: {ok} changed, {skipped} skipped, {failed} failed. Changes reach the switch in about 2 to 2.5 minutes. Rollback file: {snap}");
        SetBusy(false);
        InvalidateDry();
        await LoadPortsAsync(true);
    }

    // ---------- rollback ----------
    async void BtnRollback_Click(object sender, RoutedEventArgs e)
    {
        if (_cur == null || _client == null) return;
        var dlg = new OpenFileDialog { Title = "Choose a snapshot file", Filter = "Snapshot (*.jsonl)|*.jsonl|All files|*.*", InitialDirectory = SettingsStore.RunsDir };
        if (dlg.ShowDialog(this) != true) return;
        var entries = new List<JsonNode>();
        foreach (var line in File.ReadAllLines(dlg.FileName))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { var n = JsonNode.Parse(line); if (n != null && J.Str(n["switch"]) == _cur.Name) entries.Add(n); } catch { }
        }
        if (entries.Count == 0) { Log($"No entries for {_cur.Name} in {Path.GetFileName(dlg.FileName)}."); return; }
        if (!ConfirmCount($"{entries.Count} port(s) on {_cur.Name} will be put back to the values in the snapshot.", entries.Count)) { Log("Rollback cancelled."); return; }
        SetBusy(true);
        _cts = new CancellationTokenSource();
        Log($"--- ROLLBACK {_cur.Name}, {entries.Count} ports from {Path.GetFileName(dlg.FileName)} ---");
        int ok = 0, failed = 0;
        try
        {
            foreach (var n in entries)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var port = J.Str(n["port"])!; var scope = J.Str(n["scopeId"])!; var fn = J.Str(n["function"])!;
                var before = n["before"]!;
                var beforeLag = J.Str(before["lag"]);
                var cur = PortState.From(await _client.GetPortAsync(scope, port, _cts.Token));
                if ((beforeLag ?? "") != (cur.Lag ?? ""))
                {
                    if (!string.IsNullOrEmpty(cur.Lag)) { await _client.LagLeaveAsync(scope, fn, port, cur.Lag!, _cts.Token); await _client.WaitForLagAsync(scope, port, null, _cts.Token); }
                    if (beforeLag != null) { await _client.LagJoinAsync(scope, fn, port, beforeLag, _cts.Token); await _client.WaitForLagAsync(scope, port, beforeLag, _cts.Token); }
                }
                if (beforeLag == null)
                {
                    var rb = Snapshots.RollbackBody(before);
                    if (before["description"] != null)
                    {
                        var bd = J.Str(before["description"]) ?? "";
                        if (bd.Length > 0 && bd != (cur.Desc ?? "")) rb["description"] = bd;
                        else if (bd.Length == 0 && cur.Desc != null) rb["description"] = null;
                    }
                    await _client.PatchPortAsync(scope, port, fn, rb, _cts.Token);
                }
                await Task.Delay(400, _cts.Token);
                var now = PortState.From(await _client.GetPortAsync(scope, port, _cts.Token));
                bool good = (now.Lag ?? "") == (beforeLag ?? "") && (beforeLag != null || true) && now.Enable == J.Bool(before["enable"], true) && now.BpduGuard == J.Bool(before["bpduGuard"]) && now.LoopProtect == J.Bool(before["loopProtect"])
                            && (J.Str(before["mode"]) != "ACCESS" || now.AccessVlan == J.Int(before["accessVlan"]))
                            && (J.Str(before["mode"]) != "TRUNK" || string.Equals(now.Mode, "TRUNK", StringComparison.OrdinalIgnoreCase));
                if (beforeLag != null) good = (now.Lag ?? "") == beforeLag;
                if (good) { Log($"{port,-8} rolled back, verified"); ok++; }
                else { Log($"{port,-8} ERROR   readback differs from snapshot. Stopped."); failed++; break; }
                await Task.Delay(400, _cts.Token);
            }
        }
        catch (OperationCanceledException) { Log("Rollback stopped by operator."); }
        catch (Exception ex) { Log("ERROR: " + ex.Message + " Stopped."); failed++; }
        Log($"Rollback result: {ok} restored, {failed} failed.");
        SetBusy(false);
        InvalidateDry();
        await LoadPortsAsync(true);
    }

    // ---------- confirm dialog ----------
    bool ConfirmCount(string text, int n)
    {
        var w = new Window { Title = "Confirm", Owner = this, Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = (System.Windows.Media.Brush)FindResource("Bg") };
        var tb = new TextBox { Margin = new Thickness(0, 8, 0, 14) };
        var ok = new Button { Content = "Confirm", IsEnabled = false, IsDefault = true, Style = (Style)FindResource("Primary") };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        tb.TextChanged += (_, _) => ok.IsEnabled = tb.Text.Trim() == n.ToString();
        ok.Click += (_, _) => w.DialogResult = true;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        panel.Children.Add(new TextBlock { Text = $"Type {n} to confirm.", Foreground = (System.Windows.Media.Brush)FindResource("Muted"), Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(tb);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btns.Children.Add(ok); btns.Children.Add(cancel);
        panel.Children.Add(btns);
        w.Content = panel;
        w.Loaded += (_, _) => tb.Focus();
        return w.ShowDialog() == true;
    }
}
