/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.Restore.Import;

/// <summary>
/// The joint names a rigged mesh may name: the skeleton's bones ("mPelvis" and the like), its collision volumes ("PELVIS", "L_UPPER_LEG"),
/// the attachment points, and the aliases the viewer accepts for a bone. The names and aliases are the ones in the viewer's skeleton
/// definition (character/avatar_skeleton.xml, character/avatar_lad.xml; the Second Life wiki lists them under Avatar skeleton). Only the
/// names are listed here, no positions or other data from those files.
/// </summary>
public static class ViewerJointNames
{
    /// <summary>The bones, each a deform bone with an "m" prefix. A bone's rest rotation is none and its scale is 1.</summary>
    public static readonly IReadOnlySet<string> Bones = new HashSet<string>(StringComparer.Ordinal)
    {
        "mPelvis", "mSpine1", "mSpine2", "mTorso", "mSpine3", "mSpine4", "mChest", "mNeck", "mHead", "mSkull", "mEyeRight", "mEyeLeft", "mFaceRoot",
        "mFaceEyeAltRight", "mFaceEyeAltLeft", "mFaceForeheadLeft", "mFaceForeheadRight", "mFaceEyebrowOuterLeft", "mFaceEyebrowCenterLeft",
        "mFaceEyebrowInnerLeft", "mFaceEyebrowOuterRight", "mFaceEyebrowCenterRight", "mFaceEyebrowInnerRight", "mFaceEyeLidUpperLeft",
        "mFaceEyeLidLowerLeft", "mFaceEyeLidUpperRight", "mFaceEyeLidLowerRight", "mFaceEar1Left", "mFaceEar2Left", "mFaceEar1Right",
        "mFaceEar2Right", "mFaceNoseLeft", "mFaceNoseCenter", "mFaceNoseRight", "mFaceCheekLowerLeft", "mFaceCheekUpperLeft", "mFaceCheekLowerRight",
        "mFaceCheekUpperRight", "mFaceJaw", "mFaceChin", "mFaceTeethLower", "mFaceLipLowerLeft", "mFaceLipLowerRight", "mFaceLipLowerCenter",
        "mFaceTongueBase", "mFaceTongueTip", "mFaceJawShaper", "mFaceForeheadCenter", "mFaceNoseBase", "mFaceTeethUpper", "mFaceLipUpperLeft",
        "mFaceLipUpperRight", "mFaceLipCornerLeft", "mFaceLipCornerRight", "mFaceLipUpperCenter", "mFaceEyecornerInnerLeft",
        "mFaceEyecornerInnerRight", "mFaceNoseBridge", "mCollarLeft", "mShoulderLeft", "mElbowLeft", "mWristLeft", "mHandMiddle1Left",
        "mHandMiddle2Left", "mHandMiddle3Left", "mHandIndex1Left", "mHandIndex2Left", "mHandIndex3Left", "mHandRing1Left", "mHandRing2Left",
        "mHandRing3Left", "mHandPinky1Left", "mHandPinky2Left", "mHandPinky3Left", "mHandThumb1Left", "mHandThumb2Left", "mHandThumb3Left",
        "mCollarRight", "mShoulderRight", "mElbowRight", "mWristRight", "mHandMiddle1Right", "mHandMiddle2Right", "mHandMiddle3Right",
        "mHandIndex1Right", "mHandIndex2Right", "mHandIndex3Right", "mHandRing1Right", "mHandRing2Right", "mHandRing3Right", "mHandPinky1Right",
        "mHandPinky2Right", "mHandPinky3Right", "mHandThumb1Right", "mHandThumb2Right", "mHandThumb3Right", "mWingsRoot", "mWing1Left", "mWing2Left",
        "mWing3Left", "mWing4Left", "mWing4FanLeft", "mWing1Right", "mWing2Right", "mWing3Right", "mWing4Right", "mWing4FanRight", "mHipRight",
        "mKneeRight", "mAnkleRight", "mFootRight", "mToeRight", "mHipLeft", "mKneeLeft", "mAnkleLeft", "mFootLeft", "mToeLeft", "mTail1", "mTail2",
        "mTail3", "mTail4", "mTail5", "mTail6", "mGroin", "mHindLimbsRoot", "mHindLimb1Left", "mHindLimb2Left", "mHindLimb3Left", "mHindLimb4Left",
        "mHindLimb1Right", "mHindLimb2Right", "mHindLimb3Right", "mHindLimb4Right",
    };

