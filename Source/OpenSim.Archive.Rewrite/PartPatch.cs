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
    public Guid PartId { get; }
    public IReadOnlyList<ElementEdit> Edits { get; }

    public PartPatch(Guid partId, params ElementEdit[] edits)
    {
        if (edits == null || edits.Length == 0)
            throw new ArgumentException("a patch needs at least one edit", nameof(edits));
        PartId = partId;
        Edits = edits;
    }
}
