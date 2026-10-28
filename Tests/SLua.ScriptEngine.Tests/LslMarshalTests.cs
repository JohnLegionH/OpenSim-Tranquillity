/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using InWorldz.Phlox.Types;
using OpenMetaverse;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>Every LSL type crossing the native boundary both ways, with the edge values.</summary>
public class LslMarshalTests
{
    private static Value[] One(Value v) => new[] { v };

    // ---- integer ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void IntegersRoundTripIncludingTheEdges(int n)
    {
        Value v = LslMarshal.Of(n);
        Assert.Equal(Bindings.ValueType.Int, v.Type);
        Assert.Equal(n, LslMarshal.Int(One(v), 0, "Abs"));
    }

    [Fact]
    public void AnIntegerArgumentWrapsLikeAThirtyTwoBitInteger()
    {
        Assert.Equal(5, LslMarshal.Int(One(Value.Int((1L << 32) + 5)), 0, "Abs"));
        Assert.Equal(int.MinValue, LslMarshal.Int(One(Value.Int(1L << 31)), 0, "Abs"));
    }

    [Fact]
    public void ABooleanIsAnIntegerArgument()
    {
        Assert.Equal(1, LslMarshal.Int(One(Value.Bool(true)), 0, "Abs"));
        Assert.Equal(0, LslMarshal.Int(One(Value.Bool(false)), 0, "Abs"));
    }

    [Fact]
    public void ANumberGivenForAnIntegerIsTruncatedAndSaturatesAtTheEnds()
    {
        Assert.Equal(3, LslMarshal.Int(One(Value.Num(3.9)), 0, "Abs"));
        Assert.Equal(-3, LslMarshal.Int(One(Value.Num(-3.9)), 0, "Abs"));
        Assert.Equal(int.MaxValue, LslMarshal.Int(One(Value.Num(1e20)), 0, "Abs"));
        Assert.Equal(int.MinValue, LslMarshal.Int(One(Value.Num(double.NegativeInfinity)), 0, "Abs"));
        Assert.Equal(0, LslMarshal.Int(One(Value.Num(double.NaN)), 0, "Abs"));
    }

    // ---- float ----

    [Fact]
    public void FloatsRoundTripIncludingNaNInfinityAndNegativeZero()
    {
        foreach (float f in new[] { 0f, 1.5f, -2.25f, float.MaxValue, float.Epsilon, float.PositiveInfinity, float.NegativeInfinity })
        {
            Value v = LslMarshal.Of(f);
            Assert.Equal(Bindings.ValueType.Number, v.Type);
            Assert.Equal(f, LslMarshal.Float(One(v), 0, "Sin"));
        }
        Assert.True(float.IsNaN(LslMarshal.Float(One(LslMarshal.Of(float.NaN)), 0, "Sin")));
        Assert.True(float.IsNegative(LslMarshal.Float(One(LslMarshal.Of(-0f)), 0, "Sin")));
    }

    [Fact]
    public void AFloatArgumentTakesAnIntegerValueToo()
    {
        Assert.Equal(3f, LslMarshal.Float(One(Value.Int(3)), 0, "Sin"));
    }

    [Fact]
    public void ANumberBeyondTheRangeOfAFloatBecomesInfinity()
    {
        Assert.Equal(float.PositiveInfinity, LslMarshal.Float(One(Value.Num(1e300)), 0, "Sin"));
        Assert.Equal(float.NegativeInfinity, LslMarshal.Float(One(Value.Num(-1e300)), 0, "Sin"));
    }

    // ---- string and key ----

    [Fact]
    public void StringsRoundTripEmptyUnicodeAndLargeOnes()
    {
        foreach (string s in new[] { "", "a", "héllo ✓ \U0001F600", "line\nbreak\ttab", new string('x', 1_000_000) })
        {
            Value v = LslMarshal.OfString(s);
            Assert.Equal(Bindings.ValueType.String, v.Type);
            Assert.Equal(s, LslMarshal.String(One(v), 0, "Say"));
        }
    }

    [Fact]
    public void ANullStringIsTheEmptyStringBothWays()
    {
        Assert.Equal("", LslMarshal.OfString(null).S);
        Assert.Equal("", LslMarshal.String(One(new Value(Bindings.ValueType.String, 0, 0, null)), 0, "Say"));
    }

    [Fact]
    public void AStringParameterTakesAKeyValueAndAKeyParameterTakesAStringValue()
    {
        Assert.Equal("abc", LslMarshal.String(One(Value.Key("abc")), 0, "Say"));
        Assert.Equal("abc", LslMarshal.Key(One(Value.Str("abc")), 0, "GiveInventory"));
    }

    [Fact]
    public void KeysRoundTripValidAndInvalidOnesUnchanged()
    {
        foreach (string k in new[] { "6f1b6c0a-1d3e-4f4a-9a43-0c6a4a8e9d11", "00000000-0000-0000-0000-000000000000", "not a key", "" })
        {
            Value v = LslMarshal.OfKey(k);
            Assert.Equal(Bindings.ValueType.Key, v.Type);
            Assert.Equal(k, LslMarshal.Key(One(v), 0, "GiveInventory"));
        }
        Assert.Equal("", LslMarshal.OfKey(null).S);
    }

    // ---- vector and rotation ----

    [Fact]
    public void VectorsRoundTripWithNaNAndInfinityComponents()
    {
        var vec = new Vector3(1.5f, float.NaN, float.NegativeInfinity);
        Value v = LslMarshal.Of(vec);
        Assert.Equal(Bindings.ValueType.Vector, v.Type);
        Vector3 back = LslMarshal.Vector(One(v), 0, "VecMag");
        Assert.Equal(1.5f, back.X);
        Assert.True(float.IsNaN(back.Y));
        Assert.Equal(float.NegativeInfinity, back.Z);
    }

