using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PortPatrol;

public static class J
{
    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static int? Int(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return (int)l;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        }
        return null;
    }
    public static bool Bool(JsonNode? n, bool def = false) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;
}

public sealed class CentralException : Exception
{
    public int Status { get; }
    public CentralException(int status, string message) : base(message) { Status = status; }
}

public sealed record SwitchDef(string Name, string Serial, string Function, string? StackId);

public sealed class SwitchItem
{
    public SwitchDef Def { get; }
    public string? ScopeId { get; set; }
    public string Function { get; set; }
    public bool FunctionAssumed { get; set; }
    public string Name => Def.Name;
    public string Sub => $"{Def.Serial}" + (Def.StackId != null ? "  |  stack" : "");
    public SwitchItem(SwitchDef d, string? function)
    {
        Def = d;
        FunctionAssumed = function == null;
        Function = function ?? "ACCESS_SWITCH";
    }
}

public sealed record PortState(bool Enable, string? Mode, int? AccessVlan, int? NativeVlan,
    bool BpduGuard, bool RootGuard, bool AdminEdge, bool LoopProtect, string? LoopAction, string? Lag = null, bool? Poe = null, string? Desc = null, string? Switchport = null)
{
    public static PortState From(JsonObject o) => new(
        J.Bool(o["enable"], true),
        J.Str(o["switchport"]?["interface-mode"]),
        J.Int(o["switchport"]?["access-vlan"]),
        J.Int(o["switchport"]?["native-vlan"]),
        J.Bool(o["stp"]?["bpdu-guard"]),
        J.Bool(o["stp"]?["root-guard"]),
        J.Bool(o["stp"]?["admin-edge-port"]),
        J.Bool(o["loop-protect"]?["enable"]),
        J.Str(o["loop-protect"]?["action"]),
        J.Str(o["portchannel-lag"]),
        o["poe"] is JsonObject po ? J.Bool(po["enabled"]) : null,
        string.IsNullOrEmpty(J.Str(o["description"])) ? null : J.Str(o["description"]),
        string.Equals(J.Str(o["switchport"]?["interface-mode"]), "TRUNK", StringComparison.OrdinalIgnoreCase) ? o["switchport"]?.ToJsonString() : null);
}

public sealed class PortPlan
{
    public List<string> Changes { get; } = new();
    public JsonObject Body { get; set; } = new();
    /// <summary>Second PATCH, sent after Body was applied and verified (used when a port leaves a LAG and gets new settings).</summary>
    public JsonObject? Body2 { get; set; }
    /// <summary>Port-level end-device settings (BPDU guard, root guard, admin edge, loop protect) switched off before the port becomes a LAG member.</summary>
    public JsonObject? PreBody { get; set; }
    public string? CreateLagName { get; set; }
    public string? CreateLagDescription { get; set; }
    public string? LagLeave { get; set; }
    public string? LagJoin { get; set; }
    public string? LagFrom { get; set; }
    public string? Skip { get; set; }
    public bool HasChanges => Skip == null && Changes.Count > 0;
}

public sealed record PlanItem(string Port, PortState Before, PortPlan Plan);

/// <summary>What the operator wants to change. A null value means: leave as is.</summary>
public enum LagMode { NoChange, Remove, Join, CreateJoin }

public sealed class ChangeSpec
{
    public LagMode LagMode { get; set; }
    public string? LagId { get; set; }
    public string? LagDescription { get; set; }
    public bool? Enable { get; set; }
    public int? AccessVlan { get; set; }
    public bool? BpduGuard { get; set; }
    public bool? RootGuard { get; set; }
    public bool? AdminEdge { get; set; }
    public bool? LoopProtect { get; set; }
    public bool? Poe { get; set; }
    /// <summary>New port description (1 to 64 characters). Null = leave as is.</summary>
    public string? Description { get; set; }
    public bool ClearDescription { get; set; }
    /// <summary>Make the port a TRUNK with this native VLAN and these allowed VLANs (numbers and ranges such as "10-20").</summary>
    public bool MakeTrunk { get; set; }
    public int? TrunkNative { get; set; }
    public List<string> TrunkVlans { get; set; } = new();

    /// <summary>Expands "10", "20-22" into a set of VLAN ids. Returns null when an entry is invalid.</summary>
    public static HashSet<int>? ExpandVlans(IEnumerable<string> items)
    {
        var set = new HashSet<int>();
        foreach (var raw in items)
        {
            var t = raw.Trim();
            var m = Regex.Match(t, @"^(\d{1,4})(?:-(\d{1,4}))?$");
            if (!m.Success) return null;
            int lo = int.Parse(m.Groups[1].Value), hi = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : lo;
            if (lo < 1 || hi > 4094 || lo > hi) return null;
            for (int i = lo; i <= hi; i++) set.Add(i);
        }
        return set;
    }

    public bool IsEmpty => !MakeTrunk && LagMode == LagMode.NoChange && Description == null && !ClearDescription && Poe == null && Enable == null && AccessVlan == null && BpduGuard == null && RootGuard == null && AdminEdge == null && LoopProtect == null;
    public string Key => $"{MakeTrunk}|{TrunkNative}|{string.Join(",", TrunkVlans)}|{LagMode}|{LagId}|{LagDescription}|{Description}|{ClearDescription}|{Poe}|{Enable}|{AccessVlan}|{BpduGuard}|{RootGuard}|{AdminEdge}|{LoopProtect}";

