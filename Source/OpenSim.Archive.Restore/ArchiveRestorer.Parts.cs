/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using System.Xml;
using OpenMetaverse;
using OpenSim.Archive.Converter;
using OpenSim.Archive.Inspection;
using OpenSim.Archive.Rewrite;

namespace OpenSim.Archive.Restore;

// Deleted parts (refused, or kept as invisible stubs) and added parts (nodes the original had no part for).
public sealed partial class ArchiveRestorer
{
    private const string BlankTexture = "5748decc-f629-461c-9a36-a35a221fe21f";
    private const string PlywoodTexture = "89556747-24cb-43ed-920b-47caed15465f";
    private const float MinPrimSize = 0.01f, MaxPrimSize = 64f;

    // ------------------------------------------------------------------ a part the .glb no longer has

    private void Delete(Run run, PartReport report, PartNode original)
    {
        if (!m_options.StubDeleted)
        {
            report.Outcome = PartOutcome.Refused;
            report.Code = "PART_DELETED";
            report.Detail = "the part is not in the .glb any more; deleting a part is not restored, so it stays as it was (--stub-deleted keeps a deleted part as an invisible stub with its scripts and contents)";
            return;
        }
        if (!run.Parsed.TryGetValue(original.PartId, out ParsedPart pp) || pp.TextureEntry == null || pp.TextureEntry.Length <= 16)
        {
            report.Outcome = PartOutcome.Refused;
            report.Code = "STUB_UNSUPPORTED";
            report.Detail = "the part has no texture entry to make transparent, so it cannot be kept as an invisible stub";
            return;
        }
        byte[] transparent;
        try
        {
            Primitive.TextureEntry te = new Primitive.TextureEntry(pp.TextureEntry, 0, pp.TextureEntry.Length);
            Clear(te.DefaultTexture);
            foreach (Primitive.TextureEntryFace face in te.FaceTextures)
                if (face != null)
                    Clear(face);
            transparent = te.GetBytes();
        }
        catch (Exception e) when (e is ArgumentException || e is IndexOutOfRangeException || e is InvalidOperationException || e is NullReferenceException)
        {
            report.Outcome = PartOutcome.Refused;
            report.Code = "STUB_UNSUPPORTED";
            report.Detail = "the part's texture entry cannot be read: " + e.Message;
            return;
        }
        bool setShapeType = pp.PhysicsShapeType >= 0 && pp.PhysicsShapeType != PhysShapeNone;
        run.Edit(original.PartId, view =>
        {
            List<ElementEdit> edits = new List<ElementEdit>
            {
                ElementEdit.SetText("Flags", WithPhantom(view.Text("Flags"))),
                ElementEdit.SetText("Shape/TextureEntry", Convert.ToBase64String(transparent))
            };
            if (setShapeType && view.Present.Contains("PhysicsShapeType"))
                edits.Add(ElementEdit.SetText("PhysicsShapeType", PhysShapeNone.ToString(Inv)));
            return edits;
        });
        run.Plan1(original.PartId, "textureEntry");
        if (setShapeType)
            run.Plan1(original.PartId, "physics");
        report.Outcome = PartOutcome.Stubbed;
        report.Detail = "deleted in the .glb; kept as an invisible phantom stub with its name, description, scripts and contents";
    }

    private static void Clear(Primitive.TextureEntryFace face)
    {
        Color4 c = face.RGBA;
        face.RGBA = new Color4(c.R, c.G, c.B, 0f);
    }

    /// <summary>The part's Flags text with Phantom added, in the spelling the region reads (PrimFlags names, comma separated).</summary>
    private static string WithPhantom(string current)
    {
        if (string.IsNullOrWhiteSpace(current))
            return PrimFlags.Phantom.ToString();
        if (Enum.TryParse(current, true, out PrimFlags flags))
            return (flags | PrimFlags.Phantom).ToString();
        return current + ", " + PrimFlags.Phantom;
    }

    // ------------------------------------------------------------------ a node that is no part of the original