    /// <summary>The collision volumes; the viewer takes a mesh weighted to one as a fitted-mesh joint.</summary>
    public static readonly IReadOnlySet<string> CollisionVolumes = new HashSet<string>(StringComparer.Ordinal)
    {
        "PELVIS", "BUTT", "BELLY", "LEFT_HANDLE", "RIGHT_HANDLE", "LOWER_BACK", "CHEST", "LEFT_PEC", "RIGHT_PEC", "UPPER_BACK", "NECK", "HEAD",
        "L_CLAVICLE", "L_UPPER_ARM", "L_LOWER_ARM", "L_HAND", "R_CLAVICLE", "R_UPPER_ARM", "R_LOWER_ARM", "R_HAND", "R_UPPER_LEG", "R_LOWER_LEG",
        "R_FOOT", "L_UPPER_LEG", "L_LOWER_LEG", "L_FOOT",
    };

    /// <summary>The attachment points; a rigged mesh may name one as a joint.</summary>
    public static readonly IReadOnlySet<string> AttachmentPoints = new HashSet<string>(StringComparer.Ordinal)
    {
        "Chest", "Skull", "Left Shoulder", "Right Shoulder", "Left Hand", "Right Hand", "Left Foot", "Right Foot", "Spine", "Pelvis", "Mouth",
        "Chin", "Left Ear", "Right Ear", "Left Eyeball", "Right Eyeball", "Nose", "R Upper Arm", "R Forearm", "L Upper Arm", "L Forearm",
        "Right Hip", "R Upper Leg", "R Lower Leg", "Left Hip", "L Upper Leg", "L Lower Leg", "Stomach", "Left Pec", "Right Pec", "Center 2",
        "Top Right", "Top", "Top Left", "Center", "Bottom Left", "Bottom", "Bottom Right", "Neck", "Avatar Center", "Left Ring Finger",
        "Right Ring Finger", "Tail Base", "Tail Tip", "Left Wing", "Right Wing", "Jaw", "Alt Left Ear", "Alt Right Ear", "Alt Left Eye",
        "Alt Right Eye", "Tongue", "Groin", "Left Hind Foot", "Right Hind Foot",
    };