    static readonly Regex UplinkWords = new(@"\b(UL|uplink|LAG|backbone)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public PortPlan Plan(PortState b, string description, bool allowTrunkAndUplink,
                         IReadOnlyDictionary<string, int>? lagMembers = null, ISet<string>? knownLags = null)
    {
        var p = new PortPlan();
        bool trunk = string.Equals(b.Mode, "TRUNK", StringComparison.OrdinalIgnoreCase);
        bool looksUplink = UplinkWords.IsMatch(description ?? "");
        bool lagMember = !string.IsNullOrEmpty(b.Lag);

        // ---- LAG membership change ----
        bool lagChange = false;
        bool leaving = false;
        string? lagId = LagId?.Trim();
        switch (LagMode)
        {
            case LagMode.Remove:
                if (lagMember) { lagChange = true; leaving = true; }
                break;
            case LagMode.Join:
            case LagMode.CreateJoin:
                if (string.IsNullOrEmpty(lagId)) { p.Skip = "no LAG number given"; return p; }
                if (b.Lag == lagId) break;
                bool exists = knownLags != null && knownLags.Contains(lagId!);
                if (LagMode == LagMode.Join && knownLags != null && !exists) { p.Skip = $"LAG {lagId} does not exist on this switch (use Create new LAG)"; return p; }
                if (LagMode == LagMode.CreateJoin && exists) { p.Skip = $"LAG {lagId} already exists (use Join LAG)"; return p; }
                lagChange = true;
                break;
        }
        if (lagChange && !allowTrunkAndUplink)
        {
            p.Skip = "LAG change (guard: tick 'Allow trunk and uplink ports' to override)";
            return p;
        }

        bool touchesRisky = (Enable == false && b.Enable) || (BpduGuard == true && !b.BpduGuard) || (RootGuard == true && !b.RootGuard)
                         || (AdminEdge == true && !b.AdminEdge) || (LoopProtect == true && !b.LoopProtect);
        if (touchesRisky && !allowTrunkAndUplink && (trunk || looksUplink || lagMember))
        {
            p.Skip = lagMember ? $"LAG member (lag {b.Lag}) (guard: tick 'Allow trunk and uplink ports' to override)" : trunk ? "TRUNK port (guard: tick 'Allow trunk and uplink ports' to override)"
                           : "description looks like an uplink (guard: tick 'Allow trunk and uplink ports' to override)";
            return p;
        }

        var other = new JsonObject();
        var otherChanges = new List<string>();
        if (Enable is bool e && e != b.Enable)
        {
            other["enable"] = e;
            otherChanges.Add($"admin {(b.Enable ? "up" : "down")} -> {(e ? "up" : "down")}");
        }
        if (MakeTrunk)
        {
            if (AccessVlan != null || LagMode != LagMode.NoChange) { p.Skip = "make trunk cannot be combined with an access VLAN or a LAG change"; return p; }
            if (!allowTrunkAndUplink) { p.Skip = "make trunk (guard: tick 'Allow trunk and uplink ports' to override)"; return p; }
            if (lagMember) { p.Skip = "LAG member, remove it from the LAG first"; return p; }
            if (TrunkNative is not int nat || nat < 1 || nat > 4094 || TrunkVlans.Count == 0 || ExpandVlans(TrunkVlans) == null) { p.Skip = "trunk needs a native VLAN and a valid allowed VLAN list"; return p; }
            if (BpduGuard == true || RootGuard == true || AdminEdge == true || LoopProtect == true) { p.Skip = "BPDU guard, root guard, admin edge and loop protect are switched off on a trunk, do not set them to On"; return p; }
            var want = ExpandVlans(TrunkVlans)!;
            bool same = false;
            if (trunk && b.Switchport != null && JsonNode.Parse(b.Switchport) is JsonObject cur)
            {
                var have = cur["trunk-vlan-ranges"] is JsonArray ja ? ExpandVlans(ja.Select(x => J.Str(x) ?? "")) : null;
                same = J.Int(cur["native-vlan"]) == nat && !J.Bool(cur["trunk-vlan-all"]) && have != null && have.SetEquals(want);
            }
            if (!same)
            {
                var sp = new JsonObject { ["interface-mode"] = "TRUNK", ["native-vlan"] = nat, ["tag"] = false, ["trunk-vlan-all"] = false };
                var arr = new JsonArray(); foreach (var x in TrunkVlans) arr.Add(x.Trim());
                sp["trunk-vlan-ranges"] = arr;
                other["switchport"] = sp;
                otherChanges.Add($"mode {b.Mode ?? "?"} -> TRUNK, native VLAN {nat}, allowed {string.Join(",", TrunkVlans.Select(x => x.Trim()))}");
            }
            // end-device protections do not belong on a trunk
            var tstp = new JsonObject();
            if (b.BpduGuard) { tstp["bpdu-guard"] = false; otherChanges.Add("BPDU guard on -> off (trunk)"); }
            if (b.RootGuard) { tstp["root-guard"] = false; otherChanges.Add("root guard on -> off (trunk)"); }
            if (b.AdminEdge) { tstp["admin-edge-port"] = false; otherChanges.Add("admin edge on -> off (trunk)"); }
            if (tstp.Count > 0) other["stp"] = tstp;
            if (b.LoopProtect) { other["loop-protect"] = new JsonObject { ["enable"] = false }; otherChanges.Add("loop protect on -> off (trunk)"); }
        }
        if (AccessVlan is int v)
        {
            if (LagMode == LagMode.Join || LagMode == LagMode.CreateJoin)
            {
                p.Skip = "access VLAN cannot be combined with joining a LAG";
                return p;
            }
            bool isAccess = string.Equals(b.Mode, "ACCESS", StringComparison.OrdinalIgnoreCase);
            if (!isAccess && !leaving && !(trunk && allowTrunkAndUplink))
            {
                p.Skip = trunk ? "TRUNK port (guard: tick 'Allow trunk and uplink ports' to convert it to ACCESS)"
                               : $"mode is {b.Mode ?? "unknown"}, access VLAN only applies to ACCESS ports";
                return p;
            }
            if (trunk && !leaving && !string.IsNullOrEmpty(b.Lag)) { p.Skip = "LAG member, remove it from the LAG first"; return p; }
            if (!isAccess || b.AccessVlan != v || leaving)
            {
                other["switchport"] = new JsonObject { ["interface-mode"] = "ACCESS", ["access-vlan"] = v };
                otherChanges.Add(trunk && !leaving ? $"mode TRUNK -> ACCESS, access VLAN {v}" : $"access VLAN {b.AccessVlan?.ToString() ?? "?"} -> {v}");
            }
        }
        var stp = new JsonObject();
        if (BpduGuard is bool x1 && x1 != b.BpduGuard) { stp["bpdu-guard"] = x1; otherChanges.Add($"BPDU guard {OnOff(b.BpduGuard)} -> {OnOff(x1)}"); }
        if (RootGuard is bool x2 && x2 != b.RootGuard) { stp["root-guard"] = x2; otherChanges.Add($"root guard {OnOff(b.RootGuard)} -> {OnOff(x2)}"); }
        if (AdminEdge is bool x3 && x3 != b.AdminEdge) { stp["admin-edge-port"] = x3; otherChanges.Add($"admin edge {OnOff(b.AdminEdge)} -> {OnOff(x3)}"); }
        if (stp.Count > 0) other["stp"] = stp;
        if (LoopProtect is bool l)
        {
            if (l && (!b.LoopProtect || b.LoopAction != "TX_DISABLE"))
            {
                other["loop-protect"] = new JsonObject { ["enable"] = true, ["action"] = "TX_DISABLE" };
                otherChanges.Add($"loop protect {OnOff(b.LoopProtect)} -> on (TX_DISABLE)");
            }
            else if (!l && b.LoopProtect)
            {
                other["loop-protect"] = new JsonObject { ["enable"] = false };
                otherChanges.Add("loop protect on -> off");
            }
        }

        if (ClearDescription)
        {
            if (b.Desc != null) { other["description"] = null; otherChanges.Add($"description \"{b.Desc}\" -> (cleared)"); }
        }
        else if (Description != null && Description != (b.Desc ?? ""))
        {
            if (Description.Length > 64) { p.Skip = "description is longer than 64 characters"; return p; }
            other["description"] = Description;
            otherChanges.Add($"description \"{b.Desc ?? ""}\" -> \"{Description}\"");
        }

        if (Poe is bool pe)
        {
            if (b.Poe == null) { p.Skip = "port has no PoE setting (not PoE capable?)"; return p; }
            if (pe != b.Poe) { other["poe"] = new JsonObject { ["enabled"] = pe }; otherChanges.Add($"PoE {OnOff(b.Poe.Value)} -> {OnOff(pe)}"); }
        }

        if (!lagChange)
        {
            p.Body = other;
            p.Changes.AddRange(otherChanges);
            return p;
        }

        if (leaving)
        {
            int left = lagMembers != null && lagMembers.TryGetValue(b.Lag!, out var m) ? m - 1 : -1;
            p.LagFrom = b.Lag;
            p.LagLeave = b.Lag;
            p.Body = new JsonObject();
            p.Changes.Add($"remove from LAG {b.Lag}" + (left >= 0 ? $" ({left} member(s) left{(left == 0 ? ", LAG will be empty and DELETED" : "")})" : ""));
            if (other.Count > 0) { p.Body2 = other; p.Changes.AddRange(otherChanges); }
            return p;
        }

        // join / create + join
        // switching BPDU guard, root guard, admin edge and loop protect OFF is part of joining (done automatically); anything else is not combined with a LAG step
        var cleanup = new HashSet<string> { "BPDU guard on -> off", "root guard on -> off", "admin edge on -> off", "loop protect on -> off" };
        var blocking = otherChanges.Where(c => !cleanup.Contains(c)).ToList();
        if (blocking.Count > 0) { p.Skip = "set the LAG on its own, other settings pending: " + string.Join("; ", blocking); return p; }
        p.Body = new JsonObject();
        var pre = new JsonObject(); var preStp = new JsonObject();
        if (b.BpduGuard) { preStp["bpdu-guard"] = false; p.Changes.Add("BPDU guard on -> off (not for a LAG to another switch)"); }
        if (b.RootGuard) { preStp["root-guard"] = false; p.Changes.Add("root guard on -> off (not for a LAG to another switch)"); }
        if (b.AdminEdge) { preStp["admin-edge-port"] = false; p.Changes.Add("admin edge on -> off (not for a LAG to another switch)"); }
        if (preStp.Count > 0) pre["stp"] = preStp;
        if (b.LoopProtect) { pre["loop-protect"] = new JsonObject { ["enable"] = false }; p.Changes.Add("loop protect on -> off (not for a LAG to another switch)"); }
        if (pre.Count > 0) p.PreBody = pre;
        p.LagJoin = lagId;
        if (lagMember) p.LagLeave = b.Lag;
        if (LagMode == LagMode.CreateJoin) { p.CreateLagName = lagId; p.CreateLagDescription = LagDescription; p.Changes.Add($"create LAG {lagId} (LACP active, trunk allowed all, native VLAN 1)"); }
        p.Changes.Add($"join LAG {lagId}" + (lagMember ? $" (leaves LAG {b.Lag})" : ""));
        p.LagFrom = b.Lag;
        return p;
    }