    [Fact]
    public void RotationsRoundTripWithAllFourComponents()
    {
        var rot = new Quaternion(0.1f, 0.2f, 0.3f, 0.4f);
        Value v = LslMarshal.Of(rot);
        Assert.Equal(Bindings.ValueType.Rotation, v.Type);
        Quaternion back = LslMarshal.Rotation(One(v), 0, "Rot2Euler");
        Assert.Equal((0.1f, 0.2f, 0.3f, 0.4f), (back.X, back.Y, back.Z, back.W));
    }

    // ---- list ----

    [Fact]
    public void AnEmptyListRoundTrips()
    {
        Value v = LslMarshal.Of(new LSLList());
        Assert.Equal(Bindings.ValueType.List, v.Type);
        Assert.Empty(v.Items);
        LSLList back = LslMarshal.List(One(v), 0, "GetListLength");
        Assert.NotNull(back);
        Assert.Equal(0, back.Length);
    }

    [Fact]
    public void AListKeepsItsElementsOfEveryTypeInOrder()
    {
        Value v = Value.List(Value.Int(7), Value.Num(2.5), Value.Str("s"), Value.Vec(1, 2, 3), Value.Rot(1, 2, 3, 4), Value.Bool(true));
        LSLList list = LslMarshal.List(One(v), 0, "List2String");
        Assert.NotNull(list);
        Assert.Equal(6, list.Length);
        Assert.Equal(7, Assert.IsType<int>(list.Data[0]));
        Assert.Equal(2.5f, Assert.IsType<float>(list.Data[1]));
        Assert.Equal("s", Assert.IsType<string>(list.Data[2]));
        Assert.Equal(new Vector3(1, 2, 3), Assert.IsType<Vector3>(list.Data[3]));
        Assert.Equal(new Quaternion(1, 2, 3, 4), Assert.IsType<Quaternion>(list.Data[4]));
        Assert.Equal(1, Assert.IsType<int>(list.Data[5]));
    }

    [Fact]
    public void AKeyInAListIsAStringElementAsPhloxHoldsIt()
    {
        LSLList list = LslMarshal.List(One(Value.List(Value.Key("6f1b6c0a-1d3e-4f4a-9a43-0c6a4a8e9d11"))), 0, "List2Key");
        Assert.NotNull(list);
        Assert.Equal("6f1b6c0a-1d3e-4f4a-9a43-0c6a4a8e9d11", Assert.IsType<string>(list.Data[0]));
    }

    [Fact]
    public void AListReturnedByTheApiBecomesValuesOfTheMatchingTypes()
    {
        var list = new LSLList(new object[] { 7, 2.5f, "s", new Vector3(1, 2, 3), new Quaternion(1, 2, 3, 4) });
        Value v = LslMarshal.Of(list);
        Assert.NotNull(v.Items);
        Assert.Equal(
            new[] { Bindings.ValueType.Int, Bindings.ValueType.Number, Bindings.ValueType.String, Bindings.ValueType.Vector, Bindings.ValueType.Rotation },
            v.Items.Select(i => i.Type).ToArray());
        Assert.Equal(7, v.Items[0].I);
        Assert.Equal(2.5, v.Items[1].N);
        Assert.Equal("s", v.Items[2].S);
        Assert.Equal((1f, 2f, 3f), (v.Items[3].X, v.Items[3].Y, v.Items[3].Z));
        Assert.Equal((1f, 2f, 3f, 4f), (v.Items[4].X, v.Items[4].Y, v.Items[4].Z, v.Items[4].W));
    }

    [Fact]
    public void AListHoldingAListIsRejectedBothWays()
    {
        Assert.Throws<ArgumentException>(() => LslMarshal.List(One(Value.List(Value.List(Value.Int(1)))), 0, "List2String"));
        Assert.Throws<ArgumentException>(() => LslMarshal.Of(new LSLList(new object[] { new LSLList() })));
    }

    [Fact]
    public void ALargeListRoundTripsInOrder()
    {
        Value[] items = Enumerable.Range(0, 5000).Select(i => Value.Int(i)).ToArray();
        LSLList list = LslMarshal.List(One(Value.List(items)), 0, "GetListLength");
        Assert.NotNull(list);
        Assert.Equal(5000, list.Length);
        Value back = LslMarshal.Of(list);
        Assert.Equal(5000, back.Items.Length);
        Assert.Equal(4999, back.Items[4999].I);
    }

    // ---- wrong arguments ----

    [Fact]
    public void AWrongTypeIsAnErrorThatNamesTheFunctionAndTheArgument()
    {
        var e = Assert.Throws<ArgumentException>(() => LslMarshal.Int(new[] { Value.Int(0), Value.Str("x") }, 1, "Say"));
        Assert.Contains("Say", e.Message);
        Assert.Contains("#2", e.Message);
        Assert.Contains("integer", e.Message);
    }

    [Fact]
    public void AVectorParameterRejectsARotation()
    {
        Assert.Throws<ArgumentException>(() => LslMarshal.Vector(One(Value.Rot(1, 2, 3, 4)), 0, "VecMag"));
    }

    [Fact]
    public void TooFewArgumentsIsAnErrorAndExtraArgumentsAreIgnored()
    {
        var e = Assert.Throws<ArgumentException>(() => LslMarshal.Arity(new[] { Value.Int(0) }, 2, "Say"));
        Assert.Contains("Say", e.Message);
        LslMarshal.Arity(new[] { Value.Int(0), Value.Str("x"), Value.Int(9) }, 2, "Say");
    }
}
