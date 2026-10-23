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

/// <summary>The edits for one part, found by the part's UUID wherever it sits in the archive's object files.</summary>
public sealed class PartPatch
{
    private readonly Func<ISet<string>, IReadOnlyList<ElementEdit>> m_deferred;

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
    {
        ArgumentNullException.ThrowIfNull(make);
        PartId = partId;
        Edits = Array.Empty<ElementEdit>();
        m_deferred = make;
    }

    /// <summary>The edits to apply to a part that has the given element paths.</summary>
    public IReadOnlyList<ElementEdit> EditsFor(ISet<string> present)
    {
        if (m_deferred == null)
            return Edits;
        IReadOnlyList<ElementEdit> edits = m_deferred(present);
        if (edits == null || edits.Count == 0)
            throw new InvalidOperationException("the patch for part " + PartId + " produced no edits");
        return edits;
    }
}