    public bool Verify(PortState a, out string why)
    {
        var bad = new List<string>();
        if (LagMode == LagMode.Remove && !string.IsNullOrEmpty(a.Lag)) bad.Add($"still in LAG {a.Lag}");
        if ((LagMode == LagMode.Join || LagMode == LagMode.CreateJoin) && a.Lag != LagId?.Trim()) bad.Add($"LAG is {a.Lag ?? "none"}");
        if (Enable is bool e && a.Enable != e) bad.Add("admin state");
        if (Poe is bool pe && a.Poe != pe) bad.Add("PoE");
        if (ClearDescription && a.Desc != null) bad.Add($"description is still \"{a.Desc}\"");
        else if (!ClearDescription && Description != null && (a.Desc ?? "") != Description) bad.Add($"description is \"{a.Desc ?? ""}\"");
        if (MakeTrunk)
        {
            if (!string.Equals(a.Mode, "TRUNK", StringComparison.OrdinalIgnoreCase)) bad.Add($"mode is {a.Mode ?? "none"}");
            else if (a.Switchport != null && JsonNode.Parse(a.Switchport) is JsonObject cur)
            {
                if (J.Int(cur["native-vlan"]) != TrunkNative) bad.Add($"native VLAN is {J.Int(cur["native-vlan"])}");
                var have = cur["trunk-vlan-ranges"] is JsonArray ja ? ExpandVlans(ja.Select(x => J.Str(x) ?? "")) : null;
                var want = ExpandVlans(TrunkVlans);
                if (J.Bool(cur["trunk-vlan-all"]) || have == null || want == null || !have.SetEquals(want)) bad.Add("allowed VLANs differ");
            }
            if (a.BpduGuard || a.RootGuard || a.AdminEdge || a.LoopProtect) bad.Add("end-device protections still on");
        }
        if (AccessVlan is int v && a.AccessVlan != v) bad.Add($"access VLAN is {a.AccessVlan}");
        if (AccessVlan is int v2 && !string.Equals(a.Mode, "ACCESS", StringComparison.OrdinalIgnoreCase)) bad.Add($"mode is {a.Mode ?? "none"}");
        if (BpduGuard is bool b1 && a.BpduGuard != b1) bad.Add("BPDU guard");
        if (RootGuard is bool b2 && a.RootGuard != b2) bad.Add("root guard");
        if (AdminEdge is bool b3 && a.AdminEdge != b3) bad.Add("admin edge");
        if (LoopProtect is bool l)
        {
            if (a.LoopProtect != l) bad.Add("loop protect");
            else if (l && a.LoopAction != "TX_DISABLE") bad.Add($"loop protect action is {a.LoopAction}");
        }
        why = string.Join(", ", bad);
        return bad.Count == 0;
    }

