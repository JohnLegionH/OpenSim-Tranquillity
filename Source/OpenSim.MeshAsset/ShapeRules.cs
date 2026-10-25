/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace OpenSim.MeshAsset;

/// <summary>
/// The visual parameters of the viewer's avatar definition (avatar_lad.xml) that matter for the skeleton: each
/// parameter's range and default, the bones it scales or moves, and the parameters that drive others. The file is
/// supplied by the caller; nothing from it is built in. A shape wearable stores a weight for every parameter of the
/// shape; a "driver" parameter (for example Eye Spacing) sets the weight of the animatable parameter it drives (the
/// eye bone offset) by a fixed mapping (LLDriverParam::getDrivenWeight in the viewer), so the skeleton the viewer
/// poses is the one those resolved weights give, not the one the driven parameters' own defaults give.
/// </summary>
public sealed class ShapeRules
{
    /// <summary>One bone a parameter touches: its scale is changed by weight times <see cref="Scale"/>, its offset by weight times <see cref="Offset"/>.</summary>
    public sealed record BoneEffect(string Bone, Vector3? Scale, Vector3? Offset);

    public sealed record Slider(int Id, string Name, string? Label, int Group, string? Wearable, float Min, float Max, float Default, IReadOnlyList<BoneEffect> Bones);

    /// <summary>How a driver's weight maps to a driven parameter's: up the slope from <see cref="Min1"/> to <see cref="Max1"/>, flat to <see cref="Max2"/>, down to <see cref="Min2"/>.</summary>
    public sealed record Drive(int DrivenId, float Min1, float Max1, float Max2, float Min2);

    readonly Dictionary<int, Slider> _params = new();
    readonly Dictionary<int, List<Drive>> _drivers = new();

    public IReadOnlyDictionary<int, Slider> Params => _params;
    public IReadOnlyDictionary<int, List<Drive>> Drivers => _drivers;

    public static ShapeRules Load(string avatarDefinitionPath) => Parse(XDocument.Load(avatarDefinitionPath));

    public static ShapeRules Parse(XDocument doc)
    {
        var rules = new ShapeRules();
        foreach (var p in doc.Descendants("param"))
        {
            if (!int.TryParse((string?)p.Attribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) continue;
            float min = Num(p, "value_min", 0), max = Num(p, "value_max", 1);
            // the viewer clamps an out-of-range default into the range (LLVisualParamInfo::parseXml)
            float def = Math.Clamp(Num(p, "value_default", 0), min, max);
            var bones = new List<BoneEffect>();
            foreach (var b in p.Element("param_skeleton")?.Elements("bone") ?? Enumerable.Empty<XElement>())
                bones.Add(new BoneEffect((string?)b.Attribute("name") ?? "", Vec((string?)b.Attribute("scale")), Vec((string?)b.Attribute("offset"))));
            int.TryParse((string?)p.Attribute("group"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int group);
            rules._params[id] = new Slider(id, (string?)p.Attribute("name") ?? "", (string?)p.Attribute("label"), group, (string?)p.Attribute("wearable"), min, max, def, bones);
            var driver = p.Element("param_driver");
            if (driver == null) continue;
            var list = new List<Drive>();
            foreach (var d in driver.Elements("driven"))
            {
                if (!int.TryParse((string?)d.Attribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int drivenId)) continue;
                // defaults as the viewer sets them: the whole range up, no way down (LLDriverParamInfo::parseXml)
                float min1 = Num(d, "min1", min), max1 = Num(d, "max1", max);
                float max2 = Num(d, "max2", max1), min2 = Num(d, "min2", max1);
                list.Add(new Drive(drivenId, min1, max1, max2, min2));
            }
            if (list.Count > 0) rules._drivers[id] = list;
        }
        return rules;
    }

    static float Num(XElement e, string attribute, float fallback) =>
        float.TryParse((string?)e.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    static Vector3? Vec(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new Vector3(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture), float.Parse(p[2], CultureInfo.InvariantCulture));
    }

    /// <summary>The driven parameter's weight for a driver weight (the viewer's LLDriverParam::getDrivenWeight).</summary>
    public static float DrivenWeight(Slider driver, Drive d, Slider driven, float input)
    {
        float lo = driven.Min, hi = driven.Max;
        if (input <= d.Min1) return d.Min1 == d.Max1 && d.Min1 <= driver.Min ? hi : lo;
        if (input <= d.Max1) return lo + (input - d.Min1) / (d.Max1 - d.Min1) * (hi - lo);
        if (input <= d.Max2) return hi;
        if (input <= d.Min2) return hi + (input - d.Max2) / (d.Min2 - d.Max2) * (lo - hi);
        return d.Max2 >= driver.Max ? hi : lo;
    }

    /// <summary>
    /// The effective weight of every parameter: its default, replaced by the given value (clamped into the parameter's
    /// range), and then every driven parameter set from its driver. The given values are the ones a shape wearable holds,
    /// by parameter id.
    /// </summary>
    public Dictionary<int, float> Resolve(IReadOnlyDictionary<int, float>? values)
    {
        var w = _params.ToDictionary(kv => kv.Key, kv => kv.Value.Default);
        if (values != null)
            foreach (var (id, v) in values)
                if (_params.TryGetValue(id, out var s)) w[id] = Math.Clamp(v, s.Min, s.Max);
        foreach (var (driverId, drives) in _drivers.OrderBy(kv => kv.Key))
            foreach (var d in drives)
                if (_params.TryGetValue(d.DrivenId, out var driven))
                    w[d.DrivenId] = DrivenWeight(_params[driverId], d, driven, w[driverId]);
        return w;
    }

    /// <summary>The weight as the wire carries it: one byte across the parameter's range (the appearance message other viewers read).</summary>
    public static float WireQuantised(Slider s, float v)
    {
        float step = (s.Max - s.Min) / 255f;
        return s.Min + MathF.Round((Math.Clamp(v, s.Min, s.Max) - s.Min) / step) * step;
    }
}