    /// <summary>Builds a new part from a node and puts it in the build after the last link number. Returns false, with the report refused, when it cannot.</summary>
    private bool Add(Run run, PartReport report, PartNode node, GlbFile user, NodeFrame frame, Guid rootId, int linkNum, int ordinal)
    {
        void Refuse(string code, string detail)
        {
            report.Outcome = PartOutcome.Refused;
            report.Code = code;
            report.Detail = detail;
        }

        if (!run.Parsed.TryGetValue(rootId, out ParsedPart root))
        {
            Refuse("ADD_NO_ROOT", "the build's root part is not in the original archive's data");
            return false;
        }
        string name = Clean(string.IsNullOrWhiteSpace(node.Node.Name) ? "Added part" : node.Node.Name);
        if (name.Length > 63)
            name = name.Substring(0, 63);
        report.Name = name;
        report.LinkNum = linkNum;

        NodePose pose = frame.Relative(node.Node.Index);
        PartGeometry g = PartGeometry.FromNode(user, node.Node, new[] { pose.Scale.X, pose.Scale.Y, pose.Scale.Z }, true);
        if (g.TriangleCount == 0)
        {
            Refuse("ADD_NO_GEOMETRY", "the node has a mesh with no triangles");
            return false;
        }
        if (g.MixedTriangles > 0)
        {
            Refuse("FACES_JOINED", g.MixedTriangles + " triangles take their corners from more than one prim face");
            return false;
        }
        // an added part is credited to the root part's owner unless the caller named a creator; the root's own creator is not borrowed
        Guid creator = m_options.Creator ?? root.Part.OwnerId.Guid;
        report.Creator = creator;
        int faceCount = g.Faces.Keys.Max() + 1;
        if (g.Faces.Keys.Min() < 0 || faceCount > MeshShape.MaxFaces)
        {
            Refuse("FACES_GT8", "the part would have " + faceCount + " faces (one for each material) and a mesh part has at most " + MeshShape.MaxFaces);
            return false;
        }

        // the size is the box around the triangles, in viewer axes; the mesh is the shape in that box
        float[] min = { float.MaxValue, float.MaxValue, float.MaxValue }, max = { float.MinValue, float.MinValue, float.MinValue };
        foreach (List<float[]> tris in g.Faces.Values)
            foreach (float[] tri in tris)
                for (int c = 0; c < 3; c++)
                {
                    float x = tri[c * 8], y = -tri[c * 8 + 2], z = tri[c * 8 + 1];
                    min[0] = Math.Min(min[0], x); max[0] = Math.Max(max[0], x);
                    min[1] = Math.Min(min[1], y); max[1] = Math.Max(max[1], y);
                    min[2] = Math.Min(min[2], z); max[2] = Math.Max(max[2], z);
                }
        Vector3 size = new Vector3(Math.Max(max[0] - min[0], MinPrimSize), Math.Max(max[1] - min[1], MinPrimSize), Math.Max(max[2] - min[2], MinPrimSize));
        Vector3 center = new Vector3((min[0] + max[0]) / 2f, (min[1] + max[1]) / 2f, (min[2] + max[2]) / 2f);
        if (!float.IsFinite(size.X + size.Y + size.Z + center.X + center.Y + center.Z))
        {
            Refuse("ADD_BAD_GEOMETRY", "the node's vertices are not finite numbers");
            return false;
        }
        if (size.X > MaxPrimSize || size.Y > MaxPrimSize || size.Z > MaxPrimSize)
            report.Notes.Add("the part is larger than " + MaxPrimSize.ToString("0", Inv) + " m in some direction; many regions refuse a prim that size");

        List<EncoderFace> faces;
        try { faces = BuildFaces(g, user, size, center, faceCount, out List<int> sourceSlot); }
        catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is IndexOutOfRangeException)
        {
            Refuse("GEOMETRY_FAILED", e.GetType().Name + ": " + e.Message);
            return false;
        }
        if (faces.Count > MeshShape.MaxFaces)
        {
            Refuse("FACES_GT8", "splitting a face over the vertex limit would give the part " + faces.Count + " faces; a mesh part has at most " + MeshShape.MaxFaces);
            return false;
        }
        faceCount = faces.Count;