    static string OnOff(bool b) => b ? "on" : "off";
}

public sealed class CentralClient : IDisposable
{
    public const string DefaultTokenUrl = "https://sso.common.cloud.hpe.com/as/token.oauth2";
    readonly string _tokenUrl;
    const string Net = "/network-config/v1alpha1/ethernet-interfaces";
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    readonly string _base, _id, _secret;
    string? _token;
    DateTime _exp = DateTime.MinValue;

    public CentralClient(string baseUrl, string clientId, string clientSecret, string? tokenUrl = null)
    {
        _tokenUrl = tokenUrl ?? DefaultTokenUrl;
        _base = baseUrl.Trim().TrimEnd('/');
        _id = clientId.Trim();
        _secret = clientSecret;
    }

    static string Trim(string s) => s.Length > 400 ? s[..400] + "..." : s;

    public async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token != null && _exp > DateTime.UtcNow.AddMinutes(5)) return _token;
        using var req = new HttpRequestMessage(HttpMethod.Post, _tokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _id,
                ["client_secret"] = _secret
            })
        };
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new CentralException((int)resp.StatusCode, $"Token request failed (HTTP {(int)resp.StatusCode}): {Trim(text)}");
        var j = JsonNode.Parse(text)!;
        _token = J.Str(j["access_token"]) ?? throw new CentralException(0, "Token response has no access_token");
        _exp = DateTime.UtcNow.AddSeconds(J.Int(j["expires_in"]) ?? 3600);
        return _token;
    }

    async Task<JsonNode?> SendAsync(HttpMethod m, string path, JsonNode? body, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            using var req = new HttpRequestMessage(m, _base + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
            if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            int code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode) return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            if (code == 401) { _token = null; continue; }
            if (code == 429 || code >= 500) { await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct); continue; }
            throw new CentralException(code, $"{m} {path} -> HTTP {code}: {Trim(text)}");
        }
        throw new CentralException(0, $"{m} {path} failed after 5 attempts");
    }

    public static readonly string[] Functions = { "ACCESS_SWITCH", "AGG_SWITCH", "CORE_SWITCH" };

    static string? AsFunction(string? v)
    {
        if (v == null) return null;
        var u = v.Trim().ToUpperInvariant().Replace(' ', '_');
        if (Functions.Contains(u)) return u;
        if (u.StartsWith("AOSS")) return null;   // AOS-S personas are not CX
        if (u.Contains("CORE")) return "CORE_SWITCH";
        if (u.Contains("AGG")) return "AGG_SWITCH";
        if (u.Contains("ACCESS")) return "ACCESS_SWITCH";
        return null;
    }

    /// <summary>Lists the switches the API client can see (paged). Field names are read tolerantly (camelCase or snake_case).</summary>
    public async Task<(List<SwitchDef> list, string? rawFirst)> ListSwitchesAsync(CancellationToken ct)
    {
        var all = new List<SwitchDef>();
        string? next = null, rawFirst = null;
        for (int page = 0; page < 200; page++)
        {
            var path = "/network-monitoring/v1/switches?limit=100" + (next != null ? "&next=" + Uri.EscapeDataString(next) : "");
            var r = await SendAsync(HttpMethod.Get, path, null, ct);
            var arr = r?["items"] as JsonArray ?? r?["switches"] as JsonArray ?? r as JsonArray;
            if (arr == null || arr.Count == 0) break;
            rawFirst ??= arr[0]?.ToJsonString();
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                var serial = J.Str(o["serialNumber"]) ?? J.Str(o["serial_number"]) ?? J.Str(o["serial"]);
                var name = J.Str(o["deviceName"]) ?? J.Str(o["name"]) ?? serial;
                if (serial == null || name == null) continue;
                var stack = J.Str(o["stackId"]) ?? J.Str(o["stack_id"]);
                if (string.IsNullOrWhiteSpace(stack)) stack = null;
                var fn = AsFunction(J.Str(o["deviceFunction"])) ?? AsFunction(J.Str(o["device_function"])) ?? AsFunction(J.Str(o["function"]));
                all.Add(new SwitchDef(name, serial, fn ?? "", stack));
            }
            next = J.Str(r?["next"]);
            if (string.IsNullOrEmpty(next)) break;
        }
        // The switch list does not carry the device function; the device inventory does (persona).
        var inv = await GetInventoryFunctionsAsync(ct);
        for (int i = 0; i < all.Count; i++)
            if (all[i].Function.Length == 0 && inv.TryGetValue(all[i].Serial, out var f) && f != null)
                all[i] = new SwitchDef(all[i].Name, all[i].Serial, f, all[i].StackId);
        // A stack shows up once, under its stack id.
        var result = all.Where(d => d.StackId == null).Concat(all.Where(d => d.StackId != null).GroupBy(d => d.StackId)
            .Select(g => g.FirstOrDefault(x => x.Function.Length > 0) ?? g.First())).ToList();
        return (result, rawFirst);
    }

    public string FunctionNote { get; private set; } = "";

    /// <summary>Device function (persona) per serial from /network-monitoring/v1/device-inventory. Null = not reported or not a known value.</summary>
    async Task<Dictionary<string, string?>> GetInventoryFunctionsAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string?>();
        var raws = new SortedDictionary<string, int>();
        try
        {
            string? next = null;
            for (int page = 0; page < 50; page++)
            {
                var r = await SendAsync(HttpMethod.Get, "/network-monitoring/v1/device-inventory?limit=1000" + (next != null ? "&next=" + Uri.EscapeDataString(next) : ""), null, ct);
                var arr = r?["items"] as JsonArray;
                if (arr == null || arr.Count == 0) break;
                foreach (var n in arr)
                {
                    if (n is not JsonObject o) continue;
                    var serial = J.Str(o["serialNumber"]);
                    if (serial == null) continue;
                    var raw = J.Str(o["deviceFunction"]) ?? "(none)";
                    raws[raw] = raws.GetValueOrDefault(raw) + 1;
                    map[serial] = AsFunction(raw);
                }
                next = J.Str(r?["next"]);
                if (string.IsNullOrEmpty(next)) break;
            }
            FunctionNote = "device function values in inventory: " + string.Join(", ", raws.Select(kv => $"{kv.Key} x{kv.Value}"));
        }
        catch (Exception ex) { FunctionNote = "device function not available from inventory: " + ex.Message; }
        return map;
    }

    /// <summary>Live link state per port from Central monitoring (stack: stack id). Key = port like 1/1/10.</summary>
    public async Task<Dictionary<string, LinkInfo>> GetLinkStatesAsync(SwitchDef d, CancellationToken ct)
    {
        var res = new Dictionary<string, LinkInfo>();
        string id = d.StackId ?? d.Serial;
        const int page = 1000;
        for (int offset = 0; offset < 20000; offset += page)
        {
            var r = await SendAsync(HttpMethod.Get, $"/network-monitoring/v1/switches/{Uri.EscapeDataString(id)}/interfaces?limit={page}&offset={offset}", null, ct);
            var arr = r?["items"] as JsonArray ?? r as JsonArray;
            if (arr == null || arr.Count == 0) break;
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                var key = PortKey(J.Str(o["name"])) ?? PortKey(J.Str(o["id"]));
                if (key == null) continue;
                var oper = J.Str(o["operStatus"]) ?? J.Str(o["status"]) ?? "";
                var nb = J.Str(o["neighbour"]) ?? "";
                var nbp = J.Str(o["neighbourPort"]) ?? "";
                res[key] = new LinkInfo(oper, nb, nbp);
            }
            if (arr.Count < page) break;
        }
        return res;
    }

    public static string? PortKey(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"(\d+/\d+/\d+)$");
        return m.Success ? m.Groups[1].Value : null;
    }

    // ---------- LAGs (portchannels) ----------
    const string Pc = "/network-config/v1alpha1/portchannels";
    /// <summary>Prefix of existing portchannel names (for example "lag" or ""), so a new LAG gets the same naming style.</summary>
    public string LagNamePrefix { get; private set; } = "";

    /// <summary>VLANs defined for the device (effective config, hierarchy merged). Id and name.</summary>
    public string VlanNote { get; private set; } = "";

    public async Task<List<(int Id, string Name)>> ListVlansAsync(string scopeId, CancellationToken ct)
    {
        var notes = new List<string>();
        SortedDictionary<int, string> best = new();
        foreach (var extra in new[] { "&effective=true", "&effective=false", "" })
        {
            var res = new SortedDictionary<int, string>();
            try
            {
                var r = await SendAsync(HttpMethod.Get, $"/network-config/v1alpha1/layer2-vlan?view-type=LOCAL&scope-id={Uri.EscapeDataString(scopeId)}{extra}", null, ct);
                JsonArray? arr = r?["l2-vlan"] as JsonArray ?? r?["items"] as JsonArray ?? r as JsonArray;
                if (arr != null)
                    foreach (var n in arr)
                    {
                        if (n is not JsonObject o) continue;
                        int id;
                        if (J.Int(o["vlan"]) is int vi) id = vi;
                        else if (!int.TryParse(J.Str(o["vlan"]) ?? J.Str(o["name"]), out id)) continue;
                        var nm = J.Str(o["name"]) ?? J.Str(o["description"]) ?? J.Str(o["description-alias"]) ?? "";
                        if (nm == id.ToString()) nm = "";
                        res[id] = nm;
                    }
                var raw = r?.ToJsonString() ?? "(empty)";
                notes.Add($"[{(extra == "" ? "plain" : extra.TrimStart('&'))}] {res.Count} VLANs" + (res.Count == 0 ? ", raw: " + (raw.Length > 300 ? raw[..300] + "..." : raw) : ""));
            }
            catch (CentralException ex) { notes.Add($"[{(extra == "" ? "plain" : extra.TrimStart('&'))}] HTTP {ex.Status}"); }
            if (res.Count > best.Count) best = res;
            if (best.Count > 0) break;
        }
        VlanNote = string.Join("; ", notes);
        return best.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public async Task<List<LagInfo>> ListLagsAsync(string scopeId, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, $"{Pc}?view-type=LOCAL&scope-id={Uri.EscapeDataString(scopeId)}", null, ct);
        var res = new List<LagInfo>();
        JsonArray? arr = r as JsonArray;
        if (arr == null && r is JsonObject ro)
            foreach (var kv in ro) if (kv.Value is JsonArray a && a.Count > 0 && a[0] is JsonObject) { arr = a; break; }
        if (arr == null) return res;
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            var raw = J.Str(o["name"]); if (raw == null) continue;
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"^(\D*)(\d+)$");
            if (!m.Success) continue;
            var ports = new List<string>();
            if (o["port-list"] is JsonArray pl) foreach (var x in pl) { var ps = J.Str(x); if (ps != null) ports.Add(ps); }
            res.Add(new LagInfo(raw, m.Groups[2].Value, (J.Str(o["description"]) ?? "").Trim(), ports));
            LagNamePrefix = m.Groups[1].Value;
        }
        return res;
    }

    public Task CreateLagAsync(string scopeId, string function, string id, string? description, string firstPort, CancellationToken ct)
    {
        var raw = LagNamePrefix + id;
        var body = new JsonObject
        {
            ["name"] = raw,
            ["enable"] = true,
            ["port-list"] = new JsonArray { firstPort },
            ["lacp"] = new JsonObject { ["mode"] = "ACTIVE" },
            ["switchport"] = new JsonObject { ["interface-mode"] = "TRUNK", ["native-vlan"] = 1, ["trunk-vlan-all"] = true }
        };
        if (!string.IsNullOrWhiteSpace(description)) body["description"] = description!.Trim();
        return SendAsync(HttpMethod.Post,
            $"{Pc}/{Uri.EscapeDataString(raw)}?object-type=LOCAL&scope-id={Uri.EscapeDataString(scopeId)}&device-function={Uri.EscapeDataString(function)}", body, ct);
    }

    public async Task<string> SetLagPortListAsync(string scopeId, string function, string rawName, IEnumerable<string> ports, CancellationToken ct)
    {
        var arr = new JsonArray(); foreach (var p in ports) arr.Add(p);
        var r = await SendAsync(HttpMethod.Patch,
            $"{Pc}/{Uri.EscapeDataString(rawName)}?object-type=LOCAL&scope-id={Uri.EscapeDataString(scopeId)}&device-function={Uri.EscapeDataString(function)}",
            new JsonObject { ["port-list"] = arr }, ct);
        return $"PATCH {Pc}/{rawName} body {{\"port-list\":{arr.ToJsonString()}}} -> " + (r?.ToJsonString() ?? "(empty response)");
    }

    /// <summary>Raw portchannel object as Central returns it (diagnostics).</summary>
    public async Task<string> GetLagRawAsync(string scopeId, string id, bool effective, CancellationToken ct)
    {
        try
        {
            var r = await SendAsync(HttpMethod.Get, $"{Pc}/{Uri.EscapeDataString(LagNamePrefix + id)}?view-type=LOCAL&scope-id={Uri.EscapeDataString(scopeId)}&effective={(effective ? "true" : "false")}", null, ct);
            var t = r?.ToJsonString() ?? "(empty)";
            return t.Length > 1500 ? t[..1500] + "..." : t;
        }
        catch (Exception ex) { return "ERROR " + ex.Message; }
    }

    /// <summary>Ports that are members of a LAG, read from the interface config (membership is changed through the portchannel, not the port).</summary>
    public async Task<List<string>> LagMembersAsync(string scopeId, string id, CancellationToken ct)
    {
        var ports = await GetPortsAsync(scopeId, ct);
        return ports.Where(o => J.Str(o["portchannel-lag"]) == id).Select(o => J.Str(o["name"])!).ToList();
    }

    public async Task<string> LagLeaveAsync(string scopeId, string function, string port, string id, CancellationToken ct)
    {
        var m = await LagMembersAsync(scopeId, id, ct);
        if (!m.Remove(port)) throw new CentralException(0, $"Port {port} is not a member of LAG {id} in the config.");
        // LAG object already gone but the port still points to it (dangling reference): clear the reference on the port
        var obj = await SendAsync(HttpMethod.Get, $"{Pc}/{Uri.EscapeDataString(LagNamePrefix + id)}?view-type=LOCAL&scope-id={Uri.EscapeDataString(scopeId)}", null, ct);
        if (obj?["name"] == null)
        {
            var rr = await SendAsync(HttpMethod.Patch,
                $"{Net}/{Uri.EscapeDataString(port)}?object-type=LOCAL&scope-id={Uri.EscapeDataString(scopeId)}&device-function={Uri.EscapeDataString(function)}",
                new JsonObject { ["portchannel-lag"] = null }, ct);
            return $"LAG {id} no longer exists; cleared portchannel-lag on {port} -> " + (rr?.ToJsonString() ?? "(empty response)");
        }
        if (m.Count == 0) return await DeleteLagAsync(scopeId, function, id, ct);   // an emptied LAG is not pushed to the switch, so the LAG itself is removed
        return await SetLagPortListAsync(scopeId, function, LagNamePrefix + id, m, ct);
    }

    public async Task<string> DeleteLagAsync(string scopeId, string function, string id, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Delete,
            $"{Pc}/{Uri.EscapeDataString(LagNamePrefix + id)}?object-type=LOCAL&scope-id={Uri.EscapeDataString(scopeId)}&device-function={Uri.EscapeDataString(function)}",
            null, ct);
        return $"DELETE {Pc}/{LagNamePrefix + id} -> " + (r?.ToJsonString() ?? "(empty response)");
    }

    public async Task<string> LagJoinAsync(string scopeId, string function, string port, string id, CancellationToken ct)
    {
        var m = await LagMembersAsync(scopeId, id, ct);
        if (!m.Contains(port)) m.Add(port);
        return await SetLagPortListAsync(scopeId, function, LagNamePrefix + id, m, ct);
    }

    /// <summary>Waits until the port reports the expected LAG (null = none). Returns the last state read.</summary>
    public async Task<PortState> WaitForLagAsync(string scopeId, string port, string? expected, CancellationToken ct)
    {
        PortState st = PortState.From(await GetPortAsync(scopeId, port, ct));
        for (int i = 0; i < 12 && (st.Lag ?? "") != (expected ?? ""); i++)
        {
            await Task.Delay(1500, ct);
            st = PortState.From(await GetPortAsync(scopeId, port, ct));
        }
        return st;
    }

    public async Task<string> GetScopeIdAsync(SwitchDef d, CancellationToken ct)
    {
        string id = d.StackId ?? d.Serial;   // stacks have their device scope under the stack id
        var h = await SendAsync(HttpMethod.Get, $"/network-config/v1/hierarchy?id={Uri.EscapeDataString(id)}&type=device", null, ct);
        if (h?["items"] is JsonArray items)
            foreach (var it in items)
                if (it?["hierarchy"] is JsonArray hs)
                    foreach (var e in hs)
                        if (J.Str(e?["scopeType"]) == "device" && e?["scopeId"] is JsonNode sid)
                            return sid.ToString();
        throw new CentralException(0, $"No device scope found for {d.Name}");
    }

    public async Task<List<JsonObject>> GetPortsAsync(string scopeId, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, $"{Net}?view-type=LOCAL&scope-id={Uri.EscapeDataString(scopeId)}", null, ct);
        var list = new List<JsonObject>();
        if (r?["interface"] is JsonArray arr)
            foreach (var n in arr)
                if (n is JsonObject o && (J.Str(o["name"]) ?? "").Count(c => c == '/') == 2) list.Add(o);
        return list;
    }

    public async Task<JsonObject> GetPortAsync(string scopeId, string port, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, $"{Net}/{Uri.EscapeDataString(port)}?view-type=LOCAL&scope-id={Uri.EscapeDataString(scopeId)}", null, ct);
        if (r?["interface"] is JsonArray a && a.Count > 0 && a[0] is JsonObject o) return o;
        if (r is JsonObject ro && ro["name"] != null) return ro;
        throw new CentralException(0, $"Unexpected response for port {port}");
    }

    public Task PatchPortAsync(string scopeId, string port, string function, JsonObject body, CancellationToken ct)
        => SendAsync(HttpMethod.Patch,
            $"{Net}/{Uri.EscapeDataString(port)}?object-type=LOCAL&scope-id={Uri.EscapeDataString(scopeId)}&device-function={Uri.EscapeDataString(function)}", body, ct);

    public void Dispose() => _http.Dispose();
}

