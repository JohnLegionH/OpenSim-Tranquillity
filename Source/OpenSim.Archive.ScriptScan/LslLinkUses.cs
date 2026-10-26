/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.ScriptScan;

/// <summary>How a script reaches, or depends on, the other parts of its linkset.</summary>
public enum LinkUseKind
{
    /// <summary>The part the script is in.</summary>
    Self,
    /// <summary>LINK_ROOT (or 0 or 1): the root part.</summary>
    Root,
    /// <summary>A literal link number of 2 or more.</summary>
    Literal,
    /// <summary>LINK_SET</summary>
    Set,
    /// <summary>LINK_ALL_OTHERS</summary>
    AllOthers,
    /// <summary>LINK_ALL_CHILDREN</summary>
    AllChildren,
    /// <summary>A link argument the scan cannot work out (a variable, a computed value).</summary>
    Unknown,
    /// <summary>The script asks how many parts there are, which part it is, or which part was touched: it depends on the numbering.</summary>
    Enumeration,
    /// <summary>A link_message handler that reads the sender's link number.</summary>
    LinkMessageSender
}

/// <summary>One place a script addresses linkset parts.</summary>
public sealed class LinkUse
{
    public LinkUseKind Kind { get; set; }
    /// <summary>The link number for <see cref="LinkUseKind.Literal"/>.</summary>
    public int Number { get; set; }
    public string Function { get; set; }
    public int Line { get; set; }
    /// <summary>True for llMessageLinked: the message goes to the scripts in the target parts wherever they sit, so LINK_SET and the like address no part by number.</summary>
    public bool MessageOnly { get; set; }
}

/// <summary>Reads every place a script addresses other parts of its linkset by link number, or depends on the link numbering.</summary>
public static class LslLinkUses
{
    public static List<LinkUse> Find(string source)
    {
        throw new NotImplementedException();
    }
}