        PhysicsGeometry physics = PhysicsFrom(faces, size);
        byte[] bytes;
        try { bytes = new MeshAssetEncoder().Encode(faces, new EncodeOptions { Physics = physics }); }
        catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is OverflowException || e is IndexOutOfRangeException || e is MeshAsset.MeshFormatException)
        {
            Refuse("ENCODE_FAILED", e.GetType().Name + ": " + e.Message);
            return false;
        }
        List<string> problems = MeshValidator.Check(bytes, faces, out _, physics);
        if (problems.Count > 0)
        {
            run.Result.Invalid.Add("added part '" + name + "': " + string.Join("; ", problems));
            Refuse("MESH_INVALID", string.Join("; ", problems));
            return false;
        }
        Guid meshId = MeshId.FromBytes(bytes);
        run.Meshes.TryAdd(meshId, bytes);

        List<string> textureNotes = new List<string>();
        byte[] textureEntry = TextureEntryFor(run, user, g, faceCount, textureNotes, creator, report);

        // where it is: the node's place in the build, with the centre of its box carried through the rotation
        float[] rotation = PoseMath.ViewerQuat(pose.Rotation);
        Quaternion q = new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]);
        Vector3 offset = center * q;
        float[] ground = PoseMath.ViewerVector(pose.Translation);
        float[] position = { ground[0] + offset.X, ground[1] + offset.Y, ground[2] + offset.Z };

        Guid id = MeshId.FromBytes(Encoding.UTF8.GetBytes(rootId.ToString("D") + "|" + ordinal.ToString(Inv) + "|" + name + "|" + meshId.ToString("D")));
        string description = Clean(GlbFile.Str(node.Node.Extras, "description") ?? string.Empty);
        run.Rewriter.AddPart(new PartInsertion(rootId, read =>
        {
            string xml = NewPartXml(read, run, rootId, id, name, description, linkNum, position, rotation, size, textureEntry, creator);
            return PartXml.Edit(xml, id, MeshShape.Edits(faceCount, meshId, null));
        }));
        run.AddedParts.Add(id);

        report.Outcome = PartOutcome.Added;
        report.PartId = id;
        report.Faces = faceCount;
        report.Triangles = faces.Sum(f => f.TriangleCount);
        report.MeshId = meshId;
        report.Detail = "size " + size.X.ToString("0.###", Inv) + " x " + size.Y.ToString("0.###", Inv) + " x " + size.Z.ToString("0.###", Inv) + " m at <" + position[0].ToString("0.###", Inv) + ", " + position[1].ToString("0.###", Inv) + ", " + position[2].ToString("0.###", Inv) + ">"
            + (textureNotes.Count > 0 ? "; " + string.Join("; ", textureNotes) : string.Empty);
        if (physics == null)
            report.Notes.Add("the physics mesh has more vertices than a mesh asset can index; the part collides as its bounding hull");
        return true;
    }

    /// <summary>The texture entry of an added part: face i takes the colour and picture of the material of the primitive that holds its triangles.</summary>
    private byte[] TextureEntryFor(Run run, GlbFile user, PartGeometry g, int faceCount, List<string> notes, Guid creator, PartReport report)
    {
        Primitive.TextureEntry te = new Primitive.TextureEntry(new UUID(PlywoodTexture));
        for (int f = 0; f < faceCount; f++)
        {
            if (!g.Materials.TryGetValue(f, out List<int> materials) || materials.Count == 0 || materials[0] < 0 || materials[0] >= user.Materials.Count)
                continue;
            GlbMaterial m = user.Materials[materials[0]];
            if (m.HasTextureTransform)
                report.Notes.Add("face " + f.ToString(Inv) + ": the material gives its picture a texture transform (offset, scale or rotation); it is not applied to the new part's texture settings");
            Primitive.TextureEntryFace face = te.CreateFace((uint)f);
            float alpha = m.AlphaMode == "OPAQUE" ? 1f : m.BaseColor[3];
            face.RGBA = new Color4(Clamp01(m.BaseColor[0]), Clamp01(m.BaseColor[1]), Clamp01(m.BaseColor[2]), Clamp01(alpha));
            face.TextureID = new UUID(BlankTexture);
            if (m.Image == null)
                continue;
            Guid? asset = run.Textures.Resolve(m.Image, out bool isNew, out string problem);
            if (asset == null)
            {
                notes.Add("face " + f.ToString(Inv) + ": " + problem + "; the face is a plain colour");
                continue;
            }
            face.TextureID = new UUID(asset.Value);
            if (isNew && !run.Result.NewTextures.Any(t => t.Id == asset.Value))
                run.Result.NewTextures.Add((asset.Value, creator));
            notes.Add("face " + f.ToString(Inv) + ": " + (isNew ? "new texture " : "texture ") + asset.Value.ToString("D") + (isNew ? string.Empty : " (the same picture as an existing asset)"));
        }
        return te.GetBytes();
    }

    private static float Clamp01(float v) => float.IsFinite(v) ? Math.Min(1f, Math.Max(0f, v)) : 0f;

    private static string Clean(string s)
    {
        StringBuilder sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (XmlConvert.IsXmlChar(c))
                sb.Append(c);
        return sb.ToString();
    }

    private static string X(string s) => Clean(s).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// A new part, written as this server writes a plain prim: no scripts, contents, particles, sounds or hover text. Its owner,
    /// creator, group and permissions are the root part's (read from the object file), so it belongs to whoever owns the build.
    /// </summary>
    private static string NewPartXml(Func<string, string> read, Run run, Guid rootId, Guid id, string name, string description, int linkNum,
        float[] position, float[] rotation, Vector3 size, byte[] textureEntry, Guid creator)
    {
        string Value(string path, string fallback) => string.IsNullOrEmpty(read(path)) ? fallback : read(path);
        string zero = Guid.Empty.ToString("D");
        string owner = Value("OwnerID/UUID", zero);
        string group = Value("GroupID/UUID", zero);
        string lastOwner = Value("LastOwnerID/UUID", owner);
        string rezzer = Value("RezzerID/UUID", owner);
        float[] groupAt = run.GroupPlace.TryGetValue(rootId, out var moved) ? moved.position
            : new[] { Parse(read("GroupPosition/X")), Parse(read("GroupPosition/Y")), Parse(read("GroupPosition/Z")) };

        StringBuilder sb = new StringBuilder();
        sb.Append("<SceneObjectPart xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">");
        sb.Append("<AllowedDrop>false</AllowedDrop><CreatorID><UUID>").Append(creator.ToString("D")).Append("</UUID></CreatorID>");
        sb.Append("<FolderID><UUID>").Append(id.ToString("D")).Append("</UUID></FolderID><InventorySerial>0</InventorySerial>");
        sb.Append("<UUID><UUID>").Append(id.ToString("D")).Append("</UUID></UUID><LocalId>0</LocalId>");
        sb.Append("<Name>").Append(X(name)).Append("</Name><Material>").Append(Value("Material", "3")).Append("</Material>");
        sb.Append("<PassTouches>false</PassTouches><PassCollisions>false</PassCollisions><RegionHandle>0</RegionHandle><ScriptAccessPin>0</ScriptAccessPin>");
        sb.Append("<GroupPosition>").Append(Vec(groupAt)).Append("</GroupPosition>");
        sb.Append("<OffsetPosition>").Append(Vec(position)).Append("</OffsetPosition>");
        sb.Append("<RotationOffset><X>").Append(Text(rotation[0])).Append("</X><Y>").Append(Text(rotation[1])).Append("</Y><Z>").Append(Text(rotation[2])).Append("</Z><W>").Append(Text(rotation[3])).Append("</W></RotationOffset>");
        sb.Append("<Velocity><X>0</X><Y>0</Y><Z>0</Z></Velocity><AngularVelocity><X>0</X><Y>0</Y><Z>0</Z></AngularVelocity><Acceleration><X>0</X><Y>0</Y><Z>0</Z></Acceleration>");
        sb.Append("<Description>").Append(X(description)).Append("</Description><Color><R>0</R><G>0</G><B>0</B><A>255</A></Color><Text /><SitName /><TouchName />");
        sb.Append("<LinkNum>").Append(linkNum.ToString(Inv)).Append("</LinkNum><ClickAction>0</ClickAction>");
        sb.Append("<Shape><ProfileCurve>1</ProfileCurve><TextureEntry>").Append(Convert.ToBase64String(textureEntry)).Append("</TextureEntry><ExtraParams>AA==</ExtraParams>");
        sb.Append("<PathBegin>0</PathBegin><PathCurve>16</PathCurve><PathEnd>0</PathEnd><PathRadiusOffset>0</PathRadiusOffset><PathRevolutions>0</PathRevolutions>");
        sb.Append("<PathScaleX>100</PathScaleX><PathScaleY>100</PathScaleY><PathShearX>0</PathShearX><PathShearY>0</PathShearY><PathSkew>0</PathSkew>");
        sb.Append("<PathTaperX>0</PathTaperX><PathTaperY>0</PathTaperY><PathTwist>0</PathTwist><PathTwistBegin>0</PathTwistBegin><PCode>9</PCode>");
        sb.Append("<ProfileBegin>0</ProfileBegin><ProfileEnd>0</ProfileEnd><ProfileHollow>0</ProfileHollow><State>0</State><LastAttachPoint>0</LastAttachPoint>");
        sb.Append("<ProfileShape>Square</ProfileShape><HollowShape>Same</HollowShape><SculptTexture><UUID>").Append(zero).Append("</UUID></SculptTexture><SculptType>0</SculptType>");
        sb.Append("<FlexiSoftness>0</FlexiSoftness><FlexiTension>0</FlexiTension><FlexiDrag>0</FlexiDrag><FlexiGravity>0</FlexiGravity><FlexiWind>0</FlexiWind>");
        sb.Append("<FlexiForceX>0</FlexiForceX><FlexiForceY>0</FlexiForceY><FlexiForceZ>0</FlexiForceZ><LightColorR>0</LightColorR><LightColorG>0</LightColorG><LightColorB>0</LightColorB>");
        sb.Append("<LightColorA>1</LightColorA><LightRadius>0</LightRadius><LightCutoff>0</LightCutoff><LightFalloff>0</LightFalloff><LightIntensity>1</LightIntensity>");
        sb.Append("<FlexiEntry>false</FlexiEntry><LightEntry>false</LightEntry><SculptEntry>false</SculptEntry></Shape>");
        sb.Append("<Scale>").Append(Vec(new[] { size.X, size.Y, size.Z })).Append("</Scale>");
        sb.Append("<SitTargetOrientation><X>0</X><Y>0</Y><Z>0</Z><W>1</W></SitTargetOrientation><SitTargetPosition><X>0</X><Y>0</Y><Z>0</Z></SitTargetPosition>");
        sb.Append("<SitTargetPositionLL><X>0</X><Y>0</Y><Z>0</Z></SitTargetPositionLL><SitTargetOrientationLL><X>0</X><Y>0</Y><Z>0</Z><W>1</W></SitTargetOrientationLL>");
        sb.Append("<ParentID>0</ParentID><CreationDate>").Append(Value("CreationDate", "0")).Append("</CreationDate><Category>0</Category><SalePrice>0</SalePrice>");
        sb.Append("<ObjectSaleType>0</ObjectSaleType><OwnershipCost>0</OwnershipCost><GroupID><UUID>").Append(group).Append("</UUID></GroupID>");
        sb.Append("<OwnerID><UUID>").Append(owner).Append("</UUID></OwnerID><LastOwnerID><UUID>").Append(lastOwner).Append("</UUID></LastOwnerID><RezzerID><UUID>").Append(rezzer).Append("</UUID></RezzerID>");
        sb.Append("<BaseMask>").Append(Value("BaseMask", "0")).Append("</BaseMask><OwnerMask>").Append(Value("OwnerMask", "0")).Append("</OwnerMask><GroupMask>").Append(Value("GroupMask", "0"))
          .Append("</GroupMask><EveryoneMask>").Append(Value("EveryoneMask", "0")).Append("</EveryoneMask><NextOwnerMask>").Append(Value("NextOwnerMask", "0")).Append("</NextOwnerMask>");
        sb.Append("<Flags>None</Flags><CollisionSound><UUID>").Append(zero).Append("</UUID></CollisionSound><CollisionSoundVolume>0</CollisionSoundVolume>");
        sb.Append("<AttachedPos><X>0</X><Y>0</Y><Z>0</Z></AttachedPos><TextureAnimation></TextureAnimation><ParticleSystem></ParticleSystem>");
        sb.Append("<PayPrice0>-2</PayPrice0><PayPrice1>-2</PayPrice1><PayPrice2>-2</PayPrice2><PayPrice3>-2</PayPrice3><PayPrice4>-2</PayPrice4><Buoyancy>0</Buoyancy>");
        sb.Append("<Force><X>0</X><Y>0</Y><Z>0</Z></Force><Torque><X>0</X><Y>0</Y><Z>0</Z></Torque><VolumeDetectActive>false</VolumeDetectActive><PhysicsShapeType>0</PhysicsShapeType>");
        sb.Append("<CameraEyeOffset><X>0</X><Y>0</Y><Z>0</Z></CameraEyeOffset><CameraAtOffset><X>0</X><Y>0</Y><Z>0</Z></CameraAtOffset>");
        sb.Append("<SoundID><UUID>").Append(zero).Append("</UUID></SoundID><SoundGain>0</SoundGain><SoundFlags>0</SoundFlags><SoundRadius>0</SoundRadius><SoundQueueing>false</SoundQueueing>");
        sb.Append("<AllowUnsit>true</AllowUnsit><ScriptedSitOnly>false</ScriptedSitOnly></SceneObjectPart>");
        return sb.ToString();
    }

    private static float Parse(string text) => float.TryParse(text, System.Globalization.NumberStyles.Float, Inv, out float v) ? v : 0f;

    private static string Vec(float[] v) => "<X>" + Text(v[0]) + "</X><Y>" + Text(v[1]) + "</Y><Z>" + Text(v[2]) + "</Z>";
}
