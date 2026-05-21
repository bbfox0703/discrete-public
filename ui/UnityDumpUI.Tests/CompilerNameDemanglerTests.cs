// CompilerNameDemanglerTests — pin the patterns we recognise. If a
// future C# compiler version changes its scheme, these tests fail loud
// instead of silently regressing into ugly raw names in the UI.

using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class CompilerNameDemanglerTests
{
    // ---- single-segment Demangle --------------------------------------------

    [Theory]
    [InlineData("",                            "")]
    [InlineData("PlainClass",                  "PlainClass")]
    [InlineData("Already_Underscored",         "Already_Underscored")]
    [InlineData("Foo`1",                       "Foo`1")]            // generic arity untouched
    [InlineData("Foo[]",                       "Foo[]")]            // array sigil untouched
    public void Demangle_passes_through_clean_names(string raw, string expected)
        => Assert.Equal(expected, CompilerNameDemangler.Demangle(raw));

    [Theory]
    // From the user's Brigandine report
    [InlineData("<CreateUnitResult>d__41",     "CreateUnitResult [async 41]")]
    [InlineData("<_LevelUpProduction>d__12",   "_LevelUpProduction [async 12]")]
    // Standard Roslyn output
    [InlineData("<>c",                         "[lambda cache]")]
    [InlineData("<>O",                         "[delegate cache]")]
    [InlineData("<>c__DisplayClass5_0",        "[closure 5_0]")]
    [InlineData("<>c__DisplayClass12",         "[closure 12]")]
    [InlineData("<>9__5_0",                    "[lambda field 5_0]")]
    [InlineData("<>9",                         "[lambda field]")]
    [InlineData("<>1__state",                  "[state]")]
    [InlineData("<>2__current",                "[current]")]
    [InlineData("<>4__this",                   "[outer this]")]
    [InlineData("<>5__1",                      "[1]")]
    [InlineData("<DoWork>b__0",                "DoWork.lambda_0")]
    [InlineData("<DoWork>b__5_0",              "DoWork.lambda_5_0")]
    [InlineData("<DoWork>g__Local|3_0",        "DoWork.Local [local fn]")]
    [InlineData("<Name>k__BackingField",       "Name [backing]")]
    public void Demangle_recognises_known_patterns(string raw, string expected)
        => Assert.Equal(expected, CompilerNameDemangler.Demangle(raw));

    // ---- nested-class path --------------------------------------------------

    [Theory]
    [InlineData("PlainClass",                  "PlainClass")]
    [InlineData("Outer/Inner",                 "Outer / Inner")]
    [InlineData("Outer+Inner",                 "Outer / Inner")]  // accept reflection separator
    [InlineData(
        "Menu_WorldQuestResult/<CreateUnitResult>d__41",
        "Menu_WorldQuestResult / CreateUnitResult [async 41]")]
    [InlineData(
        "BattleUnitAction+<_LevelUpProduction>d__12",
        "BattleUnitAction / _LevelUpProduction [async 12]")]
    [InlineData(
        "Outer/<Async>d__1/<>c__DisplayClass0_0",
        "Outer / Async [async 1] / [closure 0_0]")]
    public void DemangleClassPath_handles_nested(string raw, string expected)
        => Assert.Equal(expected, CompilerNameDemangler.DemangleClassPath(raw));

    // ---- origin description -------------------------------------------------

    [Theory]
    [InlineData("PlainClass",                  null)]
    [InlineData("<CreateUnitResult>d__41",     "async / iterator state machine for 'CreateUnitResult'")]
    [InlineData("<>c",                         "static lambda cache class")]
    [InlineData("<>c__DisplayClass5_0",        "closure capture class")]
    [InlineData("<DoWork>b__0",                "lambda body inside 'DoWork'")]
    [InlineData("<DoWork>g__Local|3_0",        "local function inside 'DoWork'")]
    [InlineData("<Name>k__BackingField",       "auto-property backing field for 'Name'")]
    [InlineData("<>1__state",                  "compiler-generated state field")]
    public void DescribeOrigin_returns_expected(string raw, string? expected)
        => Assert.Equal(expected, CompilerNameDemangler.DescribeOrigin(raw));

    // ---- safety: no regression on weird input -------------------------------

    [Theory]
    [InlineData("<>",                          "<>")]              // empty inner+tail
    [InlineData("<x>",                         "<x>")]             // no tail kind
    [InlineData("<x>zzz__99",                  "<x>zzz__99")]      // unknown tail
    [InlineData("ContainsLessThan<butnotpattern>",
                "ContainsLessThan<butnotpattern>")]
    public void Demangle_falls_back_to_raw_on_unknown(string raw, string expected)
        => Assert.Equal(expected, CompilerNameDemangler.Demangle(raw));
}
