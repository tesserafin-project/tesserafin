using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Reefin.Playback.Contract.Diagnostics;
using Xunit;

namespace Reefin.Playback.Shadow.Tests;

/// <summary>
/// Issue #75's structural acceptance criterion, mechanised: "Type de sortie ne permettant
/// structurellement pas de transporter une chaîne d'origine cliente (enums et entiers uniquement)."
/// </summary>
/// <remarks>
/// <para>
/// MECHANISM. <see cref="Walk"/> starts at <see cref="ContractMappingDiagnostic"/> and computes the
/// TRANSITIVE closure of every type reachable from it: for each type it visits, it takes every
/// public instance property and every public constructor parameter, unwraps
/// <see cref="Nullable{T}"/> and any single-argument generic collection
/// (<c>IReadOnlyList&lt;T&gt;</c> and friends), and recurses into whatever is left. A type survives
/// only if it is (a) <see cref="bool"/>/<see cref="int"/>/<see cref="long"/>, (b) an enum, or
/// (c) declared in the <c>Reefin.Playback.Contract.Diagnostics</c> assembly itself - in which case
/// it is queued for the same treatment rather than trusted. Anything else - <see cref="string"/>,
/// <see cref="Guid"/>, <see cref="object"/>, a dictionary, <c>JsonElement</c>, <c>byte[]</c>, or a
/// type from any other assembly - fails the walk, naming the exact member that introduced it.
/// </para>
/// <para>
/// SCOPE. The root is <see cref="ContractMappingDiagnostic"/> and nothing else. It is deliberately
/// NOT <c>ShadowDiagnosticRecord</c> or <c>PlaybackDiagnosticDetail</c>: those legitimately carry
/// <c>Capabilities</c> and <c>PlaybackAttemptId</c>, which echo client-supplied data by design and
/// are catalogued for separate treatment in issue #80. Widening this walk to them would make it
/// fail for reasons issue #75 explicitly does not own.
/// </para>
/// <para>
/// This test is one of TWO independent guards. The other is
/// <c>src/Reefin.Playback.Contract.Diagnostics/BannedSymbols.txt</c>, which fails the BUILD (RS0030)
/// if a banned type is ever named in that assembly's source at all. This test is the one that also
/// catches a type arriving indirectly - through a member whose own declaration is elsewhere.
/// </para>
/// </remarks>
public sealed class ContractClosureTests
{
    private static readonly Assembly _contractAssembly = typeof(ContractMappingDiagnostic).Assembly;

    private static readonly HashSet<Type> _allowedLeaves =
    [
        typeof(bool),
        typeof(int),
        typeof(long),
    ];

