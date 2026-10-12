using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PortPatrol;

public sealed class PortRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public JsonObject Raw { get; }
    public PortState State { get; }
    public string Name { get; }
    public string Description { get; }
    public (int, int, int) SortKey { get; }
    /// <summary>Numeric key for the Port column, so 1/1/2 sorts before 1/1/10.</summary>
    public long SortNumber => (long)SortKey.Item1 * 1_000_000 + (long)SortKey.Item2 * 1_000 + SortKey.Item3;

    public PortRow(JsonObject o)
    {
        Raw = o;
        State = PortState.From(o);
        Name = J.Str(o["name"]) ?? "?";
        Description = (J.Str(o["description"]) ?? "").Trim();
        var p = Name.Split('/');
        int G(int i) => i < p.Length && int.TryParse(p[i], out var n) ? n : 0;
        SortKey = (G(0), G(1), G(2));
    }

    public string AdminText => State.Enable ? "Up" : "Down";
    public bool AdminDown => !State.Enable;
    public string ModeText => !string.IsNullOrEmpty(State.Lag) ? $"LAG {State.Lag}" : State.Mode ?? "";
    public string VlanText => string.Equals(State.Mode, "ACCESS", StringComparison.OrdinalIgnoreCase)
        ? State.AccessVlan?.ToString() ?? ""
        : TrunkText;
    /// <summary>Trunk ports: native VLAN and the allowed VLANs ("all" or the list).</summary>
    public string TrunkText
    {
        get
        {
            if (State.Switchport == null) return State.NativeVlan is int n ? $"native {n}" : "";
            try
            {
                var sp = JsonNode.Parse(State.Switchport) as JsonObject;
                var native = J.Int(sp?["native-vlan"]);
                string allowed = J.Bool(sp?["trunk-vlan-all"]) ? "all" : sp?["trunk-vlan-ranges"] is JsonArray a && a.Count > 0 ? string.Join(",", a.Select(x => J.Str(x))) : "none";
                return $"native {native?.ToString() ?? "?"}, allowed {allowed}";
            }
            catch { return State.NativeVlan is int n2 ? $"native {n2}" : ""; }
        }
    }
    public string BpduText => State.BpduGuard ? "on" : "";
    public string RootText => State.RootGuard ? "on" : "";
    public string EdgeText => State.AdminEdge ? "on" : "";
    public string LoopText => State.LoopProtect ? (State.LoopAction ?? "on") : "";
    LinkInfo? _link; bool _linkKnown;
    public void SetLink(LinkInfo? l, bool known)
    {
        _link = l; _linkKnown = known;
        foreach (var n in new[] { nameof(LinkText), nameof(InUse), nameof(NeighbourText) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
    public bool LinkKnown => _linkKnown;
    public bool LinkUp => _link != null && string.Equals(_link.Oper, "Up", StringComparison.OrdinalIgnoreCase);
    public bool InUse => LinkUp;
    public string LinkText => !_linkKnown ? "" : _link == null ? "?" : LinkUp ? "\u25CF Up" : "\u25CB Down";
    public string NeighbourText => _link == null || string.IsNullOrWhiteSpace(_link.Neighbour) ? "" : string.IsNullOrWhiteSpace(_link.NeighbourPort) ? _link.Neighbour : $"{_link.Neighbour} ({_link.NeighbourPort})";
    public string PoeText => J.Bool(Raw["poe"]?["enabled"]) ? "PoE" : "";
    public bool Hardened => State.BpduGuard && State.RootGuard && State.AdminEdge && State.LoopProtect;
    public string HardenedText => Hardened ? "yes" : "";

    public string DetailJson
    {
        get
        {
            var d = new JsonObject();
            foreach (var k in new[] { "description", "enable", "portchannel-lag", "switchport", "stp", "loop-protect", "poe", "lldp", "speed-duplex", "mtu" })
                if (Raw[k] != null) d[k] = Raw[k]!.DeepClone();
            return d.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
    }

    bool _expanded;
    public bool Expanded
    {
        get => _expanded;
        set { if (_expanded != value) { _expanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Expanded))); } }
    }
}