/// <summary>Snapshot and rollback use the same JSONL format as Set-Phase1.ps1, so both tools can roll back each other's runs.</summary>
public static class Snapshots
{
    public static JsonObject Before(PortState s) => new()
    {
        ["enable"] = s.Enable,
        ["mode"] = s.Mode,
        ["accessVlan"] = s.AccessVlan,
        ["switchport"] = s.Switchport != null ? JsonNode.Parse(s.Switchport) : null,
        ["bpduGuard"] = s.BpduGuard,
        ["rootGuard"] = s.RootGuard,
        ["adminEdgePort"] = s.AdminEdge,
        ["loopProtect"] = s.LoopProtect,
        ["loopAction"] = s.LoopAction,
        ["lag"] = s.Lag,
        ["poe"] = s.Poe,
        ["description"] = s.Desc ?? ""
    };

    public static string Line(string sw, string port, string scope, string function, PortState before) =>
        new JsonObject
        {
            ["time"] = DateTime.Now.ToString("s"),
            ["switch"] = sw,
            ["port"] = port,
            ["scopeId"] = scope,
            ["function"] = function,
            ["before"] = Before(before)
        }.ToJsonString();

    public static JsonObject RollbackBody(JsonNode before)
    {
        var b = new JsonObject
        {
            ["enable"] = J.Bool(before["enable"], true),
            ["stp"] = new JsonObject
            {
                ["bpdu-guard"] = J.Bool(before["bpduGuard"]),
                ["root-guard"] = J.Bool(before["rootGuard"]),
                ["admin-edge-port"] = J.Bool(before["adminEdgePort"])
            },
            ["loop-protect"] = new JsonObject { ["enable"] = J.Bool(before["loopProtect"]) }
        };
        if (before["poe"] != null) b["poe"] = new JsonObject { ["enabled"] = J.Bool(before["poe"]) };
        // the description is restored by the caller, which knows the current value (older snapshots have no description: left alone)
        if (J.Str(before["mode"]) == "ACCESS" && J.Int(before["accessVlan"]) is int v)
            b["switchport"] = new JsonObject { ["interface-mode"] = "ACCESS", ["access-vlan"] = v };
        else if (J.Str(before["mode"]) == "TRUNK" && before["switchport"] is JsonObject sp)
            b["switchport"] = sp.DeepClone();   // full trunk definition (native VLAN, tag, allowed VLANs)
        return b;
    }
}