    /// <summary>
    /// The whole point: no type outside the closed vocabulary is reachable from the diagnostic.
    /// </summary>
    [Fact]
    public void ContractMappingDiagnostic_TransitiveClosure_CarriesNothingButEnumsAndIntegers()
    {
        var violations = Walk(typeof(ContractMappingDiagnostic));

        Assert.True(
            violations.Count == 0,
            "Issue #75: the ContractMappingDiagnostic closure must contain only enums, bools and integers. Violations:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// Proves the walk actually rejects, rather than passing because it inspects nothing. Each of
    /// these is a type issue #75 names as forbidden; feeding one to the walker as a root must
    /// produce at least one violation.
    /// </summary>
    /// <param name="forbiddenHolderIndex">Which negative-control shape to feed the walker.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Walk_RejectsTheTypesIssue75Forbids(int forbiddenHolderIndex)
    {
        Type root = forbiddenHolderIndex switch
        {
            0 => typeof(StringHolder),
            1 => typeof(GuidHolder),
            2 => typeof(ObjectHolder),
            3 => typeof(DictionaryHolder),
            4 => typeof(BytesHolder),
            5 => typeof(JsonElementHolder),
            6 => typeof(CharHolder),
            7 => typeof(JsonNodeHolder),
            _ => throw new ArgumentOutOfRangeException(nameof(forbiddenHolderIndex)),
        };

        Assert.NotEmpty(Walk(root));
    }

    /// <summary>
    /// The closure guarantee is worth nothing if the walker never reaches the members that matter,
    /// so pin the set of types it actually visited.
    /// </summary>
    [Fact]
    public void Walk_ReachesEveryTypeOfTheClosedVocabulary()
    {
        var visited = new HashSet<Type>();
        Walk(typeof(ContractMappingDiagnostic), visited);

        Assert.Contains(typeof(ContractMappingDiagnostic), visited);
        Assert.Contains(typeof(ContractMappingDelta), visited);
        Assert.Contains(typeof(ContractFieldIssue), visited);
        Assert.Contains(typeof(ContractPath), visited);
        Assert.Contains(typeof(ContractMember), visited);
        Assert.Contains(typeof(ContractIssueCode), visited);
    }

    private static List<string> Walk(Type root, HashSet<Type>? visited = null)
    {
        visited ??= [];
        var violations = new List<string>();
        var queue = new Queue<Type>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!visited.Add(type))
            {
                continue;
            }

            foreach (var (memberName, memberType) in Members(type))
            {
                var unwrapped = Unwrap(memberType);
                if (unwrapped is null)
                {
                    // An array, or a multi-argument generic (a dictionary is the case that matters):
                    // both are forbidden outright, so there is nothing to unwrap into.
                    violations.Add(Describe(type, memberName, memberType));
                    continue;
                }

                if (_allowedLeaves.Contains(unwrapped) || unwrapped.IsEnum)
                {
                    // Terminal: an enum's own members are its named constants, so there is nothing
                    // further to walk into. Still recorded as reached, so the coverage test below
                    // can prove the walk got to it rather than silently skipping the branch.
                    visited.Add(unwrapped);
                    continue;
                }

                if (unwrapped.Assembly == _contractAssembly)
                {
                    queue.Enqueue(unwrapped);
                    continue;
                }

                violations.Add(Describe(type, memberName, memberType));
            }
        }

        return violations;
    }

    private static IEnumerable<(string Name, Type Type)> Members(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // EqualityContract is compiler-generated on every record and is always System.Type; it
            // is not a data member and carries nothing.
            if (string.Equals(property.Name, "EqualityContract", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (property.Name, property.PropertyType);
        }

        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return (parameter.Name ?? "?", parameter.ParameterType);
            }
        }
    }

    /// <summary>
    /// Peels <see cref="Nullable{T}"/> and single-argument generic collections down to the type that
    /// actually carries data. Returns <see langword="null"/> for a shape that cannot be peeled and is
    /// forbidden anyway: an array (<c>byte[]</c> is the case issue #75 names) or a multi-argument
    /// generic (every dictionary shape).
    /// </summary>
    private static Type? Unwrap(Type type)
    {
        while (true)
        {
            if (type.IsArray)
            {
                return null;
            }

            if (!type.IsGenericType)
            {
                return type;
            }

            var arguments = type.GetGenericArguments();
            if (arguments.Length != 1)
            {
                return null;
            }

            type = arguments[0];
        }
    }

    private static string Describe(Type owner, string memberName, Type memberType) =>
        string.Create(CultureInfo.InvariantCulture, $"  {owner.Name}.{memberName} is {memberType} - not an enum, bool, int, long, or a type of the closed vocabulary assembly.");

    private sealed record StringHolder(string Value);

    private sealed record GuidHolder(Guid Value);

    private sealed record ObjectHolder(object Value);

    private sealed record DictionaryHolder(IReadOnlyDictionary<int, int> Value);

    private sealed record BytesHolder(byte[] Value);

    private sealed record JsonElementHolder(System.Text.Json.JsonElement Value);

    private sealed record CharHolder(char Value);

    private sealed record JsonNodeHolder(System.Text.Json.Nodes.JsonNode Value);
}
