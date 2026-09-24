/*
 * Legion Grid — Phlox Script Engine Integration
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;

namespace Phlox.ScriptEngine
{
    internal class PhloxLoadRequest
    {
        public uint LocalID;
        public UUID ItemID;
        public string ScriptText;
        public int StartParam;
        public bool PostOnRez;
        public int StateSource;
        public SceneObjectPart Prim;
        /// <summary>PHLOX-22 B: the item's load generation when this request was processed; a compile that finishes
        /// after a newer load or an unload of the same item is stale and does not start.</summary>
        public long Generation;
        /// <summary>PHLOX-22 C: posting order of this item's loads, so GetScriptErrors waits for the one just saved.</summary>
        public long Serial;
    }

    internal class PhloxUnloadRequest
    {
        public uint LocalID;
        public UUID ItemID;
    }
}