public sealed class SavedSettings
{
    public string BaseUrl { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? SecretProtected { get; set; }   // DPAPI, current Windows user only
}

public static class SettingsStore
{
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PortPatrol");
    static string FilePath => Path.Combine(Dir, "settings.json");
    public static string RunsDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortPatrol", "Runs");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static (SavedSettings s, string secret) Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(FilePath)) ?? new SavedSettings();
                string secret = "";
                if (!string.IsNullOrEmpty(s.SecretProtected))
                    secret = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s.SecretProtected), null, DataProtectionScope.CurrentUser));
                return (s, secret);
            }
        }
        catch { /* unreadable or other user: start empty */ }
        return (new SavedSettings(), "");
    }

    public static void Save(string baseUrl, string clientId, string secret)
    {
        Directory.CreateDirectory(Dir);
        var s = new SavedSettings
        {
            BaseUrl = baseUrl,
            ClientId = clientId,
            SecretProtected = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser))
        };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
    }

    // Device function choices live in memory only. Nothing about switches is written to disk for this.
    static Dictionary<string, string> _functions = new();
    public static Dictionary<string, string> LoadFunctions() => new(_functions);
    public static void SaveFunctions(Dictionary<string, string> d) { _functions = new(d); }
    public static void ClearFunctions() { _functions = new(); }
    /// <summary>Removes the functions.json that older versions left behind.</summary>
    public static void PurgeLegacy() { try { var f = Path.Combine(Dir, "functions.json"); if (File.Exists(f)) File.Delete(f); } catch { } }

    public static void Forget() { try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { } }
}

public sealed record LagInfo(string RawName, string Id, string Description, List<string> Ports);
public sealed record LinkInfo(string Oper, string Neighbour, string NeighbourPort);
