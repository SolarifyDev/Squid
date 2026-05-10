using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Shouldly;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Variables;

/// <summary>
/// Drift detector for <see cref="VariableDto"/>'s copy-constructor.
///
/// <para><b>Why this test exists</b>: pre-fix, <c>ExecuteStepsPhase.EncryptIfSensitive</c>
/// hand-copied 14 fields from a <see cref="VariableDto"/> source into a new
/// instance with an encrypted Value. Future fields added to
/// <see cref="VariableDto"/> would silently NOT be copied — no compiler
/// warning, no test failure, just a silent loss in checkpoint round-trips.
/// The same fragility applies to any other code path that clones the DTO.</para>
///
/// <para><b>The fix</b>: <see cref="VariableDto.VariableDto(VariableDto)"/>
/// copy-constructor centralises the field-by-field copy. The reflection-based
/// test below enumerates EVERY public read-write property on the DTO and
/// asserts the copy preserves it. When a contributor adds a new property,
/// they get one of two outcomes:
/// <list type="bullet">
///   <item>They updated the copy-ctor → this test passes silently.</item>
///   <item>They forgot → this test fails with a message naming the missing
///         property and pointing at the copy-ctor as the fix site.</item>
/// </list>
/// This is the "drift detector" pattern from <c>~/.claude/CLAUDE.md</c> Rule
/// 12.5 applied to a copy-constructor.</para>
///
/// <para>Note on Scopes: the copy-ctor does a <i>shallow</i> copy of the
/// <c>Scopes</c> list (shares reference). The shallow-copy assertion in this
/// test is intentional — callers that need an independent list must reassign
/// post-clone, and the doc-comment on the ctor flags that contract.</para>
/// </summary>
public sealed class VariableDtoCopyConstructorTests
{
    [Fact]
    public void CopyConstructor_NullSource_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new VariableDto(null));
    }

    [Fact]
    public void CopyConstructor_PreservesAllPublicProperties()
    {
        // Distinct, type-appropriate, non-default values for every R+W
        // property. Reflection-driven so future fields are auto-checked.
        var original = MakeFullyPopulatedDto();

        var copy = new VariableDto(original);

        var failures = new List<string>();
        foreach (var prop in WritableProperties())
        {
            var originalValue = prop.GetValue(original);
            var copyValue = prop.GetValue(copy);

            if (!Equals(originalValue, copyValue))
            {
                failures.Add(
                    $"property '{prop.Name}' (type {prop.PropertyType.Name}): " +
                    $"original={Render(originalValue)} copy={Render(copyValue)}. " +
                    $"FIX: add `{prop.Name} = other.{prop.Name};` to VariableDto's copy-constructor.");
            }
        }

        failures.ShouldBeEmpty(
            customMessage: "Copy-constructor missed " + failures.Count + " field(s):\n  • " +
                          string.Join("\n  • ", failures));
    }

    [Fact]
    public void CopyConstructor_ScopesIsSharedReference_DocumentedShallowCopyContract()
    {
        // Documented behaviour: Scopes is shallow-copied. If you change the
        // ctor to deep-copy, update the doc-comment AND this test.
        var original = MakeFullyPopulatedDto();

        var copy = new VariableDto(original);

        copy.Scopes.ShouldBeSameAs(original.Scopes,
            customMessage: "VariableDto copy-ctor documents shallow copy of Scopes. " +
                          "If you change to deep-copy, update both the doc-comment AND this test.");
    }

    [Fact]
    public void CopyConstructor_SourceMutationAfterClone_DoesNotAffectClone_ForValueTypes()
    {
        // Sanity: scalar fields are independent after copy. Only the Scopes
        // list reference is shared (covered by the test above).
        var original = MakeFullyPopulatedDto();
        var copy = new VariableDto(original);

        var originalName = copy.Name;
        var originalValue = copy.Value;
        var originalIsSensitive = copy.IsSensitive;

        original.Name = "mutated-after-clone";
        original.Value = "different-value";
        original.IsSensitive = !original.IsSensitive;

        copy.Name.ShouldBe(originalName);
        copy.Value.ShouldBe(originalValue);
        copy.IsSensitive.ShouldBe(originalIsSensitive);
    }

    [Fact]
    public void CopyConstructor_AllowsValueOverrideViaObjectInitializer()
    {
        // The encrypt-for-checkpoint use case writes
        // `new VariableDto(v) { Value = encryptedText }` — confirm the
        // initializer-after-ctor pattern works as expected.
        var original = MakeFullyPopulatedDto();

        var copy = new VariableDto(original) { Value = "OVERRIDE" };

        copy.Value.ShouldBe("OVERRIDE");
        copy.Name.ShouldBe(original.Name);   // every other field still preserved
        copy.IsSensitive.ShouldBe(original.IsSensitive);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static IEnumerable<PropertyInfo> WritableProperties()
    {
        return typeof(VariableDto)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.CanWrite);
    }

    private static VariableDto MakeFullyPopulatedDto()
    {
        var dto = new VariableDto();

        foreach (var prop in WritableProperties())
        {
            var value = MakeDistinctValueFor(prop);
            prop.SetValue(dto, value);
        }

        return dto;
    }

    private static object MakeDistinctValueFor(PropertyInfo prop)
    {
        var t = prop.PropertyType;

        // Use the property name where possible to make failure messages
        // informative (e.g. "Description" got "test-description").
        if (t == typeof(string)) return $"test-{prop.Name.ToLowerInvariant()}";
        if (t == typeof(int)) return 42;
        if (t == typeof(int?)) return (int?)17;
        if (t == typeof(bool)) return true;
        if (t == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 5, 10, 12, 34, 56, TimeSpan.Zero);
        if (t == typeof(VariableType)) return VariableType.String;
        if (t == typeof(List<VariableScopeDto>)) return new List<VariableScopeDto> { new() };

        throw new InvalidOperationException(
            $"VariableDtoCopyConstructorTests: add a MakeDistinctValueFor case for type {t.FullName} " +
            $"(needed for new property {prop.Name}). This is a one-line addition; the new field is " +
            $"otherwise auto-covered by the drift detector.");
    }

    private static string Render(object value) =>
        value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            _ => value.ToString()
        };
}
