/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace SLua.ScriptEngine
{
    /// <summary>
    /// One os, iw or bot function as a script calls it: a name in one of the tables ossl, iw and bot, with one or more
    /// signatures (the overloads of Phlox's compiler table). Every signature is a <see cref="BoundFunction"/> that calls the
    /// ILSLSystemAPI method of the same name and types.
    /// </summary>
    internal sealed class OsFunction
    {
        public OsFunction(string table, string name, string apiName, BoundFunction[] overloads)
        {
            Table = table;
            Name = name;
            ApiName = apiName;
            Overloads = overloads;
        }

        /// <summary>The script's table: "ossl" for os functions, "iw" and "bot".</summary>
        public string Table { get; }

        /// <summary>The name in that table, without the prefix: "SHA256" for osSHA256.</summary>
        public string Name { get; }

        /// <summary>The name an LSL script and the API use: "osSHA256".</summary>
        public string ApiName { get; }

        /// <summary>The signatures, in the order of Phlox's compiler table.</summary>
        public BoundFunction[] Overloads { get; }
    }
}
