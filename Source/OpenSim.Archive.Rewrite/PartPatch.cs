/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Xml;

namespace OpenSim.Archive.Rewrite;

/// <summary>
/// One change to one element of a part, named by its path of child element names below the part's
/// SceneObjectPart element, for example "Name" or "Shape/ProfileShape".
/// </summary>
public sealed class ElementEdit
{
    public string Path { get; }
    public string Value { get; }
    /// <summary>True when <see cref="Value"/> is XML markup that replaces the element's content; false when it is plain text.</summary>
    public bool IsMarkup { get; }
    /// <summary>True when a part that lacks the element gets it added at the end of the part (only for an element directly below the part).</summary>
    public bool AddIfMissing { get; private set; }

    private ElementEdit(string path, string value, bool isMarkup)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("an element path is required", nameof(path));
        ArgumentNullException.ThrowIfNull(value);
        Path = path;
        Value = value;
        IsMarkup = isMarkup;
    }

    /// <summary>Replaces the content of the element with this text (escaped as needed).</summary>
    public static ElementEdit SetText(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try { XmlConvert.VerifyXmlChars(text); }
        catch (XmlException e) { throw new ArgumentException("the text holds a character XML cannot carry: " + e.Message, nameof(text)); }
        return new ElementEdit(path, text, false);
    }

    /// <summary>Like <see cref="SetText"/>, but a part that lacks the element gets it added at the end of the part. Only an element directly below the part can be added.</summary>
    public static ElementEdit SetTextOrAdd(string path, string text)
    {
        ElementEdit edit = SetText(path, text);
        edit.AddIfMissing = true;
        return edit;
    }

    /// <summary>Like <see cref="SetInnerXml"/>, but a part that lacks the element gets it added at the end of the part. Only an element directly below the part can be added.</summary>
    public static ElementEdit SetInnerXmlOrAdd(string path, string markup)
    {
        ElementEdit edit = SetInnerXml(path, markup);
        edit.AddIfMissing = true;
        return edit;
    }

    /// <summary>Replaces the content of the element with this XML markup, which must be well formed.</summary>
    public static ElementEdit SetInnerXml(string path, string markup)
    {
        ArgumentNullException.ThrowIfNull(markup);
        try
        {
            using XmlReader r = XmlReader.Create(new StringReader("<x>" + markup + "</x>"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            while (r.Read()) { }
        }
        catch (XmlException e) { throw new ArgumentException("the markup is not well formed: " + e.Message, nameof(markup)); }
        return new ElementEdit(path, markup, true);
    }
}

/// <summary>What a deferred patch can look at in the part it is about to edit.</summary>
public sealed class PartView
{
    private readonly Func<string, string> m_text;

    public PartView(ISet<string> present, Func<string, string> text)
    {
        Present = present;
        m_text = text;
    }

    /// <summary>The element paths (such as "Shape/ProfileShape") the part has.</summary>
    public ISet<string> Present { get; }

    /// <summary>The text of a leaf element as the file holds it (character references not decoded); null when the part has no such element or it is not unique.</summary>
    public string Text(string path) => m_text(path);
}

/// <summary>The edits for one part, found by the part's UUID wherever it sits in the archive's object files.</summary>
public sealed class PartPatch
{
    private readonly Func<PartView, IReadOnlyList<ElementEdit>> m_deferred;

    public Guid PartId { get; }
    /// <summary>The edits of an ordinary patch; empty for a deferred one (see <see cref="EditsFor"/>).</summary>
    public IReadOnlyList<ElementEdit> Edits { get; }

    public PartPatch(Guid partId, params ElementEdit[] edits)
    {
        if (edits == null || edits.Length == 0)
            throw new ArgumentException("a patch needs at least one edit", nameof(edits));
        PartId = partId;
        Edits = edits;
    }

    /// <summary>
    /// A patch whose edits depend on which elements the part has: <paramref name="make"/> is called with the part's element
    /// paths (such as "Shape/ProfileShape") when the object file is patched.
    /// </summary>
    public PartPatch(Guid partId, Func<ISet<string>, IReadOnlyList<ElementEdit>> make)
        : this(partId, WrapPresent(make))
    {
    }

    private static Func<PartView, IReadOnlyList<ElementEdit>> WrapPresent(Func<ISet<string>, IReadOnlyList<ElementEdit>> make)
    {
        ArgumentNullException.ThrowIfNull(make);
        return view => make(view.Present);
    }

    /// <summary>A patch whose edits depend on what the part holds now (its element paths and their text).</summary>
    public PartPatch(Guid partId, Func<PartView, IReadOnlyList<ElementEdit>> make)
    {
        ArgumentNullException.ThrowIfNull(make);
        PartId = partId;
        Edits = Array.Empty<ElementEdit>();
        m_deferred = make;
    }

    /// <summary>The edits to apply to a part that looks like <paramref name="view"/>.</summary>
    public IReadOnlyList<ElementEdit> EditsFor(PartView view)
    {
        if (m_deferred == null)
            return Edits;
        IReadOnlyList<ElementEdit> edits = m_deferred(view);
        if (edits == null || edits.Count == 0)
            throw new InvalidOperationException("the patch for part " + PartId + " produced no edits");
        return edits;
    }
}

/// <summary>
/// A new part added to the group that holds an existing part (the anchor). <see cref="Make"/> gets a reader for the anchor's
/// leaf elements (raw text, so owner and permission values can be copied) and returns the whole SceneObjectPart element.
/// </summary>
public sealed class PartInsertion
{
    public Guid AnchorPartId { get; }
    public Func<Func<string, string>, string> Make { get; }

    public PartInsertion(Guid anchorPartId, Func<Func<string, string>, string> make)
    {
        ArgumentNullException.ThrowIfNull(make);
        AnchorPartId = anchorPartId;
        Make = make;
    }
}

/// <summary>Edits a single SceneObjectPart element held as text, with the same element edits a patch of an archive uses.</summary>
public static class PartXml
{
    /// <summary>Returns <paramref name="partXml"/> with the edits applied; every edit's element must exist in it.</summary>
    public static string Edit(string partXml, Guid partId, IReadOnlyList<ElementEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(partXml);
        ArgumentNullException.ThrowIfNull(edits);
        byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(partXml);
        ObjectXml.Decoded d = ObjectXml.Decode(bytes);
        byte[] result = ObjectXml.Apply(d, new Dictionary<Guid, PartPatch> { [partId] = new PartPatch(partId, edits.ToArray()) }, "the new part");
        return new System.Text.UTF8Encoding(false).GetString(result);
    }
}