    /// <summary>Alias to bone name, for the spellings the viewer accepts in a model file.</summary>
    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
            ["abdomen"] = "mTorso",
            ["avatar_mAnkleLeft"] = "mAnkleLeft",
            ["avatar_mAnkleRight"] = "mAnkleRight",
            ["avatar_mChest"] = "mChest",
            ["avatar_mCollarLeft"] = "mCollarLeft",
            ["avatar_mCollarRight"] = "mCollarRight",
            ["avatar_mElbowLeft"] = "mElbowLeft",
            ["avatar_mElbowRight"] = "mElbowRight",
            ["avatar_mEyeLeft"] = "mEyeLeft",
            ["avatar_mEyeRight"] = "mEyeRight",
            ["avatar_mFaceCheekLowerLeft"] = "mFaceCheekLowerLeft",
            ["avatar_mFaceCheekLowerRight"] = "mFaceCheekLowerRight",
            ["avatar_mFaceCheekUpperLeft"] = "mFaceCheekUpperLeft",
            ["avatar_mFaceCheekUpperRight"] = "mFaceCheekUpperRight",
            ["avatar_mFaceChin"] = "mFaceChin",
            ["avatar_mFaceEar1Left"] = "mFaceEar1Left",
            ["avatar_mFaceEar1Right"] = "mFaceEar1Right",
            ["avatar_mFaceEar2Left"] = "mFaceEar2Left",
            ["avatar_mFaceEar2Right"] = "mFaceEar2Right",
            ["avatar_mFaceEyeAltLeft"] = "mFaceEyeAltLeft",
            ["avatar_mFaceEyeAltRight"] = "mFaceEyeAltRight",
            ["avatar_mFaceEyeLidLowerLeft"] = "mFaceEyeLidLowerLeft",
            ["avatar_mFaceEyeLidLowerRight"] = "mFaceEyeLidLowerRight",
            ["avatar_mFaceEyeLidUpperLeft"] = "mFaceEyeLidUpperLeft",
            ["avatar_mFaceEyeLidUpperRight"] = "mFaceEyeLidUpperRight",
            ["avatar_mFaceEyebrowCenterLeft"] = "mFaceEyebrowCenterLeft",
            ["avatar_mFaceEyebrowCenterRight"] = "mFaceEyebrowCenterRight",
            ["avatar_mFaceEyebrowInnerLeft"] = "mFaceEyebrowInnerLeft",
            ["avatar_mFaceEyebrowInnerRight"] = "mFaceEyebrowInnerRight",
            ["avatar_mFaceEyebrowOuterLeft"] = "mFaceEyebrowOuterLeft",
            ["avatar_mFaceEyebrowOuterRight"] = "mFaceEyebrowOuterRight",
            ["avatar_mFaceEyecornerInnerLeft"] = "mFaceEyecornerInnerLeft",
            ["avatar_mFaceEyecornerInnerRight"] = "mFaceEyecornerInnerRight",
            ["avatar_mFaceForeheadCenter"] = "mFaceForeheadCenter",
            ["avatar_mFaceForeheadLeft"] = "mFaceForeheadLeft",
            ["avatar_mFaceForeheadRight"] = "mFaceForeheadRight",
            ["avatar_mFaceJaw"] = "mFaceJaw",
            ["avatar_mFaceJawShaper"] = "mFaceJawShaper",
            ["avatar_mFaceLipCornerLeft"] = "mFaceLipCornerLeft",
            ["avatar_mFaceLipCornerRight"] = "mFaceLipCornerRight",
            ["avatar_mFaceLipLowerCenter"] = "mFaceLipLowerCenter",
            ["avatar_mFaceLipLowerLeft"] = "mFaceLipLowerLeft",
            ["avatar_mFaceLipLowerRight"] = "mFaceLipLowerRight",
            ["avatar_mFaceLipUpperCenter"] = "mFaceLipUpperCenter",
            ["avatar_mFaceLipUpperLeft"] = "mFaceLipUpperLeft",
            ["avatar_mFaceLipUpperRight"] = "mFaceLipUpperRight",
            ["avatar_mFaceNoseBase"] = "mFaceNoseBase",
            ["avatar_mFaceNoseBridge"] = "mFaceNoseBridge",
            ["avatar_mFaceNoseCenter"] = "mFaceNoseCenter",
            ["avatar_mFaceNoseLeft"] = "mFaceNoseLeft",
            ["avatar_mFaceNoseRight"] = "mFaceNoseRight",
            ["avatar_mFaceRoot"] = "mFaceRoot",
            ["avatar_mFaceTeethLower"] = "mFaceTeethLower",
            ["avatar_mFaceTeethUpper"] = "mFaceTeethUpper",
            ["avatar_mFaceTongueBase"] = "mFaceTongueBase",
            ["avatar_mFaceTongueTip"] = "mFaceTongueTip",
            ["avatar_mFootLeft"] = "mFootLeft",
            ["avatar_mFootRight"] = "mFootRight",
            ["avatar_mGroin"] = "mGroin",
            ["avatar_mHandIndex1Left"] = "mHandIndex1Left",
            ["avatar_mHandIndex1Right"] = "mHandIndex1Right",
            ["avatar_mHandIndex2Left"] = "mHandIndex2Left",
            ["avatar_mHandIndex2Right"] = "mHandIndex2Right",
            ["avatar_mHandIndex3Left"] = "mHandIndex3Left",
            ["avatar_mHandIndex3Right"] = "mHandIndex3Right",
            ["avatar_mHandMiddle1Left"] = "mHandMiddle1Left",
            ["avatar_mHandMiddle1Right"] = "mHandMiddle1Right",
            ["avatar_mHandMiddle2Left"] = "mHandMiddle2Left",
            ["avatar_mHandMiddle2Right"] = "mHandMiddle2Right",
            ["avatar_mHandMiddle3Left"] = "mHandMiddle3Left",
            ["avatar_mHandMiddle3Right"] = "mHandMiddle3Right",
            ["avatar_mHandPinky1Left"] = "mHandPinky1Left",
            ["avatar_mHandPinky1Right"] = "mHandPinky1Right",
            ["avatar_mHandPinky2Left"] = "mHandPinky2Left",
            ["avatar_mHandPinky2Right"] = "mHandPinky2Right",
            ["avatar_mHandPinky3Left"] = "mHandPinky3Left",
            ["avatar_mHandPinky3Right"] = "mHandPinky3Right",
            ["avatar_mHandRing1Left"] = "mHandRing1Left",
            ["avatar_mHandRing1Right"] = "mHandRing1Right",
            ["avatar_mHandRing2Left"] = "mHandRing2Left",
            ["avatar_mHandRing2Right"] = "mHandRing2Right",
            ["avatar_mHandRing3Left"] = "mHandRing3Left",
            ["avatar_mHandRing3Right"] = "mHandRing3Right",
            ["avatar_mHandThumb1Left"] = "mHandThumb1Left",
            ["avatar_mHandThumb1Right"] = "mHandThumb1Right",
            ["avatar_mHandThumb2Left"] = "mHandThumb2Left",
            ["avatar_mHandThumb2Right"] = "mHandThumb2Right",
            ["avatar_mHandThumb3Left"] = "mHandThumb3Left",
            ["avatar_mHandThumb3Right"] = "mHandThumb3Right",
            ["avatar_mHead"] = "mHead",
            ["avatar_mHindLimb1Left"] = "mHindLimb1Left",
            ["avatar_mHindLimb1Right"] = "mHindLimb1Right",
            ["avatar_mHindLimb2Left"] = "mHindLimb2Left",
            ["avatar_mHindLimb2Right"] = "mHindLimb2Right",
            ["avatar_mHindLimb3Left"] = "mHindLimb3Left",
            ["avatar_mHindLimb3Right"] = "mHindLimb3Right",
            ["avatar_mHindLimb4Left"] = "mHindLimb4Left",
            ["avatar_mHindLimb4Right"] = "mHindLimb4Right",
            ["avatar_mHindLimbsRoot"] = "mHindLimbsRoot",
            ["avatar_mHipLeft"] = "mHipLeft",
            ["avatar_mHipRight"] = "mHipRight",
            ["avatar_mKneeLeft"] = "mKneeLeft",
            ["avatar_mKneeRight"] = "mKneeRight",
            ["avatar_mNeck"] = "mNeck",
            ["avatar_mPelvis"] = "mPelvis",
            ["avatar_mShoulderLeft"] = "mShoulderLeft",
            ["avatar_mShoulderRight"] = "mShoulderRight",
            ["avatar_mSkull"] = "mSkull",
            ["avatar_mSpine1"] = "mSpine1",
            ["avatar_mSpine2"] = "mSpine2",
            ["avatar_mSpine3"] = "mSpine3",
            ["avatar_mSpine4"] = "mSpine4",
            ["avatar_mTail1"] = "mTail1",
            ["avatar_mTail2"] = "mTail2",
            ["avatar_mTail3"] = "mTail3",
            ["avatar_mTail4"] = "mTail4",
            ["avatar_mTail5"] = "mTail5",
            ["avatar_mTail6"] = "mTail6",
            ["avatar_mToeLeft"] = "mToeLeft",
            ["avatar_mToeRight"] = "mToeRight",
            ["avatar_mTorso"] = "mTorso",
            ["avatar_mWing1Left"] = "mWing1Left",
            ["avatar_mWing1Right"] = "mWing1Right",
            ["avatar_mWing2Left"] = "mWing2Left",
            ["avatar_mWing2Right"] = "mWing2Right",
            ["avatar_mWing3Left"] = "mWing3Left",
            ["avatar_mWing3Right"] = "mWing3Right",
            ["avatar_mWing4FanLeft"] = "mWing4FanLeft",
            ["avatar_mWing4FanRight"] = "mWing4FanRight",
            ["avatar_mWing4Left"] = "mWing4Left",
            ["avatar_mWing4Right"] = "mWing4Right",
            ["avatar_mWingsRoot"] = "mWingsRoot",
            ["avatar_mWristLeft"] = "mWristLeft",
            ["avatar_mWristRight"] = "mWristRight",
            ["chest"] = "mChest",
            ["figureHair"] = "mSkull",
            ["head"] = "mHead",
            ["hip"] = "mPelvis",
            ["lCollar"] = "mCollarLeft",
            ["lFoot"] = "mAnkleLeft",
            ["lForeArm"] = "mElbowLeft",
            ["lHand"] = "mWristLeft",
            ["lShin"] = "mKneeLeft",
            ["lShldr"] = "mShoulderLeft",
            ["lThigh"] = "mHipLeft",
            ["neck"] = "mNeck",
            ["rCollar"] = "mCollarRight",
            ["rFoot"] = "mAnkleRight",
            ["rForeArm"] = "mElbowRight",
            ["rHand"] = "mWristRight",
            ["rShin"] = "mKneeRight",
            ["rShldr"] = "mShoulderRight",
            ["rThigh"] = "mHipRight",
    };

    /// <summary>The name the viewer knows a joint by, or null when the name is none of its joints.</summary>
    public static string Canonical(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        if (Bones.Contains(name) || CollisionVolumes.Contains(name) || AttachmentPoints.Contains(name))
            return name;
        string underscored = name.Replace('_', ' ');
        if (AttachmentPoints.Contains(underscored))
            return underscored;
        return Aliases.TryGetValue(name, out string bone) ? bone : null;
    }
}
