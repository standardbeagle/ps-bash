using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>Orders below were recorded from bash 5.2 (WSL Ubuntu-24.04): `declare -A m; m[k]=1 ...; echo "${!m[@]}"`.</summary>
public class BashAssocTests
{
    private static BashAssoc Of(params string[] keys)
    {
        var t = new BashAssoc();
        foreach (var k in keys) t[k] = "1";
        return t;
    }

    [Fact] public void Keys_FollowBashHashOrder_NotInsertionOrder()
        => Assert.Equal(new[] { "A", "y", "x", "b", "a" }, Of("x", "y", "b", "a", "A").OrderedKeys());

    [Fact] public void Keys_NonAsciiAndMixed_MatchBash()
        => Assert.Equal(
            new[] { "日本", "1", "0", "Z", "z", "10", "a b", "_x", "ñ", "ü", "ключ", "é" },
            Of("é", "ü", "ñ", "a b", "ключ", "日本", "z", "Z", "0", "1", "10", "_x").OrderedKeys());

    [Fact] public void Keys_AreCaseSensitive()
    {
        var t = Of("a", "A");
        Assert.Equal(2, t.Count);
        Assert.True(t.ContainsKey("A"));
        Assert.False(t.ContainsKey("b"));
    }

    [Fact] public void Reassigning_AKey_KeepsItsPlace_RemoveThenAdd_GoesFirstInItsBucket()
    {
        var t = Of("z", "Z", "10");
        var before = t.OrderedKeys();
        t["z"] = "2";
        Assert.Equal(before, t.OrderedKeys());
        t.Remove("z");
        t["z"] = "3";
        t["Q"] = "3";
        Assert.Equal(new[] { "Z", "Q", "z", "10" }, t.OrderedKeys().Where(k => k is "Z" or "Q" or "z" or "10").ToArray());
    }

    [Fact] public void ValuesAndEnumeration_UseTheSameOrderAsKeys()
    {
        var t = new BashAssoc();
        foreach (var k in new[] { "x", "y", "b", "a", "A" }) t[k] = "v" + k;
        var viaEnum = new List<string>();
        foreach (System.Collections.DictionaryEntry e in t) viaEnum.Add((string)e.Key);
        Assert.Equal(t.OrderedKeys(), viaEnum);
        Assert.Equal(t.OrderedKeys().Select(k => "v" + k), t.Values.Cast<object>().Select(v => (string)v));
    }

    [Fact] public void Clone_KeepsOrderAndIsIndependent()
    {
        var t = Of("x", "y", "b", "a", "A");
        var c = (BashAssoc)t.Clone();
        Assert.Equal(t.OrderedKeys(), c.OrderedKeys());
        c["new"] = "1";
        Assert.False(t.ContainsKey("new"));
    }

    [Fact] public void FromPairs_ReplayOfReplayOrder_RebuildsTheSameOrder()
    {
        var t = Of("é", "ü", "ñ", "z", "Z", "0", "1", "10", "_x");
        var args = t.ReplayOrder().SelectMany(kv => new object?[] { kv.Key, kv.Value }).ToArray();
        Assert.Equal(t.OrderedKeys(), BashAssoc.FromPairs(args).OrderedKeys());
    }

    [Fact] public void Merge_AddsNewKeys_ReplacesValues_KeepsPlaces()
    {
        var t = Of("apple", "orange");
        var added = new BashAssoc(); added["lemon"] = "y"; added["apple"] = "g";
        var merged = BashAssoc.Merge(t, added);
        Assert.Same(t, merged);
        Assert.Equal(3, t.Count);
        Assert.Equal("g", t["apple"]);
    }

    [Fact] public void FromWords_PairsWordsAndGivesALoneLastKeyAnEmptyValue()
    {
        var t = BashAssoc.FromWords(new object?[] { "1", "2", "3" });
        Assert.Equal("2", t["1"]);
        Assert.Equal("", t["3"]);
    }
}